using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

public sealed class DecisionAuditLog
{
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly string _absolutePath;
    private readonly ILogger<DecisionAuditLog> _logger;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public DecisionAuditLog(IConfiguration configuration, IHostEnvironment environment, ILogger<DecisionAuditLog> logger)
    {
        _logger = logger;
        var configuredPath = configuration["Ollama:DecisionLogPath"] ?? "logs/ollama-decisions.jsonl";
        _absolutePath = Path.GetFullPath(
            Path.IsPathRooted(configuredPath)
                ? configuredPath
                : Path.Combine(environment.ContentRootPath, configuredPath));
        DisplayPath = Path.GetRelativePath(environment.ContentRootPath, _absolutePath).Replace('\\', '/');
    }

    public string DisplayPath { get; }

    public async Task WriteAsync(
        string model,
        string endpoint,
        object request,
        int? httpStatus,
        long latencyMs,
        string? rawResponse,
        string? fallback,
        Exception? exception)
    {
        object? response = rawResponse;
        if (!string.IsNullOrWhiteSpace(rawResponse))
        {
            try
            {
                response = JsonSerializer.Deserialize<JsonElement>(rawResponse, JsonOptions);
            }
            catch (JsonException)
            {
                // Preserve the original response body when Ollama does not return JSON.
            }
        }

        var entry = new
        {
            timestampUtc = DateTimeOffset.UtcNow,
            provider = "ollama",
            model,
            endpoint,
            httpStatus,
            latencyMs,
            request,
            response,
            fallback,
            error = exception is null ? null : new
            {
                type = exception.GetType().Name,
                message = exception.Message
            }
        };

        var line = JsonSerializer.Serialize(entry, JsonOptions) + Environment.NewLine;
        try
        {
            await _writeLock.WaitAsync();
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_absolutePath)!);
                await File.AppendAllTextAsync(_absolutePath, line, Encoding.UTF8);
            }
            finally
            {
                _writeLock.Release();
            }
        }
        catch (Exception logException) when (logException is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(logException, "Unable to write the decision log to {DecisionLogPath}", _absolutePath);
        }
    }
}
