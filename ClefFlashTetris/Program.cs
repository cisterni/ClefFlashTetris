using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddProblemDetails();
builder.Services.AddSingleton<DecisionAuditLog>();
builder.Services.AddHttpClient<OllamaDecisionClient>(client =>
{
    client.Timeout = TimeSpan.FromMinutes(5);
});

var app = builder.Build();

app.UseExceptionHandler();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/status", async (OllamaDecisionClient client, CancellationToken cancellationToken) =>
    Results.Ok(await client.GetStatusAsync(cancellationToken)));

app.MapPost("/api/decision", async (DecisionRequest request, OllamaDecisionClient client, CancellationToken cancellationToken) =>
{
    if (request.Board is not { Length: 20 } || request.Board.Any(row => row is null || row.Length != 10))
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            [nameof(request.Board)] = ["The board must contain 20 rows of 10 cells each."]
        });
    }

    if (request.Candidates is not { Count: > 0 and <= 64 })
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            [nameof(request.Candidates)] = ["Between 1 and 64 candidate moves are required."]
        });
    }

    if (request.Candidates.Any(candidate => string.IsNullOrWhiteSpace(candidate.Id) ||
                                            candidate.Id.Length > 100 ||
                                            candidate.Id.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '_' and not '.' and not '-')))
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            [nameof(request.Candidates)] = ["Each candidate must have a simple ID (letters, numbers, _, . or -)."]
        });
    }

    var decision = await client.DecideAsync(request, cancellationToken);
    return Results.Ok(decision);
});

app.MapFallbackToFile("index.html");
app.Run();

public sealed record DecisionRequest(int[][] Board, string Piece, int Score, int Lines, IReadOnlyList<MoveCandidate> Candidates);

public sealed record MoveCandidate(
    string Id,
    int Rotation,
    int X,
    int LandingY,
    int ClearedLines,
    int Holes,
    int AggregateHeight,
    int Bumpiness,
    int MaxHeight);

public sealed record DecisionResponse(
    string Move,
    double Confidence,
    IReadOnlyDictionary<string, double> Probabilities,
    long LatencyMs,
    string Source,
    string Model,
    string? Note = null);

public sealed record ModelStatus(string Model, string Provider, string Endpoint, string DecisionLog, bool Available, bool ModelAvailable, string Mode, string? Note = null);

public sealed class OllamaDecisionClient(HttpClient httpClient, IConfiguration configuration, DecisionAuditLog auditLog, ILogger<OllamaDecisionClient> logger)
{
    private const int MaxChoiceOptions = 26;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private string Model => Environment.GetEnvironmentVariable("OLLAMA_MODEL") ?? configuration["Ollama:Model"] ?? "clef-flash";
    private string BaseUrl => (Environment.GetEnvironmentVariable("OLLAMA_BASE_URL") ?? configuration["Ollama:BaseUrl"] ?? "http://localhost:11434").TrimEnd('/');

    public async Task<ModelStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await httpClient.GetAsync($"{BaseUrl}/api/tags", cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new ModelStatus(Model, "Ollama", BaseUrl, auditLog.DisplayPath, false, false, "local-fallback", $"Ollama HTTP {(int)response.StatusCode}");
            }

            var json = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, cancellationToken);
            var modelAvailable = json.TryGetProperty("models", out var models) && models.EnumerateArray().Any(item =>
            {
                var name = item.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null;
                var model = item.TryGetProperty("model", out var modelElement) ? modelElement.GetString() : null;
                return MatchesModel(name) || MatchesModel(model);
            });

            return new ModelStatus(
                Model,
                "Ollama",
                BaseUrl,
                auditLog.DisplayPath,
                true,
                modelAvailable,
                modelAvailable ? "ollama" : "local-fallback",
                modelAvailable ? null : $"Model {Model} is not installed. Run: ollama pull {Model}");
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            logger.LogDebug(exception, "Ollama is unavailable at {BaseUrl}", BaseUrl);
            return new ModelStatus(Model, "Ollama", BaseUrl, auditLog.DisplayPath, false, false, "local-fallback", "Ollama is unreachable.");
        }

        bool MatchesModel(string? installed) =>
            installed is not null && (installed.Equals(Model, StringComparison.OrdinalIgnoreCase) ||
                                      installed.StartsWith($"{Model}:", StringComparison.OrdinalIgnoreCase));
    }

    public async Task<DecisionResponse> DecideAsync(DecisionRequest request, CancellationToken cancellationToken)
    {
        var shortlistedCandidates = RankCandidates(request.Candidates)
            .Take(MaxChoiceOptions)
            .Select(item => item.Candidate)
            .ToArray();

        var criteria = shortlistedCandidates.ToDictionary(
            candidate => candidate.Id,
            candidate => $"Rotation {candidate.Rotation}, column {candidate.X}. Outcome: clearedLines={candidate.ClearedLines}, holes={candidate.Holes}, aggregateHeight={candidate.AggregateHeight}, bumpiness={candidate.Bumpiness}, maxHeight={candidate.MaxHeight}.");

        var state = new
        {
            game = "Tetris",
            objective = "Choose a legal placement that maximizes cleared lines while minimizing holes, maximum height, and surface bumpiness.",
            piece = request.Piece,
            score = request.Score,
            lines = request.Lines,
            board = request.Board.Select(row => string.Concat(row.Select(cell => cell == 0 ? '.' : '#'))).ToArray()
        };

        var payload = new
        {
            model = Model,
            state,
            questions = new Dictionary<string, object>
            {
                ["move"] = new
                {
                    type = "choice",
                    instructions = "Choose the best Tetris move among the allowed options. Give absolute priority to cleared lines, avoid holes, then reduce maximum height, aggregate height, and bumpiness.",
                    criteria
                }
            },
            keep_alive = "10m"
        };

        using var message = new HttpRequestMessage(
            HttpMethod.Post,
            $"{BaseUrl}/v1/systemone");
        message.Content = JsonContent.Create(payload, options: JsonOptions);

        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var response = await httpClient.SendAsync(message, cancellationToken);
            var rawResponse = await response.Content.ReadAsStringAsync(cancellationToken);
            stopwatch.Stop();

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Ollama returned HTTP {StatusCode}", (int)response.StatusCode);
                var note = $"Ollama HTTP {(int)response.StatusCode}; used local fallback.";
                await auditLog.WriteAsync(Model, $"{BaseUrl}/v1/systemone", payload, (int)response.StatusCode, stopwatch.ElapsedMilliseconds, rawResponse, note, null);
                return LocalDecision(request.Candidates, note, stopwatch.ElapsedMilliseconds);
            }

            var json = JsonSerializer.Deserialize<JsonElement>(rawResponse, JsonOptions);
            var moveAnswer = json.GetProperty("answers").GetProperty("move");
            var choice = moveAnswer.GetProperty("choice").GetString();
            var confidence = moveAnswer.TryGetProperty("confidence", out var confidenceElement) ? confidenceElement.GetDouble() : 0;
            var probabilities = moveAnswer.GetProperty("probabilities")
                .EnumerateObject()
                .ToDictionary(property => property.Name, property => property.Value.GetDouble());

            if (choice is null || shortlistedCandidates.All(candidate => candidate.Id != choice))
            {
                const string note = "Ollama returned an invalid choice; used local fallback.";
                await auditLog.WriteAsync(Model, $"{BaseUrl}/v1/systemone", payload, (int)response.StatusCode, stopwatch.ElapsedMilliseconds, rawResponse, note, null);
                return LocalDecision(request.Candidates, note, stopwatch.ElapsedMilliseconds);
            }

            logger.LogInformation("Ollama/{Model}: {Move} ({Confidence:P1}) in {Latency} ms", Model, choice, confidence, stopwatch.ElapsedMilliseconds);
            await auditLog.WriteAsync(Model, $"{BaseUrl}/v1/systemone", payload, (int)response.StatusCode, stopwatch.ElapsedMilliseconds, rawResponse, null, null);
            return new DecisionResponse(choice, confidence, probabilities, stopwatch.ElapsedMilliseconds, "ollama", Model);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            stopwatch.Stop();
            logger.LogWarning(exception, "Ollama/{Model} decision unavailable; enabling local fallback", Model);
            const string note = "Ollama/Clef-Flash is unreachable; used local fallback.";
            await auditLog.WriteAsync(Model, $"{BaseUrl}/v1/systemone", payload, null, stopwatch.ElapsedMilliseconds, null, note, exception);
            return LocalDecision(request.Candidates, note, stopwatch.ElapsedMilliseconds);
        }
    }

    private static DecisionResponse LocalDecision(IReadOnlyList<MoveCandidate> candidates, string note, long latencyMs = 0)
    {
        var ranked = RankCandidates(candidates).ToArray();

        var max = ranked[0].Utility;
        var weights = ranked.ToDictionary(item => item.Candidate.Id, item => Math.Exp(Math.Clamp((item.Utility - max) / 40d, -30, 0)));
        var total = weights.Values.Sum();
        var probabilities = weights.ToDictionary(item => item.Key, item => item.Value / total);
        var choice = ranked[0].Candidate.Id;

        return new DecisionResponse(choice, probabilities[choice], probabilities, latencyMs, "local-fallback", "deterministic-heuristic", note);
    }

    private static IOrderedEnumerable<RankedCandidate> RankCandidates(IReadOnlyList<MoveCandidate> candidates) =>
        candidates
            .Select(candidate => new
            {
                Candidate = candidate,
                Utility = candidate.ClearedLines * 1000d - candidate.Holes * 120d - candidate.MaxHeight * 8d - candidate.AggregateHeight * 2.2d - candidate.Bumpiness * 1.8d
            })
            .Select(item => new RankedCandidate(item.Candidate, item.Utility))
            .OrderByDescending(item => item.Utility);

    private sealed record RankedCandidate(MoveCandidate Candidate, double Utility);
}

public partial class Program { }
