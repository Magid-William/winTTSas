# winTTSas reference

Complete reference for the CLI, HTTP API, configuration, and build.
New here? Start with the [README](../README.md).

```
app -> POST /v1/audio/speech -> winTTSas -> System.Speech (SAPI5)
     -> NaturalVoiceSAPIAdapter -> Narrator natural voices
```

Working v0.6.1. Tested on Windows 11 with `Microsoft Andrew (Natural HD)`.

## Table of contents

- [Installation and setup](#installation-and-setup)
  - [Release files](#release-files)
  - [Install with the wizard](#install-with-the-wizard)
  - [Manual setup](#manual-setup)
  - [Natural voices (optional)](#natural-voices-optional)
  - [Start the server](#start-the-server)
  - [Verify the install](#verify-the-install)
  - [Start at login](#start-at-login)
- [Quick start](#quick-start)
- [CLI reference](#cli-reference)
  - [Setup](#setup)
  - [Lifecycle](#lifecycle)
  - [Voices](#voices)
  - [Defaults](#defaults)
  - [Global hotkeys](#global-hotkeys)
  - [Sample playback](#sample-playback)
  - [Sanitization](#sanitization)
  - [Autostart and uninstall](#autostart-and-uninstall)
  - [JSON output](#json-output)
- [HTTP API](#http-api)
  - [OpenAI client setup](#openai-client-setup)
  - [Synthesize speech](#synthesize-speech)
  - [Synthesis from a URL](#synthesis-from-a-url)
  - [Request fields](#request-fields)
  - [Output formats](#output-formats)
  - [Models and voices](#models-and-voices)
  - [Status and doctor](#status-and-doctor)
  - [Authentication and CORS](#authentication-and-cors)
  - [Response headers](#response-headers)
  - [Errors](#errors)
- [Configuration](#configuration)
  - [Config file](#config-file)
  - [Paths and portability](#paths-and-portability)
- [Build from source](#build-from-source)
- [How it works](#how-it-works)
- [Troubleshooting](#troubleshooting)
- [Limitations](#limitations)
- [Layout](#layout)
- [Licenses](#licenses)

## Installation and setup

### Requirements

- Windows 10 or 11, x64.
- Nothing else for the app itself: the release already contains the .NET
  runtime and ffmpeg, so there is nothing to download or install.
- Natural voices only: Narrator voices installed in Windows, plus the one-time
  adapter registration below. Classic SAPI voices need neither.

### Release files

Extract `winTTSas.zip` anywhere you like, for example `C:\Tools\winTTSas`:

```
winTTSas\
  winTTSas.exe            CLI
  winTTSas-server.exe     hidden HTTP server
  tools\ffmpeg\ffmpeg.exe bundled transcoder
  tools\NaturalVoiceSAPIAdapter\
  scripts\install-adapter.ps1
  README.md
  LICENSE
  docs\reference.md
```

No installer is required. The app writes its config, cache, and logs under
`%APPDATA%` and `%LOCALAPPDATA%`, so the program folder stays self-contained.

The next two sections are optional: run the wizard to get the `winTTSas`
command on `PATH`, or keep the folder portable and call the executable directly.

### Install with the wizard

The release is portable, so nothing has to be installed. The wizard is the
convenient path: it puts `winTTSas` on your user `PATH` so the command works in
any new terminal, and it wires up the parts that are easy to forget.

```powershell
cd C:\Tools\winTTSas   # or wherever you extracted the zip
.\winTTSas.exe setup
```

It walks through these steps, each with a default you can accept with Enter:

1. **Install folder.** Defaults to `%LOCALAPPDATA%\Programs\winTTSas`. The
   release is copied there, so the extracted zip can be deleted afterwards.
2. **Natural voices.** Runs `scripts\install-adapter.ps1` with one UAC prompt.
   Answer `n` to keep classic SAPI voices only.
3. **PATH.** Appends the install folder to `HKCU\Environment\Path` (no admin)
   and verifies from a child process that `winTTSas.exe` resolves.
4. **Start at login.** Writes the hidden server to `HKCU\...\Run` and starts it.
   An existing logon entry is rewritten to the new folder.
5. **Default voice.** Shows the voice table and prompts once.
6. **Test speech.** Speaks a sentence. Skip it if the machine is muted.
7. **Shortcuts.** Adds a Start Menu shortcut and an Apps & Features entry whose
   Uninstall button runs `winTTSas uninstall`.

The wizard is idempotent: running it again upgrades the installed copy, restarts
the server, and never duplicates the `PATH` entry. Config, cache, and logs live
under `%APPDATA%` and `%LOCALAPPDATA%`, so they survive upgrades.

Non-interactive use:

```powershell
.\winTTSas.exe setup --yes
.\winTTSas.exe setup --yes --dir D:\Tools\winTTSas
.\winTTSas.exe setup --yes --portable
```

| Flag | Effect |
|---|---|
| `--yes` | Accept every default; no prompts and no test speech |
| `--dir <path>` | Install folder (default `%LOCALAPPDATA%\Programs\winTTSas`) |
| `--portable` | Keep the files where they are instead of copying |
| `--skip-adapter` | Do not touch natural voice registration |
| `--no-logon` | Do not start at login |
| `--skip-path` | Do not change `PATH` |
| `--skip-voice` | Keep the current default voice |
| `--skip-test` | Do not speak a test sentence |
| `--no-start` | Do not start the server now |
| `--open` | Open the test page at the end |

If you move the folder later, run `setup` again: the adapter stores absolute
paths, and the PATH entry, logon command, and shortcut are rewritten.

> Open a **new** terminal after setup. The one you ran it from keeps its old
> `PATH`; the final summary prints the base URL and the commands to try.

> The executables are unsigned. SmartScreen may warn on first run; choose
> **More info > Run anyway**.

### Manual setup

Without the wizard, run the executable by full path or add the folder yourself:

```powershell
# Add the folder to this PowerShell session only
$env:Path += ";C:\Tools\winTTSas"

# Or run it by full path
C:\Tools\winTTSas\winTTSas.exe status
```

To make the command permanent by hand, add the folder to the user `PATH` in
**Settings > System > About > Advanced system settings > Environment
Variables**, then open a new terminal. The wizard edits
`HKCU\Environment\Path` directly and is the recommended route.

### Natural voices (optional)

Classic SAPI voices (David, Zira, ...) work immediately. For the modern
Narrator voices:

1. Install voices in Windows:
   **Settings > Accessibility > Narrator > Add natural voices**.
   This machine was tested with `Microsoft Andrew (Natural HD)`; Aria, Guy,
   Jenny, Ryan, and Sonia work the same way.
2. Register the SAPI adapter once, from an **elevated** PowerShell:

   ```powershell
   cd C:\Tools\winTTSas
   .\scripts\install-adapter.ps1
   ```

   The script registers the x64 and x86 adapters with one UAC prompt and
   disables the adapter's online Edge/Azure voices. If the local adapter copy is
   missing, it downloads v0.2.11 and verifies its SHA-256 first.

3. Check what SAPI can see:

   ```powershell
   .\winTTSas.exe list
   ```

   Natural voices show a `yes` in the **Natural** column.

Important: SAPI registration points at the folder you registered from. Keep
`tools\NaturalVoiceSAPIAdapter` in place, or re-run the script after moving the
app. Removing it breaks natural voices for every SAPI app that uses the adapter.

### Start the server

After the wizard, the command is on `PATH`:

```powershell
winTTSas start
```

Without the wizard, `cd` into the folder and run `.\winTTSas.exe start`.

`start` launches the hidden server, waits for it to become ready, and prints
the effective settings:

```
winTTSas 0.6.1
state:    running (pid 14816)
base URL: http://127.0.0.1:5178/v1
voice:    Microsoft Andrew
format:   wav
gain:     0.0 dB
pitch:    0.0 semitones
normalize: off
speed:    2.00 (SAPI rate 6)
...
ffmpeg:   ffmpeg version 9.1-full_build ...
binary:   C:\Tools\winTTSas\tools\ffmpeg\ffmpeg.exe
```

`start` is idempotent: running it again attaches to the existing server.

Run the server in the current console instead (useful for debugging, Ctrl+C
stops it):

```powershell
.\winTTSas.exe start --foreground
```

Print only the base URL, for scripts:

```powershell
.\winTTSas.exe start --quiet
# http://127.0.0.1:5178/v1
```

Stop it:

```powershell
.\winTTSas.exe stop
```

### Verify the install

```powershell
# Show what is running
winTTSas status

# Confirm the command resolves in a new terminal
where winTTSas

# Speak a sentence on the default output device
winTTSas sample "Hello from winTTSas."

# List output devices, then target one explicitly
winTTSas sample --devices
winTTSas sample --device 1 "Hello on the speakers."
```

If `sample` is silent, Windows is probably routing to a Bluetooth headset and
not the speakers you expect. `sample --devices` prints the indices; pick the
right one, or change the default output in Windows settings.

### Start at login

```powershell
.\winTTSas.exe logon on
```

This adds a hidden per-user entry under `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`
and starts the server if it is not already running.

```powershell
.\winTTSas.exe logon        # show state
.\winTTSas.exe logon off    # remove the entry
```

## Quick start

```powershell
winTTSas start
winTTSas list
winTTSas sample "Hello from winTTSas."
winTTSas sample --devices
winTTSas sample --device 1 "Hello on the speakers."
winTTSas sample "Faster and louder." --speed 2 --volume +6
winTTSas status
```

Point any OpenAI-compatible TTS client at:

```
Base URL: http://127.0.0.1:5178/v1
API key:  local          # ignored unless a token is configured
Model:    tts-1
Voice:    alloy
```

Or call it directly:

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

A browser test form is at <http://127.0.0.1:5178/>.

## CLI reference

Run `winTTSas help` for the built-in summary, and `winTTSas version` for the
version. Commands that change settings write `config.json` and hot-reload a
running server, so no restart is needed except for `port`.

### Setup

```powershell
winTTSas setup                 # guided install
winTTSas setup --yes           # defaults, no prompts
winTTSas install               # alias
```

The wizard copies the release to `%LOCALAPPDATA%\Programs\winTTSas` by default,
adds that folder to the user `PATH`, registers natural voices, enables logon
startup, picks a default voice, and creates the Start Menu and Apps & Features
entries. See [install with the wizard](#install-with-the-wizard) for the full
walkthrough and the flag table.

`setup` refuses to run without `--yes` when standard input is redirected, so
scripts must pass it explicitly. `--portable` keeps the current folder instead
of copying, and `--dir <path>` chooses a different install folder.

### Lifecycle

| Command | What it does |
|---|---|
| `winTTSas start` | Start the hidden server (or attach to a running one) |
| `winTTSas start --foreground` | Run the server in this console |
| `winTTSas start --quiet` | Start if needed, print only the base URL |
| `winTTSas stop` | Graceful shutdown, with a force-kill fallback |
| `winTTSas status` | State, PID, URL, voice, format, speed, pitch, normalization, hotkeys, queue, logon |
| `winTTSas status --json` | Same data as JSON |

Examples:

```powershell
winTTSas start --quiet > url.txt
winTTSas status
winTTSas status --json | ConvertFrom-Json | Select-Object running, status
winTTSas stop
```

### Voices

| Command | What it does |
|---|---|
| `winTTSas list` | Table of installed SAPI voices |
| `winTTSas list --json` | Voice list as JSON |
| `winTTSas select <index\|name\|slug>` | Set the default voice |
| `winTTSas select` | Interactive picker |

```powershell
winTTSas list
winTTSas list --json
winTTSas select 1
winTTSas select "Microsoft Andrew"
winTTSas select andrew
```

Voice requests resolve in this order: 1-based index, `voiceMap` alias, exact
name, exact slug, Azure-style name, OpenAI alias (`alloy`, `nova`, ...),
substring match, then the default voice.

### Defaults

These set the built-in defaults used when a request does not override them.
With no argument each command prints the current value.

| Command | Range | Notes |
|---|---|---|
| `winTTSas speed [factor]` | 0.25-4.0 | Maps to SAPI rate -10..10 |
| `winTTSas pitch [semitones]` | -12..+12 | Shifts pitch, keeps duration and tempo |
| `winTTSas normalize [on\|off]` | | Loudness normalization (`loudnorm`) |
| `winTTSas volume [dB]` | -24..+24 | Alias: `gain`; applied with a limiter |
| `winTTSas format [format]` | see below | Hot-reloaded, no restart |
| `winTTSas port [port]` | 1024-65535 | Restarts the server if running |

```powershell
winTTSas speed              # print current speed
winTTSas speed 1.5
winTTSas pitch              # print current pitch
winTTSas pitch -2
winTTSas normalize          # print current normalization
winTTSas normalize on
winTTSas volume             # print current gain
winTTSas volume +3
winTTSas gain -2
winTTSas format             # print current format
winTTSas format mp3
winTTSas port               # print current port
winTTSas port 5200
```

`format` accepts `mp3`, `wav`, `opus`, `aac`, `flac`, or `pcm` (aliases such as
`ogg`, `m4a`, `wave`, and `s16le` are normalized).

`pitch` is a post-synthesis shift in semitones (positive is higher). It uses
ffmpeg's `rubberband` filter, so duration and speaking rate do not change and
formants are preserved. Quality is best within about ±6; beyond that the voice
starts to sound synthetic.

`normalize` evens out loudness differences between voices with ffmpeg
`loudnorm` (target -16 LUFS, -1.5 dBTP). It runs before gain, so `volume` still
works as a relative trim on top.

`volume`/`gain` is a default for all requests, not a system volume control. A
request can override it with `gain_db`. Gain is applied during transcoding with
a lookahead limiter (`volume=<dB>,alimiter=limit=0.84`); the cached raw WAV is
left untouched, so changing gain never invalidates the cache. Non-zero gain
makes even WAV output go through ffmpeg. Pitch and normalization behave the
same way: they are applied after the cache, so they never re-synthesize.

### Global hotkeys

The server registers two global shortcuts while it runs. They read aloud from
any application:

| Shortcut | Action |
|---|---|
| `Ctrl+Alt+R` | Read the selected text |
| `Ctrl+Alt+C` | Read the clipboard |

| Command | Effect |
|---|---|
| `winTTSas selectedText` | Show the chord and whether it is active |
| `winTTSas selectedText on` | Re-enable the configured chord |
| `winTTSas selectedText off` | Unregister it, keep the chord |
| `winTTSas selectedText change` | Wait for the next chord you press; `Esc` cancels |
| `winTTSas selectedText change Ctrl+Shift+R` | Set the chord directly |
| `winTTSas clipboard ...` | Same four forms for the clipboard |

```powershell
winTTSas selectedText
winTTSas selectedText off
winTTSas selectedText change Ctrl+Shift+R
winTTSas clipboard change        # then press the new chord
winTTSas status                  # shows both chords, errors, and speech state
```

Reads use the same synthesis path and defaults as the API (voice, speed, pitch,
gain, normalization, sanitization), so `select`, `speed`, `pitch`, `volume`, and
`normalize` apply. Audio plays on the default output device.

Pressing a shortcut while speech is playing stops it; pressing it again reads.

The selection is copied with an injected `Ctrl+C` after the shortcut's modifiers
are released, and the previous plain-text clipboard is restored afterwards.
Empty text produces a short beep and a log entry instead of speech.

Registration failures are reported instead of being silent: a chord that
Windows or another application already owns shows up in `change`, `on`, and
`status`. An invalid chord is kept verbatim in `config.json` so the error can be
read back. The hotkeys need the running server, so use `start` or `logon on`.

### Sample playback

`sample` plays through the local sound card, or writes a file with `--save`,
and never changes config. Values resolve as `flag -> config -> built-in`.

| Option | Meaning |
|---|---|
| `--speed <0.25-4.0>` | Speed for this sample |
| `--volume <dB>` / `--gain <dB>` | Gain in dB for this sample (`+2`, `-3`) |
| `--pitch <-12..12>` | Pitch shift in semitones for this sample |
| `--normalize [on\|off]` | Loudness normalization for this sample; `--no-normalize` is `off` |
| `--level <0-100>` | SAPI volume (not the same as gain) |
| `--voice <index\|name\|slug>` | Strict voice selection |
| `--save <path>` | Write to a file instead of playing (alias `-o`) |
| `--format <format>` | Output format for `--save`; inferred from the extension |
| `--raw` | Skip markdown and symbol sanitization |
| `--device <n>` | Output device index |
| `--devices` | List output devices and exit |
| `--help` | Sample usage |
| `--` | Treat everything after as text |

Options may appear before or after the text and accept `--speed=2` style.
Unknown options and invalid values exit non-zero instead of being spoken.
`--device` cannot be combined with `--save`; `--format` requires `--save`.

```powershell
winTTSas sample
winTTSas sample "Hello from winTTSas."
winTTSas sample "Faster and louder." --speed 2 --volume +6
winTTSas sample --voice andrew --speed 1.5 --gain 6 --device 1
winTTSas sample "Lower and even." --pitch -3 --normalize
winTTSas sample --level 80 --device 1 "SAPI level test"
winTTSas sample --speed=2 --gain=+2 "Equals syntax"
winTTSas sample -- "Text that starts with --dashes"
winTTSas sample --raw "## Literal — text"

# Save instead of play; format comes from the extension
winTTSas sample "Hello" --save hello.mp3
winTTSas sample "Hello" --save hello.wav
winTTSas sample --voice andrew --speed 1.2 --volume +3 --save hello.flac

# Force a format regardless of the extension
winTTSas sample "Hello" --save output.bin --format mp3
```

`--volume` is gain in dB, the same unit as `winTTSas volume`. `--level` is the
SAPI 0-100 control. `--device 1` referred to the Realtek speakers on this
machine; use the indices from `sample --devices`.

`--save` writes `mp3`, `wav`, `opus`, `aac`, `flac`, or `pcm`. The format comes
from `--format`, otherwise the file extension, otherwise the CLI default.
Parent directories are created and existing files are overwritten. Rendering
does not require the server; only ffmpeg is used for non-WAV output, pitch, gain,
or loudness normalization.

### Sanitization

LLM and Markdown output is converted to speech-friendly text before synthesis.
This is enabled by default for the server and `sample`.

Settings in `config.json`:

| Key | Default | Values |
|---|---|---|
| `sanitize` | `true` | on/off |
| `markdown` | `auto` | `auto`, `on`, `off` |
| `urlMode` | `link` | `skip`, `link`, `domain`, `speak` |
| `codeMode` | `label` | `label`, `skip`, `speak` |
| `inlineCodeMode` | `speak` | `speak`, `label`, `skip` |
| `tableMode` | `flatten` | `flatten`, `skip` |
| `textReplacements` | `{}` | character or string to replacement |

With `markdown: "auto"`, text is parsed as Markdown only when it contains a
strong signal such as a heading, fence, link, table, list, or bold text.

| Input | Spoken |
|---|---|
| `## Headline` | `Headline.` |
| `**bold**`, `*italic*`, `~~gone~~` | `bold`, `italic`, `gone` |
| `- item` / `1. item` | `item.` with sentence pauses |
| `> quote` | quote text |
| `[OpenAI](https://openai.com)` | `OpenAI` |
| `![alt](image.png)` | `alt` |
| `https://example.com` | `link` by default; `skip`, `domain`, or `speak` |
| `` `inline code` `` | `inline code` |
| fenced/indented code block | `code block` |
| Markdown table | flattened rows |
| raw HTML | tags stripped, text retained |
| `—` / `→` / `×` | `,` / `right arrow` / `times` |

```powershell
winTTSas sanitize                  # show modes
winTTSas sanitize on|off
winTTSas sanitize --test "## Hi`nA — B → C"
winTTSas markdown auto|on|off
winTTSas sample --raw "## Keep Markdown"
```

`--test` prints the final spoken text without synthesizing. `textReplacements`
overrides the built-in symbol table; an empty value removes the character.
The API can override the global setting per request with `"sanitize": false`,
and URL forms accept `sanitize/off`.

The sanitized text is what enters the synthesis cache, so changing the
replacement table or Markdown policy changes the cache key.

### Autostart and uninstall

```powershell
winTTSas logon on
winTTSas logon
winTTSas logon off
```

`uninstall` stops the server and removes the logon entry, the user `PATH` entry,
the Start Menu shortcut, the Apps & Features entry, and the install marker. It
then deletes config, cache, and logs, and finally the program folder. The
adapter registration is kept by default.

```powershell
winTTSas uninstall                  # interactive confirmation
winTTSas uninstall --yes            # non-interactive
winTTSas uninstall --yes --keep-data
winTTSas uninstall --yes --keep-files
winTTSas uninstall --yes --remove-adapter
```

| Flag | Effect |
|---|---|
| `--yes` | Do not prompt |
| `--keep-data` | Keep config, cache, and logs |
| `--keep-files` | Keep the program folder |
| `--remove-adapter` | Unregister the natural voice adapter |

The uninstaller uses the `InstallDir` that `setup` wrote under
`HKCU\Software\winTTSas`. When the running executable lives inside that folder,
Windows cannot delete it; removal is scheduled for just after the process exits.
Folders that are a drive root, a standard user folder, or that do not contain
`winTTSas.exe` are never deleted.

### JSON output

`status --json` and `list --json` are intended for scripts:

```powershell
winTTSas status --json | ConvertFrom-Json |
  Select-Object running, configFile, logDir

winTTSas list --json | ConvertFrom-Json |
  Where-Object natural |
  Select-Object index, name, slug, language
```

## HTTP API

The server listens on `http://127.0.0.1:5178` by default and only on loopback.
The OpenAI-compatible base URL is `http://127.0.0.1:5178/v1`.

| Method | Path | Purpose |
|---|---|---|
| `POST` | `/v1/audio/speech` | OpenAI-compatible synthesis |
| `POST` | `/audio/speech` | Alias without `/v1` |
| `GET` | `/v1/audio/speech` | Synthesis from query parameters |
| `GET` | `/audio/speech` | Alias without `/v1` |
| `GET` | `/v1/audio/speech/{key}/{value}/...` | URL-only synthesis nested under the endpoint |
| `GET` | `/audio/speech/{key}/{value}/...` | Alias without `/v1` |
| `GET` | `/v1/{key}/{value}/...` | Shorthand for the same thing |
| `GET` | `/audio/{key}/{value}/...` | Alias without `/v1` |
| `POST` | `/v1/audio/speech/{key}/{value}/...` | JSON body plus URL values |
| `POST` | `/v1/{key}/{value}/...` | Shorthand for the same thing |
| `GET` | `/v1/models` | `tts-1`, `tts-1-hd`, `gpt-4o-mini-tts` |
| `GET` | `/v1/audio/voices` | Installed Windows voices |
| `GET` | `/health` | Compact status |
| `GET` | `/doctor` | Adapter, voices, ffmpeg path, cache |
| `GET` | `/` | Browser test form |

### OpenAI client setup

Any client that accepts a custom OpenAI base URL works:

```
Base URL: http://127.0.0.1:5178/v1
API key:  local
Model:    tts-1
Voice:    alloy
Format:   mp3
```

The API key is ignored unless `token` is set in the config; if it is set, send
it as `Authorization: Bearer <token>`.

Python with the OpenAI SDK:

```python
from openai import OpenAI

client = OpenAI(base_url="http://127.0.0.1:5178/v1", api_key="local")

with client.audio.speech.with_streaming_response.create(
    model="tts-1",
    voice="alloy",
    input="Hello from Python.",
    response_format="mp3",
) as response:
    response.stream_to_file("hello.mp3")
```

### Synthesize speech

`POST /v1/audio/speech` with a JSON body:

```json
{
  "model": "tts-1",
  "input": "Hello from Windows",
  "voice": "alloy",
  "response_format": "mp3",
  "speed": 1.0,
  "gain_db": 0,
  "pitch": 0,
  "normalize": false
}
```

curl (Windows ships `curl.exe`):

```powershell
$body = '{"model":"tts-1","input":"Hello from curl","voice":"alloy","response_format":"mp3"}'
curl.exe -s -X POST http://127.0.0.1:5178/v1/audio/speech `
  -H "Content-Type: application/json" `
  -d $body `
  -o hello.mp3
```

PowerShell:

```powershell
$body = @{
  model = "tts-1"
  input = "Hello from PowerShell"
  voice = "andrew"
  response_format = "wav"
  speed = 1.5
} | ConvertTo-Json

Invoke-WebRequest `
  -Uri http://127.0.0.1:5178/v1/audio/speech `
  -Method Post `
  -ContentType "application/json" `
  -Body $body `
  -OutFile hello.wav
```

JavaScript (browser or Node 18+):

```js
const response = await fetch("http://127.0.0.1:5178/v1/audio/speech", {
  method: "POST",
  headers: { "Content-Type": "application/json" },
  body: JSON.stringify({
    model: "tts-1",
    input: "Hello from JavaScript",
    voice: "alloy",
    response_format: "mp3",
  }),
});

const audio = await response.blob();
console.log("speed:", response.headers.get("x-winttsas-speed"));
console.log("rate: ", response.headers.get("x-winttsas-rate"));
```

Browser code on another origin works: CORS allows any origin and exposes the
`X-WinTTSas-*` headers.

### Synthesis from a URL

Some apps only let you paste a server URL or an endpoint path, with no JSON
body. Others send a normal JSON body but need values in the path. Both work:
values can live in query parameters or in alternating key/value path segments,
and the path forms accept GET (no body) and POST (JSON body).

```powershell
# Query parameters
curl.exe -s "http://127.0.0.1:5178/v1/audio/speech?input=Hello&voice=1&speed=1.2&format=wav" -o hello.wav

# Path segments nested under the speech endpoint; open in a browser and it plays
http://127.0.0.1:5178/v1/audio/speech/voice/1/speed/1.5/gain_db/-3/format/wav/text/Hello%20world

# The same segments work directly under /v1 as a shorthand
curl.exe -s "http://127.0.0.1:5178/v1/voice/1/speed/1.5/text/Hello%20world" -o hello.mp3
```

POST works too. Path and query values win over body fields; the body supplies
whatever the URL omitted, usually just `input`:

```powershell
curl.exe -s -X POST "http://127.0.0.1:5178/v1/voice/1/speed/0.9" `
  -H "Content-Type: application/json" `
  -d '{"input":"Hello from the browser.","voice":"alloy","response_format":"mp3"}' `
  -o hello.mp3
```

Here `voice/1` and `speed/0.9` win; `response_format` comes from the body.

The `/audio/...` alias works the same way, with or without the `/speech`
prefix (for example `http://127.0.0.1:5178/audio/speech/voice/andrew/text/Hello`).

On the nested form, text and other missing values can also come from the query
string: `/v1/audio/speech/voice/1/speed/1.2/?input=Hello`. Path values always
win over query values, and both win over JSON body fields; the body only fills
fields the URL did not set.

| Key | Aliases | Meaning |
|---|---|---|
| `input` | `text` | Text to speak; required |
| `voice` | | SAPI name, slug, `voiceMap` alias, OpenAI alias, or 1-based index |
| `speed` | | 0.25-4.0, overriding the CLI default |
| `pitch` | | -12 to +12 semitones, overriding the CLI default |
| `gain_db` | `gain`, `gaindb`, `volume` | -24 to +24 dB, overriding the CLI default |
| `normalize` | `loudnorm` | `on`/`off`, overriding the config default |
| `response_format` | `format` | `mp3` (default), `wav`, `opus`, `aac`, `flac`, `pcm`, plus aliases |
| `sanitize` | | `on`/`off`, overriding the config default |
| `model` | | Accepted and ignored |

Rules:

- Keys are case-insensitive; values are URL-decoded.
- Path segments must come in pairs, in any order, and a trailing slash is
  ignored. `voice/1/speed/1.5/text/Hi` and `text/Hi/speed/1.5/voice/1` are
  equivalent.
- GET works without a body; POST accepts a JSON body and uses its `input` when
  the path has no text. URL values override body fields with the same name.
- When a key repeats, the last value wins. This also applies across aliases, so
  `format=wav&response_format=mp3` returns mp3.
- Unknown path keys, an odd number of path segments, and empty path values
  return `400`. Unknown query parameters are ignored, so `?token=` and
  cache-busting parameters are harmless.
- Use a dot for decimals (`speed/1.5`); a comma is rejected.
- Text is short by design: the whole URL must fit the HTTP request-line limit
  (8 KB), so use the JSON endpoint for long input.
- A `/` inside the text must be percent-encoded as `%2F`:
  `/v1/text/AC%2FDC/speed/1` speaks `AC/DC`. In a query string, `+` means a
  space, so write `%2B` for a literal plus.

Values behave exactly like the JSON fields: `X-WinTTSas-*` headers, caching,
and errors are identical.

### Request fields

| Field | Behavior |
|---|---|
| `input` | Required, 1 to `maxTextLength` chars (default 4096); `text` in URLs |
| `voice` | OpenAI name, Azure-style name, SAPI name, slug, `voiceMap` alias, or 1-based index from `winTTSas list` |
| `response_format` | `mp3` (default), `wav`, `opus`, `aac`, `flac`, `pcm` |
| `speed` | 0.25-4.0; overrides the CLI default; CLI speed is used when omitted |
| `pitch` | -12 to +12 semitones; overrides the CLI pitch. Duration is unchanged |
| `gain_db` | -24 to +24 dB; overrides the CLI gain. `gainDb` also accepted |
| `normalize` | Boolean; loudness normalization (`loudnorm`) for this request |
| `sanitize` | Boolean; overrides the config default for this request |
| `model` | Accepted and ignored |
| `instructions` | Accepted and ignored |

Unknown voices fall back to the default voice. Missing `voice` uses the default
voice. Text is sanitized by default (Markdown, URLs, symbols); set
`"sanitize": false` to speak the raw text. No SSML is interpreted.

Long input is split at sentence boundaries, synthesized in chunks, concatenated,
and transcoded once. `input` over the limit returns `400`.

### Output formats

| `response_format` | Content type | Extension | Aliases |
|---|---|---|---|
| `mp3` | `audio/mpeg` | `.mp3` | `mpeg` |
| `wav` | `audio/wav` | `.wav` | `wave` |
| `opus` | `audio/ogg` | `.opus` | `ogg`, `oga` |
| `aac` | `audio/aac` | `.aac` | `m4a` |
| `flac` | `audio/flac` | `.flac` | |
| `pcm` | `audio/pcm` | `.pcm` | `s16le` (24 kHz mono, 16-bit LE) |

Audio is normalized to 24 kHz mono. ffmpeg is bundled, so every format works
out of the box.

### Models and voices

```powershell
curl.exe -s http://127.0.0.1:5178/v1/models
curl.exe -s http://127.0.0.1:5178/v1/audio/voices
```

`/v1/models` returns:

```json
{
  "object": "list",
  "data": [
    { "id": "tts-1", "object": "model", "owned_by": "winTTSas" },
    { "id": "tts-1-hd", "object": "model", "owned_by": "winTTSas" },
    { "id": "gpt-4o-mini-tts", "object": "model", "owned_by": "winTTSas" }
  ]
}
```

`/v1/audio/voices` returns the installed SAPI voices with `id` (slug), `name`,
`language`, `gender`, `natural`, `description`, and `sample_rate`. Use the `id`
or `name` directly as the request `voice`.

### Status and doctor

```powershell
curl.exe -s http://127.0.0.1:5178/health
curl.exe -s http://127.0.0.1:5178/doctor
```

`/health` returns `ok`, version, PID, port, base URL, default voice, format,
gain, pitch, normalization, speed, rate, voice counts, queue, workers, and
whether ffmpeg is available.

`/doctor` returns the config/log paths, adapter status (`ok`, `x64 only`,
`x86 only`, or `not registered`), the voice list, the ffmpeg version and
resolved `ffmpegPath`, cache stats, and the full status object. Use it first
when something is wrong.

### Authentication and CORS

By default there is no authentication. To require a token, edit the config:

```json
{
  "token": "secret"
}
```

Then send it as a bearer token (or `X-Api-Key`, or the `token` query parameter):

```powershell
curl.exe -s http://127.0.0.1:5178/v1/models `
  -H "Authorization: Bearer secret"
```

The token protects `/v1/*` and `/audio/*`. `/health`, `/doctor`, and `/` stay
open for local diagnostics and the test page.

CORS allows any origin, any header, and any method. The custom response headers
below are exposed to browser JavaScript.

The server binds only to `127.0.0.1` by default and does not open a firewall
port. To expose it on a LAN, set `bind` to an interface address or `0.0.0.0`,
and set a token.

### Response headers

| Header | Meaning |
|---|---|
| `X-WinTTSas-Voice` | Voice actually used |
| `X-WinTTSas-Speed` | Effective speed after request/CLI fallback |
| `X-WinTTSas-Rate` | SAPI rate -10..10 |
| `X-WinTTSas-Gain-Db` | Effective gain in dB |
| `X-WinTTSas-Pitch` | Effective pitch shift in semitones |
| `X-WinTTSas-Normalize` | `on` or `off` |
| `X-WinTTSas-Synth-Ms` | Synthesis plus transcode time |
| `X-WinTTSas-Cache` | `hit` or `miss` |

Use them to confirm which defaults a request actually used.

### Errors

Errors use OpenAI's JSON shape:

```json
{
  "error": {
    "message": "input must be 4096 characters or fewer",
    "type": "invalid_request_error",
    "param": "input",
    "code": null
  }
}
```

| Status | Meaning |
|---|---|
| `400` | Invalid or empty input, bad format, bad speed, gain, or pitch, bad URL key/value |
| `401` | Token configured but missing or wrong |
| `414` | URL-based request exceeded the HTTP request-line limit; use the JSON endpoint |
| `429` | Synthesis queue full; retry shortly (`Retry-After: 2`) |
| `500` | Synthesis failure |
| `503` | ffmpeg missing, or the bundled copy could not run |

## Configuration

### Config file

Settings live in `%APPDATA%\winTTSas\config.json`. Edit it directly or use the
CLI; both are equivalent. The server hot-reloads the file on the next request.

```json
{
  "port": 5178,
  "bind": "127.0.0.1",
  "defaultVoice": "Microsoft Andrew",
  "defaultSpeed": 1.0,
  "defaultFormat": "mp3",
  "gainDb": 0,
  "pitchSemitones": 0,
  "normalizeLoudness": false,
  "maxTextLength": 4096,
  "chunkChars": 1200,
  "workers": 2,
  "queueLimit": 20,
  "volume": 100,
  "cacheEnabled": true,
  "cacheMaxMb": 200,
  "token": "",
  "ffmpegPath": "ffmpeg",
  "mp3Bitrate": "64k",
  "voiceMap": {
    "alloy": "Microsoft Andrew"
  },
  "sanitize": true,
  "markdown": "auto",
  "urlMode": "link",
  "codeMode": "label",
  "inlineCodeMode": "speak",
  "tableMode": "flatten",
  "textReplacements": {
    "™": " trademark "
  },
  "hotkeys": {
    "selectedText": { "enabled": true, "shortcut": "Ctrl+Alt+R" },
    "clipboard": { "enabled": true, "shortcut": "Ctrl+Alt+C" }
  }
}
```

| Key | Default | Meaning |
|---|---|---|
| `port` | `5178` | HTTP port, 1024-65535 |
| `bind` | `127.0.0.1` | Listen address; `0.0.0.0` exposes to the LAN |
| `defaultVoice` | `""` | Voice name/slug; empty picks a natural voice or the first installed |
| `defaultSpeed` | `1.0` | 0.25-4.0 |
| `defaultFormat` | `mp3` | Default `response_format` |
| `gainDb` | `0` | Default gain, -24..+24 dB |
| `pitchSemitones` | `0` | Default pitch shift, -12..+12 semitones |
| `normalizeLoudness` | `false` | Normalize loudness with ffmpeg `loudnorm` |
| `maxTextLength` | `4096` | Reject longer input |
| `chunkChars` | `1200` | Split long text at sentence boundaries |
| `workers` | `2` | SAPI worker threads, 1-4 |
| `queueLimit` | `20` | Bounded synthesis queue |
| `volume` | `100` | SAPI volume 0-100 for synthesis |
| `cacheEnabled` | `true` | Cache canonical WAVs |
| `cacheMaxMb` | `200` | Cache size limit |
| `token` | `""` | Optional API token |
| `ffmpegPath` | `"ffmpeg"` | Bare name prefers the bundled copy, then `PATH` |
| `mp3Bitrate` | `"64k"` | MP3/AAC bitrate |
| `voiceMap` | `{}` | Alias -> SAPI voice name |
| `sanitize` | `true` | Markdown and symbol sanitization |
| `markdown` | `auto` | `auto`, `on`, `off` |
| `urlMode` | `link` | `skip`, `link`, `domain`, `speak` |
| `codeMode` | `label` | `label`, `skip`, `speak` |
| `inlineCodeMode` | `speak` | `speak`, `label`, `skip` |
| `tableMode` | `flatten` | `flatten`, `skip` |
| `textReplacements` | `{}` | Symbol/string replacement map |
| `hotkeys.selectedText` | `Ctrl+Alt+R` | `enabled` and `shortcut` for reading the selection |
| `hotkeys.clipboard` | `Ctrl+Alt+C` | `enabled` and `shortcut` for reading the clipboard |

### Paths and portability

| Path | Contents |
|---|---|
| `%APPDATA%\winTTSas\config.json` | Settings |
| `%LOCALAPPDATA%\winTTSas\runtime.json` | PID and log location |
| `%LOCALAPPDATA%\winTTSas\logs` | Logs |
| `%LOCALAPPDATA%\winTTSas\cache` | Cached WAVs |

Set `WINTTSAS_DATA_DIR` to relocate all four (tests, portable installs). It also
changes the control-pipe name, so isolated instances can run side by side.

Logs and cache keys never contain plaintext text; cache keys are hashes of
text/voice/rate/volume.

## Build from source

The SDK and ffmpeg folder below are only needed to build; running a published
release needs neither.

Requires the .NET 8 SDK. A local build uses ffmpeg from `PATH` unless
`tools\ffmpeg\ffmpeg.exe` is populated; releases always bundle it.

```powershell
dotnet build
dotnet run --project src\WinTtsAs.Cli -- start --foreground
```

Populate the gitignored ffmpeg folder once before publishing:

```powershell
New-Item -ItemType Directory -Force tools\ffmpeg | Out-Null
Copy-Item C:\path\to\ffmpeg.exe tools\ffmpeg\
Copy-Item C:\path\to\LICENSE tools\ffmpeg\LICENSE
```

Use a static build. This project was tested with the gyan.dev 9.1 full build
(GPLv3); its license is copied into the bundle.

Publish the self-contained layout:

```powershell
.\scripts\publish.ps1
```

The first self-contained publish downloads the .NET runtime packs from NuGet
(~100 MB, one time). Output:

```
dist\winTTSas\
  winTTSas.exe            CLI, self-contained
  winTTSas-server.exe     hidden HTTP server, self-contained
  tools\ffmpeg\ffmpeg.exe bundled transcoder + LICENSE
  tools\NaturalVoiceSAPIAdapter\
  scripts\install-adapter.ps1
  LICENSE
```

Result: about 460 MB on disk, about 194 MB zipped.

## How it works

- SAPI voices are enumerated at startup; descriptions containing `(Natural)`
  are flagged and preferred as the default.
- Synthesis runs on dedicated STA worker threads (default 2) with a bounded
  queue (default 20). SAPI is never called from Kestrel request threads.
- Long input is split at sentence boundaries, synthesized as WAV, concatenated,
  then transcoded once with ffmpeg. Pitch (`rubberband`), loudness
  normalization (`loudnorm`), and gain with a true-peak limiter all run in that
  single encode.
- Markdown and symbol sanitization runs before normalization and chunking. The
  sanitized text is what enters the cache, so changing the policy changes the
  cache key.
- Config changes are hot-reloaded from disk on the next request. The CLI also
  pokes the running server over the control pipe and reports the server's
  effective setting.
- Canonical WAVs are cached by text/voice/rate/volume hash in
  `%LOCALAPPDATA%\winTTSas\cache`; logs and cache never contain plaintext text.
  Pitch, normalization, and gain are applied after the cache, so they never
  trigger re-synthesis.
- CLI and server communicate over a per-user named pipe with
  `PipeOptions.CurrentUserOnly`.
- Global hotkeys use `RegisterHotKey` on a dedicated message-loop thread in the
  server, with auto-repeat suppressed. Each press is handled on a worker that
  reads the text, synthesizes WAV through the same pipeline, and plays it on a
  cancellable background thread. Selection reading injects `Ctrl+C` after the
  shortcut modifiers are released and restores the plain-text clipboard.
- The CLI and server are self-contained; ffmpeg is resolved from the bundled
  `tools\ffmpeg\ffmpeg.exe` first, then `PATH`.

## Troubleshooting

- **Speed, format, gain, pitch, or normalization change does not apply**: run
  `winTTSas status` and check the effective values. Response headers
  `X-WinTTSas-Speed`, `X-WinTTSas-Rate`, `X-WinTTSas-Gain-Db`,
  `X-WinTTSas-Pitch`, and `X-WinTTSas-Normalize` show what a request actually
  used. Restart once after upgrading from an older build.
- **No audio from `sample`**: run `winTTSas sample --devices`. Windows may be
  routing to a Bluetooth headset; pick the speaker index or change the default
  output device.
- **"The operation was canceled by the user" while starting the server**: the
  files still carry the Mark-of-the-Web from the downloaded zip, so the shell
  refuses to run them. `setup` clears it on the installed copy; for a folder you
  extracted by hand, unblock the zip (Properties → **Unblock**) or run
  `Unblock-File .\winTTSas-server.exe`.
- **No natural voices**: install Narrator voices, then run
  `scripts\install-adapter.ps1` elevated. `winTTSas list` should show at least
  one row with `Natural = yes`. If you moved the app folder, re-run the script.
- **401**: a token is configured; send `Authorization: Bearer <token>`.
- **503 "ffmpeg is required"**: `tools\ffmpeg\ffmpeg.exe` is missing or
  `ffmpegPath` points elsewhere. `winTTSas status` prints the resolved binary;
  `/doctor` returns it as `ffmpegPath`.
- **Port already in use**: `winTTSas port 5200`, or stop whatever owns 5178 and
  run `winTTSas start` again.
- **Server is running but the CLI says otherwise**: the control pipe belongs to
  the same user and `WINTTSAS_DATA_DIR`. Check `winTTSas status --json` and the
  logs from `winTTSas status`.
- **Client fails on `/v1/models`**: some clients call it before synthesis. Make
  sure the base URL includes `/v1` and, if a token is set, that it is sent.
- **A hotkey does nothing**: run `winTTSas selectedText` (or `clipboard`).
  `(off)` means it is disabled; `server not running` means `winTTSas start` is
  needed. A warning names a chord that another app already registered; pick a
  different one with `winTTSas selectedText change`.
- **A hotkey beeps instead of speaking**: nothing was selected, the clipboard
  has no text, or the focused window is elevated (Windows blocks injected
  input). The server log names the reason.

## Limitations

- No true streaming: audio is returned after synthesis.
- No SSML: per-span prosody, emphasis, and voice switching are not interpreted.
  Pitch, speed, and loudness are global per request.
- Text sanitization is on by default; use `sanitize off` or `--raw` to speak raw text.
- URL-only requests are limited to short text by the HTTP request-line limit;
  send long input to the JSON endpoint.
- Gain, pitch, and normalization are defaults; the nonstandard `gain_db`,
  `pitch`, and `normalize` request fields can override them. Pitch and
  normalization need ffmpeg filters (`rubberband`, `loudnorm`) that the bundled
  full build has; a PATH-only minimal ffmpeg may lack them.
- `mp3`/`opus`/`aac`/`flac`/`pcm` require ffmpeg; releases bundle it under
  `tools\ffmpeg`. Only a dev build without that folder falls back to `PATH`.
- NaturalVoiceSAPIAdapter is an unsupported shim; Windows updates can break it.
- Stored Narrator HD voices are only partially supported by the adapter.
- The server is single-user and loopback-first; LAN use needs `bind` plus a
  token, and there is no TLS.
- Global hotkeys need the server running and do not reach elevated windows.
  Selection reading injects `Ctrl+C`, so a console with no selection can have
  its running command interrupted, and only plain text is restored to the
  clipboard.

## Layout

```
src/WinTtsAs.Core/       speech, audio, cache, config, pipe, server host
src/WinTtsAs.Cli/        winTTSas.exe
src/WinTtsAs.Server/     winTTSas-server.exe
tests/                   (not yet scaffolded)
tools/NaturalVoiceSAPIAdapter/    copied/downloaded, gitignored
tools/ffmpeg/            bundled ffmpeg.exe + LICENSE, gitignored
scripts/install-adapter.ps1
scripts/publish.ps1
scripts/smoke.ps1
docs/openai-clients.md
docs/reference.md
```

## Licenses

- `tools\ffmpeg` is a GPLv3 ffmpeg build from gyan.dev. The bundle includes its
  `LICENSE` and `README-ffmpeg.txt` with the exact source commit and build
  links.
- NaturalVoiceSAPIAdapter is MIT, © 2024 gexgd0419. Its license is in
  `docs\NaturalVoiceSAPIAdapter-LICENSE.txt`.
- Markdig is BSD-2-Clause, © 2016-2026 Alexandre Mutel. Its license is in
  `docs\Markdig-LICENSE.txt`.
- The .NET 8 runtime is MIT, © .NET Foundation and Contributors.
  `licenses\` holds Microsoft's `LICENSE.TXT` and third-party notices.
- winTTSas itself is MIT, © 2026 Magid William; see [LICENSE](../LICENSE).
