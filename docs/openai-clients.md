# OpenAI client setup

Use `http://127.0.0.1:5178/v1` as the base URL and any non-empty key if no token
is configured.

## Environment variables

```powershell
$env:OPENAI_BASE_URL = "http://127.0.0.1:5178/v1"
$env:OPENAI_API_KEY  = "local"
```

Most OpenAI SDKs read these. The model is ignored; `tts-1` is a safe choice.

## Python SDK

```python
from openai import OpenAI

client = OpenAI(
    base_url="http://127.0.0.1:5178/v1",
    api_key="local",
)

with client.audio.speech.with_streaming_response.create(
    model="tts-1",
    voice="alloy",
    input="Hello from winTTSas.",
    response_format="mp3",
) as response:
    response.stream_to_file("hello.mp3")
```

## Node / fetch

```js
const response = await fetch("http://127.0.0.1:5178/v1/audio/speech", {
  method: "POST",
  headers: {
    "Content-Type": "application/json",
    Authorization: "Bearer local"
  },
  body: JSON.stringify({
    model: "tts-1",
    input: "Hello from winTTSas.",
    voice: "alloy",
    response_format: "mp3",
    speed: 1.0
  })
});

if (!response.ok) {
  throw new Error(await response.text());
}

const audio = Buffer.from(await response.arrayBuffer());
require("fs").writeFileSync("hello.mp3", audio);
```

## PowerShell

```powershell
$body = @{
  model = "tts-1"
  input = "Hello from winTTSas."
  voice = "alloy"
  response_format = "mp3"
  speed = 1.0
} | ConvertTo-Json

Invoke-WebRequest `
  -Uri http://127.0.0.1:5178/v1/audio/speech `
  -Method Post `
  -ContentType application/json `
  -Headers @{ Authorization = "Bearer local" } `
  -Body $body `
  -OutFile hello.mp3
```

## Loudness

Set the default gain in the CLI:

```powershell
winTTSas volume +6
```

Standard OpenAI clients omit volume and get that default. A request can override
it with the nonstandard `gain_db` field (or `gainDb`):

```json
{
  "model": "tts-1",
  "input": "Louder, please.",
  "voice": "alloy",
  "response_format": "mp3",
  "gain_db": 12
}
```

Gain is applied with a lookahead limiter during transcoding. The raw WAV cache
stays intact, so changing gain does not invalidate it. `gain_db: 0` overrides a
non-zero CLI default and disables gain for that request.

`speed` works the same way: the request value overrides `winTTSas speed`; the
CLI default applies only when `speed` is omitted.

## Voice names

`voice` accepts:

- OpenAI aliases: `alloy`, `echo`, `fable`, `onyx`, `nova`, `shimmer`, `coral`,
  `verse`, `sage`, `ash`, `ballad`
- Azure-style names: `en-GB-RyanNeural`
- SAPI names: `Microsoft Ryan`
- Slugs from `/v1/audio/voices`: `ryan`

If the requested voice is unavailable, the server uses its default voice.
`winTTSas list` shows what is actually installed.

## Troubleshooting

- **401**: a token is configured; send it as `Authorization: Bearer <token>`.
- **503 for mp3/opus/etc.**: the bundled `tools\ffmpeg\ffmpeg.exe` is missing or
  `FfmpegPath` points elsewhere. `winTTSas status` shows the resolved binary.
- **Robotic voice**: `/v1/audio/voices` is falling back to David/Zira. Install
  Narrator voices and run `scripts\install-adapter.ps1`.
- **`/v1/models` fails**: some clients call it before synthesis; the endpoint is
  implemented, so check the base URL includes `/v1`.
- **App only accepts a raw URL, not an OpenAI base URL**: pass the endpoint path
  directly. Values can live in the URL, for example
  `http://127.0.0.1:5178/v1/audio/speech/voice/1/speed/1.2/text/Hello`; GET and
  POST both work. See
  [Synthesis from a URL](reference.md#synthesis-from-a-url).
