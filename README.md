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
2. It simulates each landing and calculates the eight BCTS features: landing height, eroded piece cells, row and column transitions, holes, cumulative wells, hole depth, and rows with holes.
3. The server shortlists at most 26 placements, the limit supported by Ollama for a `choice` question.
4. The selected input mode sends either the structured board or a base64-encoded PNG snapshot to `POST /api/decision`.
5. The server calls the configured Ollama endpoint, appending `/v1/systemone`.
6. Ollama/Clef-Flash returns one allowed ID, its confidence, and a probability for each candidate.
7. The browser displays the distribution and applies the selected placement.

The fallback evaluates the same candidate moves, making the demo reproducible offline. The response's `source` field always distinguishes a model decision from a local one.

[`sample-request.json`](ClefFlashTetris/sample-request.json) contains a minimal request ready to send to `POST /api/decision` when demonstrating or testing the API.

## Input modes

Use the **Model input** selector before starting the autopilot:

- **Text board + metrics** puts a text rendering of the board in the System One `state`, while every candidate includes its computed BCTS features.
- **Board image + metrics** captures the 300 × 600 canvas as a PNG and sends its raw base64 content in the System One `images` array together with the same candidate features.
- **Text board only** sends the text rendering and the rotation/column for each allowed move, but no computed candidate metrics.
- **Board image only** sends the PNG and the rotation/column for each allowed move, but no computed candidate metrics.

In both image modes, the browser request omits `board`; in both text modes it omits `boardImage`. The server validates the selected representation before calling Ollama. In the two modes without metrics, Clef-Flash must infer board quality directly from the text or image. The server still uses its metrics to shortlist legal moves, reject avoidable holes, and provide the low-confidence fallback, but those values are not included in the System One prompt. Clef and Clef Flash image decisions require Ollama 0.35.1 or later. See the official [decision guide](https://docs.ollama.com/capabilities/decision) and [System One API reference](https://docs.ollama.com/api/systemone).

## Evaluation policy

The prompt asks Clef-Flash to optimize long-term survival and future placement flexibility rather than immediate line clears alone. It applies a safety-first ordering: reject moves that create new holes when a zero-hole-delta alternative exists, then maximize the published BCTS utility. Rotation is explicitly a final tie-breaker, so an unrotated placement wins whenever the structural outcome is equivalent.

Because a decision model can still return a poorly calibrated numerical comparison, the server applies one transparent guardrail: when confidence is below 15% and the selected move conflicts with the best BCTS candidate, it uses the deterministic fallback and records the reason in the audit log. Confident model choices remain untouched.

The deterministic fallback and the server-side shortlist use the eight-feature BCTS linear policy documented in [Why Most Decisions Are Easy in Tetris](https://proceedings.mlr.press/v48/simsek16.html):

```text
utility =  6.60 * erodedPieceCells
         - 24.04 * rowsWithHoles
         - 19.77 * columnTransitions
         - 13.08 * holes
         - 12.63 * landingHeight
         - 10.49 * cumulativeWells
         -  9.22 * rowTransitions
         -  1.61 * holeDepth
```

This replaces the earlier ad-hoc combination of cleared lines, aggregate height, maximum height, holes, and bumpiness.

## Prompt and response audit log

Every decision attempt is appended to `ClefFlashTetris/logs/ollama-decisions.jsonl`. Each line is an independent JSON object containing:

- the complete System One request (`request`), including state, instructions, options, and the base64 image when image mode is selected;
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

## Ollama decision throughput benchmark

[`benchmark-ollama.ps1`](ClefFlashTetris/scripts/benchmark-ollama.ps1) measures completed System One decisions per minute and reports failures plus mean, p50, p95, and p99 latency. It requires PowerShell 7 or later.

Run a one-minute sequential test against local Ollama:

```powershell
pwsh ./ClefFlashTetris/scripts/benchmark-ollama.ps1
```

Test the remote Tailscale node with four concurrent requests for two minutes:

```powershell
pwsh ./ClefFlashTetris/scripts/benchmark-ollama.ps1 `
  -BaseUrl http://100.64.0.9:11434 `
  -Model clef-flash `
  -Concurrency 4 `
  -DurationSeconds 120
```

The warm-up request is excluded from the measurement. Each measured request contains a different ID to avoid benchmarking an identical cached prompt. A worker finishes its current in-flight request after the requested duration, so the reported elapsed time can be slightly longer than `DurationSeconds`.

- No API key is required, and no data is sent to cloud services.
- The server reduces candidate moves to the best 26 before asking the model to choose.
- Network errors, timeouts, or invalid responses trigger an explicit fallback and are recorded in the ASP.NET Core logs.

## Controls

- `←` / `→`: move
- `↑`: rotate
- `↓`: soft drop
- `Space`: hard drop
- `A`: toggle autopilot
