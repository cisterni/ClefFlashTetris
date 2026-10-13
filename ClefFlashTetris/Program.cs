using System.Diagnostics;
using System.Globalization;
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
    var inputMode = request.InputMode?.Trim().ToLowerInvariant() ?? "structured";
    if (inputMode is not ("structured" or "image" or "structured-no-metrics" or "image-no-metrics"))
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            [nameof(request.InputMode)] = ["Input mode must be 'structured', 'image', 'structured-no-metrics', or 'image-no-metrics'."]
        });
    }

    var usesImage = inputMode is "image" or "image-no-metrics";

    if (!usesImage &&
        (request.Board is not { Length: 20 } || request.Board.Any(row => row is null || row.Length != 10)))
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            [nameof(request.Board)] = ["The board must contain 20 rows of 10 cells each."]
        });
    }

    if (usesImage && !IsSupportedBoardImage(request.BoardImage, out var imageError))
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            [nameof(request.BoardImage)] = [imageError]
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

static bool IsSupportedBoardImage(string? base64Image, out string error)
{
    if (string.IsNullOrWhiteSpace(base64Image))
    {
        error = "A base64-encoded board image is required in image mode.";
        return false;
    }

    if (base64Image.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
    {
        error = "Send raw base64 image data without a data URL prefix.";
        return false;
    }

    const int maxEncodedLength = 12 * 1024 * 1024;
    if (base64Image.Length > maxEncodedLength)
    {
        error = "The board image is too large.";
        return false;
    }

    byte[] bytes;
    try
    {
        bytes = Convert.FromBase64String(base64Image);
    }
    catch (FormatException)
    {
        error = "The board image is not valid base64 data.";
        return false;
    }

    var isPng = bytes.Length >= 8 && bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
    var isJpeg = bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF;
    var isWebP = bytes.Length >= 12 && bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) && bytes.AsSpan(8, 4).SequenceEqual("WEBP"u8);

    if (!isPng && !isJpeg && !isWebP)
    {
        error = "The board image must be a PNG, JPEG, or WebP file.";
        return false;
    }

    error = string.Empty;
    return true;
}

public sealed record DecisionRequest(
    int[][]? Board,
    string Piece,
    int Score,
    int Lines,
    IReadOnlyList<MoveCandidate> Candidates,
    string InputMode = "structured",
    string? BoardImage = null);

public sealed record MoveCandidate(
    string Id,
    int Rotation,
    int X,
    int LandingY,
    int ClearedLines,
    int RotationCost,
    double LandingHeight,
    int ErodedPieceCells,
    int RowTransitions,
    int ColumnTransitions,
    int Holes,
    int HolesDelta,
    int CumulativeWells,
    int HoleDepth,
    int RowsWithHoles,
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
    private const double MinDecisionConfidence = 0.15;
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
        var inputMode = request.InputMode.Trim().ToLowerInvariant();
        var usesImage = inputMode is "image" or "image-no-metrics";
        var includesMetrics = inputMode is "structured" or "image";
        var safeCandidates = request.Candidates.Any(candidate => candidate.HolesDelta <= 0)
            ? request.Candidates.Where(candidate => candidate.HolesDelta <= 0).ToArray()
            : request.Candidates;
        var shortlistedCandidates = RankCandidates(safeCandidates)
            .Take(MaxChoiceOptions)
            .Select(item => item.Candidate)
            .ToArray();

        var criteria = shortlistedCandidates.ToDictionary(
            candidate => candidate.Id,
            candidate => includesMetrics
                ? $"BCTS_UTILITY={CalculateUtility(candidate).ToString("0.00", CultureInfo.InvariantCulture)} (higher is better); rotationCost={candidate.RotationCost}; rotation={candidate.Rotation}; column={candidate.X}; holesDelta={candidate.HolesDelta}; holes={candidate.Holes}; rowsWithHoles={candidate.RowsWithHoles}; holeDepth={candidate.HoleDepth}; maxHeight={candidate.MaxHeight}; landingHeight={candidate.LandingHeight.ToString("0.0", CultureInfo.InvariantCulture)}; columnTransitions={candidate.ColumnTransitions}; rowTransitions={candidate.RowTransitions}; cumulativeWells={candidate.CumulativeWells}; erodedPieceCells={candidate.ErodedPieceCells}."
                : $"Rotate the current {request.Piece} piece {candidate.Rotation} clockwise quarter-turn(s), then place the left edge of its rotated bounding box at zero-based column {candidate.X}.");

        object state = usesImage
            ? new
            {
                game = "Tetris",
                objective = includesMetrics
                    ? "Choose the afterstate with the best long-term survival and future placement flexibility. Use the attached 10 by 20 board image together with the BCTS-style candidate features."
                    : "Inspect the attached 10 by 20 board image, mentally simulate each allowed placement, and choose the afterstate with the best long-term survival and future placement flexibility.",
                piece = request.Piece,
                score = request.Score,
                lines = request.Lines,
                imageGuide = "The image shows the occupied board cells and the current colored piece at the top. Candidate columns are zero-based from the left."
            }
            : new
            {
                game = "Tetris",
                objective = includesMetrics
                    ? "Choose the afterstate with the best long-term survival and future placement flexibility using the BCTS-style candidate features."
                    : "Read the 10 by 20 text board, mentally simulate each allowed placement, and choose the afterstate with the best long-term survival and future placement flexibility.",
                piece = request.Piece,
                score = request.Score,
                lines = request.Lines,
                boardGuide = "Rows run from top to bottom; '#' is occupied and '.' is empty. Candidate columns are zero-based from the left.",
                board = request.Board!.Select(row => string.Concat(row.Select(cell => cell == 0 ? '.' : '#'))).ToArray()
            };

        var payload = new Dictionary<string, object?>
        {
            ["model"] = Model,
            ["state"] = state,
            ["questions"] = new Dictionary<string, object>
            {
                ["move"] = new
                {
                    type = "choice",
                    instructions = (usesImage, includesMetrics) switch
                    {
                        (true, true) => "Study the image, then choose the candidate with the greatest BCTS_UTILITY. Candidates that create avoidable holes have already been removed. Do not reinterpret or rebalance the component metrics. If BCTS_UTILITY values differ by less than 0.50, choose the lower rotationCost; prefer rotationCost=0. Return exactly one allowed candidate ID.",
                        (false, true) => "Choose the candidate with the greatest BCTS_UTILITY. Candidates that create avoidable holes have already been removed. Do not reinterpret or rebalance the component metrics. If BCTS_UTILITY values differ by less than 0.50, choose the lower rotationCost; prefer rotationCost=0. Return exactly one allowed candidate ID.",
                        (true, false) => "Study the board image and mentally simulate every allowed placement. Prefer moves that create no covered holes, keep the stack low and accessible, reduce deep wells and jagged surfaces, and preserve open columns. Do not rotate unless the rotated placement is structurally better; when outcomes appear equivalent, prefer the unrotated placement. Return exactly one allowed candidate ID.",
                        _ => "Read the text board and mentally simulate every allowed placement. Prefer moves that create no covered holes, keep the stack low and accessible, reduce deep wells and jagged surfaces, and preserve open columns. Do not rotate unless the rotated placement is structurally better; when outcomes appear equivalent, prefer the unrotated placement. Return exactly one allowed candidate ID."
                    },
                    criteria
                }
            },
            ["keep_alive"] = "10m"
        };

        if (usesImage)
        {
            payload["images"] = new[] { request.BoardImage! };
        }

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
                return LocalDecision(safeCandidates, note, stopwatch.ElapsedMilliseconds);
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
                return LocalDecision(safeCandidates, note, stopwatch.ElapsedMilliseconds);
            }

            var bestBctsCandidate = RankCandidates(safeCandidates).First().Candidate;
            if (confidence < MinDecisionConfidence && choice != bestBctsCandidate.Id)
            {
                var note = $"Ollama confidence {confidence:P1} is below {MinDecisionConfidence:P0} and its choice conflicts with the BCTS guardrail; used local fallback.";
                await auditLog.WriteAsync(Model, $"{BaseUrl}/v1/systemone", payload, (int)response.StatusCode, stopwatch.ElapsedMilliseconds, rawResponse, note, null);
                return LocalDecision(safeCandidates, note, stopwatch.ElapsedMilliseconds);
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
            return LocalDecision(safeCandidates, note, stopwatch.ElapsedMilliseconds);
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
            .Select(candidate => new RankedCandidate(candidate, CalculateUtility(candidate)))
            .OrderByDescending(item => item.Utility)
            .ThenBy(item => item.Candidate.RotationCost);

    private static double CalculateUtility(MoveCandidate candidate) =>
        candidate.ErodedPieceCells * 6.60d
        - candidate.RowsWithHoles * 24.04d
        - candidate.ColumnTransitions * 19.77d
        - candidate.Holes * 13.08d
        - candidate.LandingHeight * 12.63d
        - candidate.CumulativeWells * 10.49d
        - candidate.RowTransitions * 9.22d
        - candidate.HoleDepth * 1.61d;

    private sealed record RankedCandidate(MoveCandidate Candidate, double Utility);
}

public partial class Program { }
