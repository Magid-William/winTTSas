# winTTSas

**Pronunciation:** win - tee tee - es ae es.

Local, OpenAI-compatible text-to-speech for Windows. winTTSas serves the voices
already installed on the machine through `/v1/audio/speech`, so any OpenAI TTS
client can use them without cloud calls, accounts, or API keys.

[![release](https://img.shields.io/github/v/release/Magid-William/winTTSas?label=release)](https://github.com/Magid-William/winTTSas/releases/latest)
[![license](https://img.shields.io/github/license/Magid-William/winTTSas)](LICENSE)
[![platform](https://img.shields.io/badge/platform-Windows%2010%20%7C%2011%20x64-0078D4)](docs/reference.md#requirements)
[![cloud](https://img.shields.io/badge/cloud-none-success)](docs/reference.md)

```text
client -> POST /v1/audio/speech -> winTTSas -> System.Speech (SAPI5)
                                            -> NaturalVoiceSAPIAdapter -> Narrator
```

> **No installer, no admin prompt, no network.** A release bundles the .NET 8
> runtime and ffmpeg, binds to `127.0.0.1` only, and keeps config, cache, and
> logs under `%APPDATA%` / `%LOCALAPPDATA%`.

![The winTTSas test page, with text, voice, format, speed, gain, pitch, sanitize, normalize, and a synthesized clip](docs/images/test-page.png)

## Highlights

- **Any OpenAI TTS client.** Point the base URL at
  `http://127.0.0.1:5178/v1` and use `local` as the API key.
- **The voices Windows already has.** Classic SAPI voices work immediately;
  Narrator natural voices come through the bundled NaturalVoiceSAPIAdapter.
- **Six output formats.** `mp3`, `wav`, `opus`, `aac`, `flac`, and `pcm`,
  produced by the bundled ffmpeg.
- **Tunable delivery.** Speed, pitch in semitones, gain, and optional loudness
  normalization, per request or as defaults.
- **Read aloud from anywhere.** `Ctrl+Alt+R` reads the selection, `Ctrl+Alt+C`
  reads the clipboard, and pressing the shortcut again stops.
- **Friendly to scripts.** `sample` speaks or saves one line, URL routes
  synthesize without a JSON body, and `status --json` reports the real values.
- **Nothing to babysit.** One hidden background process, optional start at
  login, config hot-reload, and a local WAV cache.

## Quick start

1. **Extract the release** anywhere, for example `C:\Tools\winTTSas`. There is
   no installer; run `winTTSas.exe` from the folder or add it to `PATH`.

2. **Start the server.**

   ```powershell
   .\winTTSas.exe start
   ```

   It prints the effective settings and listens on `http://127.0.0.1:5178`.
   `start` is idempotent, `stop` shuts it down, and
   `start --foreground` keeps it in the console. `winTTSas logon on` starts the
   hidden server at login instead.

3. **Hear it.**

   ```powershell
   .\winTTSas.exe sample "Hello from winTTSas."
   ```

   Silent? `.\winTTSas.exe sample --devices` lists the outputs; pass one with
   `--device <n>`.

4. **Open the test page:** <http://127.0.0.1:5178/>. This also works:
   <http://127.0.0.1:5178/v1/audio/speech/voice/1/speed/1.2/text/Hello%20from%20winTTSas>.

## Use it from a client

```text
Base URL: http://127.0.0.1:5178/v1
API key:  local          # ignored unless a token is configured
Model:    tts-1
Voice:    alloy
```

Any OpenAI TTS client works, and `curl.exe` is enough:

```powershell
$body = '{"model":"tts-1","input":"Hello from Windows","voice":"alloy","response_format":"mp3"}'
curl.exe -s -X POST http://127.0.0.1:5178/v1/audio/speech `
  -H "Content-Type: application/json" -d $body -o hello.mp3
```

`voice` accepts an OpenAI alias, a SAPI name, a slug, or a 1-based index from
`winTTSas list`. Set the defaults once and override them per request:

| Setting | Set once | Override per request |
|---|---|---|
| Voice | `winTTSas select` | `voice` |
| Speed | `winTTSas speed` | `speed` |
| Pitch | `winTTSas pitch` | `pitch` |
| Gain | `winTTSas volume` / `gain` | `gain_db` |
| Normalize | `winTTSas normalize` | `normalize` |
| Format | `winTTSas format` | `response_format` |

Clients that only accept a URL can put the values in the path:
`http://127.0.0.1:5178/v1/audio/speech/voice/1/speed/1.2/text/Hello`. See
[synthesis from a URL](docs/reference.md#synthesis-from-a-url).

## Global hotkeys

While the server runs, two shortcuts work in any application:

| Shortcut | Action |
|---|---|
| `Ctrl+Alt+R` | Read the selected text aloud |
| `Ctrl+Alt+C` | Read the clipboard aloud |

Press either shortcut while speech is playing to stop it. The selection is
copied with `Ctrl+C`, then the previous plain-text clipboard is restored.

```powershell
winTTSas selectedText               # Ctrl+Alt+R (on, active)
winTTSas selectedText off
winTTSas selectedText change        # press the new chord; Esc cancels
winTTSas clipboard ...              # same four forms
```

Both chords are configurable, reported by `winTTSas status`, and only reach
non-elevated windows. Details: [global hotkeys](docs/reference.md#global-hotkeys).

## Natural voices (optional)

Classic SAPI voices work immediately. For Narrator voices, install them in
**Settings > Accessibility > Narrator > Add natural voices**, then run once from
an elevated PowerShell:

```powershell
.\scripts\install-adapter.ps1
```

`winTTSas list` shows a `Natural` column. Keep `tools\NaturalVoiceSAPIAdapter`
in place after registering it.

## Documentation

The complete reference — every command, flag, URL route, request field, config
key, and error — is in **[docs/reference.md](docs/reference.md)**.

| I want to… | Go to |
|---|---|
| See every command and flag | [CLI reference](docs/reference.md#cli-reference) |
| Set the default voice, speed, pitch, gain, normalization, or format | [Defaults](docs/reference.md#defaults) |
| Synthesize without a JSON body | [Synthesis from a URL](docs/reference.md#synthesis-from-a-url) |
| See every endpoint | [HTTP API](docs/reference.md#http-api) |
| Tune markdown and symbol sanitization | [Sanitization](docs/reference.md#sanitization) |
| Edit `config.json` | [Configuration](docs/reference.md#configuration) |
| Play, save, or pick a device with `sample` | [Sample playback](docs/reference.md#sample-playback) |
| Fix silence, a 401, or a port clash | [Troubleshooting](docs/reference.md#troubleshooting) |
| Build and publish from source | [Build from source](docs/reference.md#build-from-source) |

## How it works

- Synthesis runs on dedicated STA worker threads with a bounded queue; SAPI is
  never called from request threads.
- Long input is split at sentence boundaries, synthesized as WAV, concatenated,
  then transcoded once with ffmpeg.
- Sanitization runs before normalization, and the sanitized text is the cache
  key, so changing the policy invalidates cached audio.
- Config hot-reloads from disk on the next request; the CLI also pokes the
  running server over a per-user named pipe.
- Canonical WAVs are cached under `%LOCALAPPDATA%\winTTSas\cache`; logs and cache
  keys never contain plaintext text.
- Global hotkeys are registered by the server itself, and local playback pads a
  clip so a sleeping audio device cannot clip the first phoneme.

## Licenses

winTTSas is MIT, © 2026 Magid William. It bundles or depends on:

- **ffmpeg** — GPLv3 build from gyan.dev; license and source links live in
  `tools\ffmpeg`.
- **NaturalVoiceSAPIAdapter** — MIT, © 2024 gexgd0419.
- **Markdig** — BSD-2-Clause, © 2016-2026 Alexandre Mutel.
- **.NET 8 runtime** — MIT, © .NET Foundation and Contributors.

Windows voices are licensed with Windows and are never redistributed. Full
notices are in [the reference](docs/reference.md#licenses) and `licenses\`.
Provided as-is, without support or liability.
