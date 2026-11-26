# winTTSas

Local, OpenAI-compatible text-to-speech for Windows. It serves the voices
already installed on the machine through `/v1/audio/speech`, so any OpenAI TTS
client can use them without cloud calls, accounts, or API keys.

```
client -> POST /v1/audio/speech -> winTTSas -> System.Speech (SAPI5)
                                            -> NaturalVoiceSAPIAdapter -> Narrator
```

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

## Legal

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
- **This project.** No license file yet, so the source is all rights reserved by
  default. Add a `LICENSE` (MIT or Apache-2.0 are common choices) before
  publishing the source.
- **No warranty.** Provided as-is, without support or liability.
