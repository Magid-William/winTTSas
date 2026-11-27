namespace WinTtsAs.Core;

public static class TestPage
{
    public const string Html = """
<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>winTTSas</title>
  <style>
    :root { color-scheme: light dark; font-family: Segoe UI, system-ui, sans-serif; }
    body { max-width: 46rem; margin: 2rem auto; padding: 0 1rem; line-height: 1.5; }
    h1 { font-size: 1.4rem; margin-bottom: .25rem; }
    p { color: #888; margin-top: 0; }
    label { display: block; margin-top: 1rem; font-weight: 600; }
    textarea, input, select { width: 100%; box-sizing: border-box; padding: .5rem; font: inherit; margin-top: .3rem; }
    textarea { min-height: 8rem; resize: vertical; }
    .row { display: grid; grid-template-columns: 1fr 1fr 1fr; gap: .75rem; }
    button { margin-top: 1rem; padding: .6rem 1.2rem; font: inherit; cursor: pointer; }
    #status { margin-left: .75rem; color: #888; }
    audio { width: 100%; margin-top: 1rem; }
    .check { display: flex; align-items: center; gap: .5rem; font-weight: 600; }
    .check input { width: auto; margin: 0; }
  </style>
</head>
<body>
  <h1>winTTSas</h1>
  <p>OpenAI-compatible speech endpoint backed by Windows SAPI voices.</p>

  <label for="text">Text</label>
  <textarea id="text">Hello from winTTSas. This is a local Windows natural voice.</textarea>

  <div class="row">
    <div>
      <label for="voice">Voice</label>
      <select id="voice"></select>
    </div>
    <div>
      <label for="format">Format</label>
      <select id="format">
        <option>mp3</option>
        <option>wav</option>
        <option>opus</option>
        <option>aac</option>
        <option>flac</option>
        <option>pcm</option>
      </select>
    </div>
    <div>
      <label for="speed">Speed</label>
      <input id="speed" type="number" min="0.25" max="4" step="0.05" value="1">
    </div>
  </div>

  <label for="key">API key (only if a token is configured)</label>
  <input id="key" type="password" autocomplete="off" placeholder="local">

  <label class="check" for="sanitize">
    <input id="sanitize" type="checkbox" checked>
    Sanitize Markdown, URLs, and symbols
  </label>

  <div>
    <button id="speak">Synthesize</button>
    <span id="status"></span>
  </div>

  <audio id="audio" controls></audio>

  <script>
    const $ = id => document.getElementById(id);

    async function loadVoices() {
      try {
        const response = await fetch('/v1/audio/voices');
        const data = await response.json();
        const voices = data.data || [];
        $('voice').innerHTML = voices.map(v =>
          `<option value="${v.id}">${v.name}${v.natural ? ' (natural)' : ''}</option>`
        ).join('');
      } catch (error) {
        $('status').textContent = 'could not load voices: ' + error;
      }
    }

    async function speak() {
      const key = $('key').value.trim();
      const body = {
        model: 'tts-1',
        input: $('text').value,
        voice: $('voice').value,
        response_format: $('format').value,
        speed: parseFloat($('speed').value) || 1,
        sanitize: $('sanitize').checked
      };
      const headers = { 'Content-Type': 'application/json' };
      if (key) headers.Authorization = 'Bearer ' + key;

      $('status').textContent = 'synthesizing...';
      const started = performance.now();
      try {
        const response = await fetch('/v1/audio/speech', {
          method: 'POST',
          headers,
          body: JSON.stringify(body)
        });

        if (!response.ok) {
          $('status').textContent = await response.text();
          return;
        }

        const blob = await response.blob();
        const url = URL.createObjectURL(blob);
        $('audio').src = url;
        $('audio').play();
        $('status').textContent =
          `${blob.size} bytes in ${Math.round(performance.now() - started)} ms`;
      } catch (error) {
        $('status').textContent = String(error);
      }
    }

    $('speak').addEventListener('click', speak);
    loadVoices();
  </script>
</body>
</html>
""";
}
