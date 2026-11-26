# winTTSas

Local HTTP TTS for Windows natural voices, behind an OpenAI-compatible endpoint.

```
app -> POST /v1/audio/speech -> winTTSas -> System.Speech (SAPI5)
     -> NaturalVoiceSAPIAdapter -> Narrator natural voices
```

## Status

Working v0.1.0. Tested on Windows 11 with `Microsoft Andrew (Natural HD)`.
Aria/Guy/Jenny/Ryan/Sonia can be installed later from
**Settings > Accessibility > Narrator > Add natural voices**.

## Quick start

```powershell
.\dist\winTTSas\winTTSas.exe start
.\dist\winTTSas\winTTSas.exe list
.\dist\winTTSas\winTTSas.exe sample "Hello from winTTSas."
.\dist\winTTSas\winTTSas.exe sample --devices
.\dist\winTTSas\winTTSas.exe sample --device 1 "Hello on the speakers."
.\dist\winTTSas\winTTSas.exe status
```

`--device 1` above was the Realtek speakers on this machine; use the index
printed by `sample --devices`. If the sample is silent, Windows may be routing
to a connected Bluetooth headset — choose another index or change the default
output in Windows settings.

Then point any OpenAI-compatible TTS client at:

```
Base URL: http://127.0.0.1:5178/v1
API key:  local          # ignored unless a token is configured
Model:    tts-1
Voice:    alloy
```

Example:

```powershell
$body = @{
  model = "tts-1"
  input = "Hello from Windows"
  voice = "alloy"
  response_format = "mp3"
} | ConvertTo-Json

Invoke-WebRequest `
  -Uri http://127.0.0.1:5178/v1/audio/speech `
  -Method Post `
  -ContentType application/json `
  -Headers @{ Authorization = "Bearer local" } `
  -Body $body `
  -OutFile hello.mp3
```

A browser test form is at `http://127.0.0.1:5178/`.

## Build

Requires the .NET 8 SDK and ffmpeg on `PATH` for compressed formats or any
non-zero gain; plain WAV at 0 dB needs no ffmpeg.

```powershell
dotnet build
dotnet run --project src\WinTtsAs.Cli -- start --foreground
```

Publish the two-exe layout:

```powershell
.\scripts\publish.ps1
```

Output:

```
dist\winTTSas\
  winTTSas.exe            CLI
  winTTSas-server.exe     hidden HTTP server
  tools\NaturalVoiceSAPIAdapter\
  scripts\install-adapter.ps1
```

## Natural voice adapter

The repository copy lives under `tools\NaturalVoiceSAPIAdapter` (gitignored).
Register both DLLs once:

```powershell
.\scripts\install-adapter.ps1
```

The script copies from the ReadAloud `prior-art` folder when available,
otherwise downloads v0.2.11, verifies its SHA-256, registers x64 + x86 with one
UAC prompt, and disables the adapter's online Edge/Azure voices.

Important: the SAPI registration points at the folder you register from. If you
move or delete it, natural voices disappear for every SAPI app, including
ReadAloud. Re-run the script, or use `winTTSas uninstall --remove-adapter`.

## API

| Method | Path | Purpose |
|---|---|---|
| `POST` | `/v1/audio/speech` | OpenAI-compatible synthesis |
| `POST` | `/audio/speech` | Alias |
| `GET` | `/v1/models` | `tts-1`, `tts-1-hd`, `gpt-4o-mini-tts` |
| `GET` | `/v1/audio/voices` | Installed Windows voices |
| `GET` | `/health` | Status |
| `GET` | `/doctor` | Adapter, voices, ffmpeg, cache |
| `GET` | `/` | Browser test form |

Request fields:

| Field | Behavior |
|---|---|
| `input` | Required, 1-4096 chars; chunked internally |
| `voice` | OpenAI name, Azure-style name, SAPI name, or slug |
| `response_format` | `mp3` (default), `wav`, `opus`, `aac`, `flac`, `pcm` |
| `speed` | 0.25-4.0; multiplied by the global speed |
| `model` | Accepted and ignored |

Audio is normalized to 24 kHz mono. Responses include `X-WinTTSas-Voice`,
`X-WinTTSas-Synth-Ms`, and `X-WinTTSas-Cache`. Errors use OpenAI's JSON shape.

## CLI

| Command | Behavior |
|---|---|
| `winTTSas start` | Start the hidden server; print URL/voice/format/speed |
| `winTTSas start --foreground` | Run the server in this console |
| `winTTSas stop` | Graceful shutdown; force-kill fallback |
| `winTTSas sample [--device N] [text]` | Speak a sentence aloud; `--devices` lists outputs |
| `winTTSas status [--json]` | State, PID, URL, voice, format, speed, queue, logon |
| `winTTSas list [--json]` | Installed SAPI voices |
| `winTTSas select <index, name, or slug>` | Set default voice; live reload |
| `winTTSas volume [dB]` | Set global gain (-24 to +24 dB); live reload |
| `winTTSas speed [factor]` | Set global speed; live reload |
| `winTTSas port [port]` | Set port; restarts if running |
| `winTTSas format [format]` | Set default format; live reload, no restart |
| `winTTSas logon on / off` | Hidden per-user autostart via HKCU Run |
| `winTTSas uninstall` | Stop, remove logon, delete app data |
| `winTTSas help` | Usage |

`format` accepts `mp3`, `wav`, `opus`, `aac`, `flac`, or `pcm`.

`volume` applies a global gain during transcoding with a lookahead limiter
(`volume=<dB>,alimiter=limit=0.84`). The raw WAV cache is not modified, so
changing gain does not invalidate it. Non-zero gain makes even WAV output go
through ffmpeg.

Uninstall keeps the adapter registration by default. Use `--remove-adapter` to
unregister it, `--keep-data` to preserve config/cache/logs, and `--yes` for
non-interactive use.

## How it works

- SAPI voices are enumerated at startup; descriptions containing `(Natural)`
  are flagged and preferred as the default.
- Synthesis runs on dedicated STA worker threads (default 2) with a bounded
  queue (default 20). SAPI is not called from Kestrel request threads.
- Long input is split at sentence boundaries, synthesized as WAV, concatenated,
  then transcoded once with ffmpeg. Optional global gain is applied during that
  encode with a true-peak limiter.
- Canonical WAVs are cached by text/voice/rate/volume hash in
  `%LOCALAPPDATA%\winTTSas\cache`; logs and cache never contain plaintext text.
- Runtime/config/log paths:
  - `%APPDATA%\winTTSas\config.json`
  - `%LOCALAPPDATA%\winTTSas\runtime.json`
  - `%LOCALAPPDATA%\winTTSas\logs`
  - `WINTTSAS_DATA_DIR` overrides all three for tests/portable use.
- CLI and server communicate over a per-user named pipe with
  `PipeOptions.CurrentUserOnly`.

## Layout

```
src/WinTtsAs.Core/       speech, audio, cache, config, pipe, server host
src/WinTtsAs.Cli/        winTTSas.exe
src/WinTtsAs.Server/     winTTSas-server.exe
tests/                   (not yet scaffolded)
tools/NaturalVoiceSAPIAdapter/    copied/downloaded, gitignored
scripts/install-adapter.ps1
scripts/publish.ps1
scripts/smoke.ps1
docs/openai-clients.md
```

## Limitations

- No true streaming: audio is returned after synthesis.
- No SSML/pitch controls in v1.
- No text cleaning by default; input is spoken as sent.
- Gain is global; there is no per-request volume field in the OpenAI API.
- `mp3`/`opus`/`aac`/`flac`/`pcm` require ffmpeg; `wav` only requires it when
  gain is active.
- NaturalVoiceSAPIAdapter is an unsupported shim; Windows updates can break it.
- Stored Narrator HD voices are only partially supported by the adapter.
