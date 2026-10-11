# Clef‑Flash plays Tetris

Demo per un articolo sui decision model: una web app ASP.NET Core / .NET 10 con un Tetris giocabile e un autopilota guidato da `clef-flash` eseguito localmente con Ollama.

## Avvio rapido

Servono Ollama **0.35.1 o successivo** e circa 12 GB per il modello predefinito:

```powershell
ollama pull clef-flash
cd ClefFlashTetris
dotnet run
```

Aprire l'indirizzo indicato da ASP.NET Core e premere **Avvia autopilota**. Per impostazione predefinita la demo contatta Ollama in locale su `http://localhost:11434`.

Se Ollama è spento o il modello non è installato, la demo resta giocabile e usa un fallback euristico locale chiaramente indicato nell'interfaccia.

## Flusso della decisione

1. Il browser enumera le pose legali del tetramino corrente.
2. Per ogni posa simula l'atterraggio e calcola righe eliminate, buchi, altezza aggregata, altezza massima e irregolarità.
3. Il server preseleziona al massimo 26 pose, limite previsto da Ollama per una domanda `choice`.
4. `POST /api/decision` invia griglia e pose all'endpoint Ollama configurato, aggiungendo `/v1/systemone`.
5. Ollama/Clef‑Flash restituisce un ID ammesso, confidenza e probabilità per candidato.
6. Il browser mostra la distribuzione e applica la posa.

Il fallback usa le stesse mosse candidate e rende la demo riproducibile offline; il campo `source` della risposta permette di distinguere sempre una decisione reale da quella locale.

`sample-request.json` contiene inoltre una richiesta minima pronta da inviare a `POST /api/decision` durante una spiegazione o una prova dell'API.

## Audit di prompt e risposte

Ogni tentativo di decisione viene aggiunto a `logs/ollama-decisions.jsonl`. Ogni riga è un oggetto JSON autonomo con:

- richiesta System One completa (`request`), cioè stato, istruzioni e opzioni;
- risposta grezza di Ollama (`response`);
- modello, endpoint, timestamp, stato HTTP e latenza;
- eventuale errore e motivo del fallback.

Il file non è pubblicato da `wwwroot`. Il percorso si può cambiare con `Ollama:DecisionLogPath` in `appsettings.json`.

## Configurazione

- Modello: `clef-flash`
- Endpoint: Ollama System One API, `/v1/systemone`
- Configurazione: `Ollama:BaseUrl` e `Ollama:Model` in `appsettings.json`
- Audit: `Ollama:DecisionLogPath`, predefinito `logs/ollama-decisions.jsonl`
- Override opzionali: variabili `OLLAMA_BASE_URL` e `OLLAMA_MODEL`

Per usare il nodo Ollama remoto solo per l'esecuzione corrente:

```powershell
$env:OLLAMA_BASE_URL = "http://100.64.0.9:11434"
dotnet run
```

In alternativa, la URL può essere passata dalla riga di comando:

```powershell
dotnet run -- --Ollama:BaseUrl=http://100.64.0.9:11434
```

- Nessuna chiave API e nessun dato inviato a servizi cloud.
- Il server riduce le mosse candidate alle 26 migliori prima della scelta del modello.
- Errori di rete, timeout o risposte non valide attivano un fallback esplicito e vengono registrati nei log ASP.NET Core.

## Controlli

- `←` / `→`: sposta
- `↑`: ruota
- `↓`: scende
- `Spazio`: posa immediata
- `A`: attiva/disattiva autopilota
