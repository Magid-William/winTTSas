[CmdletBinding()]
param(
    [int]$Port = 5199
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$cli = Join-Path $root 'dist\winTTSas\winTTSas.exe'
if (-not (Test-Path $cli)) {
    throw "Publish first: scripts\publish.ps1"
}

$data = Join-Path $env:TEMP "winTTSas-smoke-$PID"
New-Item -ItemType Directory -Force -Path $data | Out-Null

$config = @{
    port = $Port
    bind = '127.0.0.1'
    defaultVoice = ''
    defaultFormat = 'mp3'
    defaultSpeed = 1.0
    maxTextLength = 4096
    chunkChars = 1200
    workers = 2
    queueLimit = 20
    volume = 100
    cacheEnabled = $true
    cacheMaxMb = 50
    token = ''
    ffmpegPath = 'ffmpeg'
    mp3Bitrate = '64k'
    voiceMap = @{}
} | ConvertTo-Json
Set-Content -Path (Join-Path $data 'config.json') -Value $config -Encoding UTF8

$env:WINTTSAS_DATA_DIR = $data
$mp3 = Join-Path $data 'smoke.mp3'

try {
    & $cli start

    $body = @{
        model = 'tts-1'
        input = 'winTTSas smoke test.'
        voice = 'alloy'
        response_format = 'mp3'
    } | ConvertTo-Json

    Invoke-WebRequest `
        -Uri "http://127.0.0.1:$Port/v1/audio/speech" `
        -Method Post `
        -ContentType 'application/json' `
        -Body $body `
        -OutFile $mp3

    $file = Get-Item $mp3
    Write-Host "audio: $($file.FullName) ($($file.Length) bytes)"

    $getPath = Join-Path $data 'smoke-get.wav'
    Invoke-WebRequest `
        -Uri "http://127.0.0.1:$Port/v1/voice/1/speed/1.2/format/wav/text/Smoke%20GET%20path" `
        -OutFile $getPath

    $getQuery = Join-Path $data 'smoke-get-query.mp3'
    Invoke-WebRequest `
        -Uri "http://127.0.0.1:$Port/v1/audio/speech?input=Smoke%20GET%20query&format=mp3" `
        -OutFile $getQuery

    Write-Host "get path:  $((Get-Item $getPath).Length) bytes"
    Write-Host "get query: $((Get-Item $getQuery).Length) bytes"

    if (Get-Command ffprobe -ErrorAction SilentlyContinue) {
        ffprobe -v error -show_entries format=format_name,duration -of default=noprint_wrappers=1 $mp3
    }
}
finally {
    & $cli stop
    Remove-Item Env:\WINTTSAS_DATA_DIR -ErrorAction SilentlyContinue
    Remove-Item $data -Recurse -Force -ErrorAction SilentlyContinue
}
