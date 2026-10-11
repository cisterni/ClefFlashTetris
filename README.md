# Clef-Flash plays Tetris

A decision-model demo built for an article: an ASP.NET Core / .NET 10 web application featuring a playable Tetris game and an autopilot powered by `clef-flash` running locally through Ollama.

## Quick start

The default model requires Ollama **0.35.1 or later** and approximately 12 GB of storage:

```powershell
ollama pull clef-flash
dotnet run --project ClefFlashTetris/ClefFlashTetris.csproj
```

Open the address displayed by ASP.NET Core and select **Start autopilot**. By default, the demo connects to Ollama at `http://localhost:11434`.

If Ollama is not running or the model is not installed, the game remains playable and uses a deterministic local heuristic fallback that is clearly identified in the interface.

## Decision flow

1. The browser enumerates every legal placement for the current tetromino.
2. It simulates each landing and calculates cleared lines, holes, aggregate height, maximum height, and surface bumpiness.
3. The server shortlists at most 26 placements, the limit supported by Ollama for a `choice` question.
4. `POST /api/decision` sends the board and placements to the configured Ollama endpoint, appending `/v1/systemone`.
5. Ollama/Clef-Flash returns one allowed ID, its confidence, and a probability for each candidate.
6. The browser displays the distribution and applies the selected placement.

The fallback evaluates the same candidate moves, making the demo reproducible offline. The response's `source` field always distinguishes a model decision from a local one.

[`sample-request.json`](ClefFlashTetris/sample-request.json) contains a minimal request ready to send to `POST /api/decision` when demonstrating or testing the API.

## Prompt and response audit log

Every decision attempt is appended to `ClefFlashTetris/logs/ollama-decisions.jsonl`. Each line is an independent JSON object containing:

- the complete System One request (`request`), including state, instructions, and options;
- the raw Ollama response (`response`);
- model, endpoint, timestamp, HTTP status, and latency;
- any error and the reason the fallback was used.

The file is not exposed through `wwwroot`. Change its location with `Ollama:DecisionLogPath` in [`appsettings.json`](ClefFlashTetris/appsettings.json).

## Configuration

- Model: `clef-flash`
- Endpoint: Ollama System One API, `/v1/systemone`
- Settings: `Ollama:BaseUrl` and `Ollama:Model` in `appsettings.json`
- Audit log: `Ollama:DecisionLogPath`, defaulting to `logs/ollama-decisions.jsonl`
- Optional overrides: the `OLLAMA_BASE_URL` and `OLLAMA_MODEL` environment variables

To use a remote Ollama node for the current session:

```powershell
$env:OLLAMA_BASE_URL = "http://100.64.0.9:11434"
dotnet run --project ClefFlashTetris/ClefFlashTetris.csproj
```

Alternatively, pass the URL on the command line:

```powershell
dotnet run --project ClefFlashTetris/ClefFlashTetris.csproj -- --Ollama:BaseUrl=http://100.64.0.9:11434
```

- No API key is required, and no data is sent to cloud services.
- The server reduces candidate moves to the best 26 before asking the model to choose.
- Network errors, timeouts, or invalid responses trigger an explicit fallback and are recorded in the ASP.NET Core logs.

## Controls

- `←` / `→`: move
- `↑`: rotate
- `↓`: soft drop
- `Space`: hard drop
- `A`: toggle autopilot
