[CmdletBinding()]
param(
    [string]$BaseUrl = "http://localhost:11434",
    [string]$Model = "clef-flash",

    [ValidateRange(1, 128)]
    [int]$Concurrency = 1,

    [ValidateRange(5, 3600)]
    [int]$DurationSeconds = 60,

    [ValidateRange(0, 100)]
    [int]$WarmupRequests = 1,

    [ValidateRange(1, 1800)]
    [int]$TimeoutSeconds = 300
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if ($PSVersionTable.PSVersion.Major -lt 7) {
    throw "This benchmark requires PowerShell 7 or later (pwsh)."
}

$endpoint = "{0}/v1/systemone" -f $BaseUrl.TrimEnd('/')
$payload = [ordered]@{
    model = $Model
    state = [ordered]@{
        workload = "Ollama decision throughput benchmark"
        requestId = "__REQUEST_ID__"
        signal = 42
    }
    questions = [ordered]@{
        decision = [ordered]@{
            type = "choice"
            instructions = "Choose the safer option. Return exactly one allowed option ID."
            criteria = [ordered]@{
                A = "Low risk, reversible, and inexpensive."
                B = "High risk, irreversible, and expensive."
            }
        }
    }
    keep_alive = "10m"
}
$requestTemplate = $payload | ConvertTo-Json -Depth 8 -Compress

function Test-DecisionResponse {
    param([string]$Json)

    $document = [System.Text.Json.JsonDocument]::Parse($Json)
    try {
        $choice = $document.RootElement.GetProperty("answers").GetProperty("decision").GetProperty("choice").GetString()
        return $choice -in @("A", "B")
    }
    finally {
        $document.Dispose()
    }
}

Write-Host "Target:      $endpoint"
Write-Host "Model:       $Model"
Write-Host "Concurrency: $Concurrency"
Write-Host "Duration:    $DurationSeconds seconds"

if ($WarmupRequests -gt 0) {
    Write-Host "Warming up with $WarmupRequests request(s)..."
    $warmupClient = [System.Net.Http.HttpClient]::new()
    $warmupClient.Timeout = [TimeSpan]::FromSeconds($TimeoutSeconds)
    try {
        for ($index = 1; $index -le $WarmupRequests; $index++) {
            $body = $requestTemplate.Replace("__REQUEST_ID__", "warmup-$index")
            $content = [System.Net.Http.StringContent]::new($body, [System.Text.Encoding]::UTF8, "application/json")
            try {
                $response = $warmupClient.PostAsync($endpoint, $content).GetAwaiter().GetResult()
                try {
                    $responseBody = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
                    if (-not $response.IsSuccessStatusCode) {
                        throw "Warm-up failed with HTTP $([int]$response.StatusCode): $responseBody"
                    }
                    if (-not (Test-DecisionResponse $responseBody)) {
                        throw "Warm-up returned HTTP 200 but no valid decision."
                    }
                }
                finally {
                    $response.Dispose()
                }
            }
            finally {
                $content.Dispose()
            }
        }
    }
    finally {
        $warmupClient.Dispose()
    }
}

Write-Host "Running benchmark..."
$wallClock = [System.Diagnostics.Stopwatch]::StartNew()

$results = 1..$Concurrency | ForEach-Object -ThrottleLimit $Concurrency -Parallel {
    $workerId = $_
    $workerEndpoint = $using:endpoint
    $workerRequestTemplate = $using:requestTemplate
    $client = [System.Net.Http.HttpClient]::new()
    $client.Timeout = [TimeSpan]::FromSeconds($using:TimeoutSeconds)
    $workerClock = [System.Diagnostics.Stopwatch]::StartNew()
    $requestNumber = 0

    try {
        while ($workerClock.Elapsed.TotalSeconds -lt $using:DurationSeconds) {
            $requestNumber++
            $requestId = "worker-$workerId-request-$requestNumber-$([Guid]::NewGuid().ToString('N'))"
            $body = $workerRequestTemplate.Replace("__REQUEST_ID__", $requestId)
            $content = [System.Net.Http.StringContent]::new($body, [System.Text.Encoding]::UTF8, "application/json")
            $requestClock = [System.Diagnostics.Stopwatch]::StartNew()

            try {
                $response = $client.PostAsync($workerEndpoint, $content).GetAwaiter().GetResult()
                try {
                    $responseBody = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
                    $validDecision = $false
                    if ($response.IsSuccessStatusCode) {
                        try {
                            $document = [System.Text.Json.JsonDocument]::Parse($responseBody)
                            try {
                                $choice = $document.RootElement.GetProperty("answers").GetProperty("decision").GetProperty("choice").GetString()
                                $validDecision = $choice -in @("A", "B")
                            }
                            finally {
                                $document.Dispose()
                            }
                        }
                        catch {
                            $validDecision = $false
                        }
                    }

                    [pscustomobject]@{
                        Success = $response.IsSuccessStatusCode -and $validDecision
                        LatencyMs = $requestClock.Elapsed.TotalMilliseconds
                        StatusCode = [int]$response.StatusCode
                        Error = if ($response.IsSuccessStatusCode -and -not $validDecision) { "Invalid decision response" } else { $null }
                    }
                }
                finally {
                    $response.Dispose()
                }
            }
            catch {
                [pscustomobject]@{
                    Success = $false
                    LatencyMs = $requestClock.Elapsed.TotalMilliseconds
                    StatusCode = 0
                    Error = $_.Exception.Message
                }
            }
            finally {
                $content.Dispose()
            }
        }
    }
    finally {
        $client.Dispose()
    }
}

$wallClock.Stop()
$successful = @($results | Where-Object Success)
$failed = @($results | Where-Object { -not $_.Success })
$latencies = @($successful | ForEach-Object LatencyMs | Sort-Object)
$elapsedMinutes = $wallClock.Elapsed.TotalMinutes

function Get-Percentile {
    param(
        [double[]]$SortedValues,
        [ValidateRange(0, 1)]
        [double]$Percentile
    )

    if ($SortedValues.Count -eq 0) {
        return 0
    }

    $index = [Math]::Max(0, [Math]::Ceiling($SortedValues.Count * $Percentile) - 1)
    return $SortedValues[$index]
}

$summary = [pscustomobject]@{
    Endpoint = $endpoint
    Model = $Model
    Concurrency = $Concurrency
    ElapsedSeconds = [Math]::Round($wallClock.Elapsed.TotalSeconds, 2)
    SuccessfulDecisions = $successful.Count
    FailedRequests = $failed.Count
    DecisionsPerMinute = if ($elapsedMinutes -gt 0) { [Math]::Round($successful.Count / $elapsedMinutes, 2) } else { 0 }
    AttemptedRequestsPerMinute = if ($elapsedMinutes -gt 0) { [Math]::Round($results.Count / $elapsedMinutes, 2) } else { 0 }
    MeanLatencyMs = if ($latencies.Count -gt 0) { [Math]::Round(($latencies | Measure-Object -Average).Average, 2) } else { 0 }
    P50LatencyMs = [Math]::Round((Get-Percentile $latencies 0.50), 2)
    P95LatencyMs = [Math]::Round((Get-Percentile $latencies 0.95), 2)
    P99LatencyMs = [Math]::Round((Get-Percentile $latencies 0.99), 2)
}

Write-Host ""
Write-Host "Results"
$summary | Format-List

if ($failed.Count -gt 0) {
    Write-Host "Failure samples"
    $failed |
        Group-Object StatusCode, Error |
        Sort-Object Count -Descending |
        Select-Object -First 5 Count, Name |
        Format-Table -AutoSize
    exit 2
}
