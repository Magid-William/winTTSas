# winTTSas

**Pronunciation:** win - tee tee - es ae es.

Local, OpenAI-compatible text-to-speech for Windows. It serves the voices
already installed on the machine through `/v1/audio/speech`, so any OpenAI TTS
client can use them without cloud calls, accounts, or API keys.

```
client -> POST /v1/audio/speech -> winTTSas -> System.Speech (SAPI5)
                                            -> NaturalVoiceSAPIAdapter -> Narrator
```

## Table of contents

- [Introduction](#introduction)
- [Getting started](#getting-started)
  - [CLI defaults vs API overrides](#cli-defaults-vs-api-overrides)
- [URL routes](#url-routes)
- [Flags](#flags)
  - [Server and CLI](#server-and-cli)
  - [sample](#sample)
- [Licenses](#licenses)

## Introduction

winTTSas is a small background server plus a CLI. It speaks through Windows
SAPI5, including Narrator natural voices through the bundled
NaturalVoiceSAPIAdapter. Responses support `mp3`, `wav`, `opus`, `aac`, `flac`,
and `pcm`, produced with the bundled ffmpeg.

A published release is self-contained: it includes the .NET runtime and ffmpeg,
binds to `127.0.0.1` only, and stores config, cache, and logs under
`%APPDATA%` / `%LOCALAPPDATA%`.

```text
Base URL: http://127.0.0.1:5178/v1
API key:  local          # ignored unless a token is configured
Model:    tts-1
Voice:    alloy
```

The full CLI, API, and configuration reference is in
[docs/reference.md](docs/reference.md).

## Getting started

1. **Extract the release.** There is no installer and no admin prompt. Run
   `winTTSas.exe` from the folder, or add that folder to `PATH`.

2. **Start the server.**

   ```powershell
   .\winTTSas.exe start
   ```

   It prints the effective settings and listens on
   `http://127.0.0.1:5178`. `start` is idempotent; `stop` shuts it down.

3. **Test it locally.**

   ```powershell
   .\winTTSas.exe sample "Hello from winTTSas."
   ```

   No client or JSON needed either: open
   <http://127.0.0.1:5178/v1/audio/speech/voice/1/speed/1.2/text/Hello%20from%20winTTSas>
   in a browser and it plays.

   If that is silent, run `.\winTTSas.exe sample --devices` and pass the right
   index with `--device`.

4. **Natural voices (optional).** Classic SAPI voices work immediately. For
   Narrator voices, install them in **Settings > Accessibility > Narrator >
   Add natural voices**, then run this once from an elevated PowerShell:

   ```powershell
   .\scripts\install-adapter.ps1
   ```

   `.\winTTSas.exe list` shows a `Natural` column. Keep
   `tools\NaturalVoiceSAPIAdapter` in place after registering it.

5. **Start at login (optional).**

   ```powershell
   .\winTTSas.exe logon on
   .\winTTSas.exe logon       # show state
   .\winTTSas.exe logon off   # remove
   ```

   This adds a hidden per-user entry under
   `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`.

### CLI defaults vs API overrides

`winTTSas select`, `speed`, `volume`/`gain`, and `format` set the defaults used
when a request omits a field. Each request can override those defaults; the
precedence is **request > CLI > built-in**.

| Setting | CLI default | Request override |
|---|---|---|
| Voice | `winTTSas select` | `voice` |
| Speed | `winTTSas speed` | `speed` |
| Gain | `winTTSas volume` / `gain` | `gain_db` |
| Format | `winTTSas format` | `response_format` |

```powershell
winTTSas speed 1.5
winTTSas format mp3

# Uses the CLI defaults above
curl.exe -s http://127.0.0.1:5178/v1/audio/speech `
  -H "Content-Type: application/json" `
  -d '{"input":"Default speed and format","voice":"alloy"}' -o default.mp3

# Overrides speed and gain for this request only
curl.exe -s http://127.0.0.1:5178/v1/audio/speech `
  -H "Content-Type: application/json" `
  -d '{"input":"Faster and louder","voice":"alloy","speed":2.5,"gain_db":3}' -o faster.mp3
```

`winTTSas status` shows the current defaults. Response headers
`X-WinTTSas-Speed`, `X-WinTTSas-Rate`, and `X-WinTTSas-Gain-Db` show what a
request actually used.

## URL routes

Every synthesis form is available with and without `/v1`, nested under the
speech endpoint or as a shorthand. Path values win over query values, and both
win over JSON body fields.

| Method | Route | Form |
|---|---|---|
| `POST` | `/v1/audio/speech` | JSON body (OpenAI standard) |
| `POST` | `/audio/speech` | Alias without `/v1` |
| `GET` | `/v1/audio/speech?input=...` | Query parameters |
| `GET` | `/audio/speech?input=...` | Alias without `/v1` |
| `GET` | `/v1/audio/speech/{key}/{value}/...` | URL values, nested |
| `GET` | `/audio/speech/{key}/{value}/...` | Alias without `/v1` |
| `GET` | `/v1/{key}/{value}/...` | URL values, shorthand |
| `GET` | `/audio/{key}/{value}/...` | Alias without `/v1` |
| `POST` | `/v1/audio/speech/{key}/{value}/...` | URL values plus JSON body |
| `POST` | `/audio/speech/{key}/{value}/...` | Alias without `/v1` |
| `POST` | `/v1/{key}/{value}/...` | URL values plus body, shorthand |
| `POST` | `/audio/{key}/{value}/...` | Alias without `/v1` |

Utility routes:

| Method | Route | Purpose |
|---|---|---|
| `GET` | `/v1/models` | Model list |
| `GET` | `/v1/audio/voices` | Installed voices |
| `GET` | `/health` | Status |
| `GET` | `/doctor` | Diagnostics |
| `GET` | `/` | Browser test page |

Path keys: `voice`, `speed`, `gain_db` (`gain`, `volume`), `response_format`
(`format`), `input` (`text`), and `model`. Pairs can be in any order, a trailing
slash is ignored, and `%2F` means a literal `/` in text.

```powershell
# JSON body
curl.exe -s -X POST http://127.0.0.1:5178/v1/audio/speech `
  -H "Content-Type: application/json" `
  -d '{"input":"Hello","voice":"alloy","response_format":"mp3"}' -o hello.mp3

# Query parameters
curl.exe -s "http://127.0.0.1:5178/v1/audio/speech?input=Hello&voice=1&speed=1.2&format=wav" -o hello.wav

# Path values, GET: open in a browser
http://127.0.0.1:5178/v1/audio/speech/voice/1/speed/1.2/text/Hello

# Path values, POST: body supplies the text
curl.exe -s -X POST "http://127.0.0.1:5178/v1/voice/1/speed/0.9" `
  -H "Content-Type: application/json" `
  -d '{"input":"Hello","response_format":"mp3"}' -o hello.mp3
```

## Flags

### Server and CLI

| Flag | Applies to | Meaning |
|---|---|---|
| `--foreground` | `start` | Run the server in this console instead of hidden |
| `--quiet` | `start` | Print only the base URL |
| `--json` | `status`, `list` | Machine-readable output |
| `--yes` | `uninstall` | Skip the confirmation prompt |
| `--keep-data` | `uninstall` | Keep config, cache, and logs |
| `--remove-adapter` | `uninstall` | Unregister the natural voice adapter |
| `-h`, `--help` | global | Show help |
| `-v`, `--version` | global | Show version |

```powershell
winTTSas start --foreground
winTTSas start --quiet > url.txt
winTTSas status --json
winTTSas list --json
winTTSas uninstall --yes
winTTSas uninstall --yes --keep-data
winTTSas uninstall --yes --remove-adapter
winTTSas --help
winTTSas --version
```

### `sample`

`sample` never changes config. Flags may appear before or after the text and
also accept `--flag=value`.

| Flag | Meaning |
|---|---|
| `--speed <0.25-4.0>` | Speed for this sample |
| `--volume <dB>` | Gain in dB (`+2`, `-3`) |
| `--gain <dB>` | Alias for `--volume` |
| `--level <0-100>` | SAPI volume, separate from gain |
| `--voice <index\|name\|slug>` | Strict voice selection |
| `--device <n>` | Output device index |
| `--devices` | List audio outputs and exit |
| `--help` | Show sample usage |
| `--` | Treat everything after as literal text |

```powershell
winTTSas sample "Default settings."
winTTSas sample "Faster." --speed 2
winTTSas sample "Louder." --volume +6
winTTSas sample "Quieter." --gain -3
winTTSas sample "Half SAPI level." --level 50
winTTSas sample --voice 1 "Selected by index."
winTTSas sample --voice andrew "Selected by slug."
winTTSas sample --device 1 "Selected output device."
winTTSas sample --devices
winTTSas sample --help
winTTSas sample -- "--dashes are spoken as text"
winTTSas sample --speed=2 --gain=+2 "Equals syntax."
```

## Licenses

- **Windows voices.** winTTSas calls the voices installed on the machine
  through the Windows SAPI API. It does not include or redistribute voice data
  and does not bypass Microsoft's licensing; users need a licensed Windows
  install.
- **ffmpeg.** The bundle contains a GPLv3 build from gyan.dev. Its license is at
  `tools\ffmpeg\LICENSE`, and `tools\ffmpeg\README-ffmpeg.txt` lists the exact
  source commit and build links. Redistributing the bundle means complying with
  GPLv3, including making the corresponding ffmpeg source available.
- **NaturalVoiceSAPIAdapter.** MIT, © 2024 gexgd0419. Its license is in
  `docs\NaturalVoiceSAPIAdapter-LICENSE.txt`; keep it with the adapter when
  redistributing.
- **.NET 8 runtime.** MIT, © .NET Foundation and Contributors. Self-contained
  builds embed it; `licenses\` contains Microsoft's `LICENSE.TXT` and
  third-party notices.
- **This project.** MIT, © 2026 Magid William. See [LICENSE](LICENSE).
- **No warranty.** Provided as-is, without support or liability.
