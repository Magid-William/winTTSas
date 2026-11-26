[CmdletBinding()]
param(
    [string]$AdapterDir = (Join-Path $PSScriptRoot '..\tools\NaturalVoiceSAPIAdapter'),
    [string]$SourceDir = 'D:\Dev\Playground\Projects\Apps\readaloudFromClipboard\prior-art\tools\NaturalVoiceSAPIAdapter',
    [switch]$Elevated
)

$ErrorActionPreference = 'Stop'
$adapterDir = [IO.Path]::GetFullPath($AdapterDir)
$x64 = Join-Path $adapterDir 'x64\NaturalVoiceSAPIAdapter.dll'
$x86 = Join-Path $adapterDir 'x86\NaturalVoiceSAPIAdapter.dll'

if (-not (Test-Path $x64) -or -not (Test-Path $x86)) {
    if (Test-Path (Join-Path $SourceDir 'x64\NaturalVoiceSAPIAdapter.dll')) {
        Write-Host "Copying adapter from $SourceDir"
        New-Item -ItemType Directory -Force -Path $adapterDir | Out-Null
        Copy-Item -Path (Join-Path $SourceDir '*') -Destination $adapterDir -Recurse -Force
    }
    else {
        $zip = Join-Path $env:TEMP 'NaturalVoiceSAPIAdapter_v0.2.11_x86_x64.zip'
        $expected = '01A0D38E4B42750AF3CBD82A120215C83BEA9A5B1AE133805E0F82819C911994'
        Write-Host "Downloading NaturalVoiceSAPIAdapter v0.2.11"
        Invoke-WebRequest `
            -Uri 'https://github.com/gexgd0419/NaturalVoiceSAPIAdapter/releases/download/v0.2.11/NaturalVoiceSAPIAdapter_v0.2.11_x86_x64.zip' `
            -OutFile $zip `
            -Headers @{ 'User-Agent' = 'opencode' }

        $actual = (Get-FileHash $zip -Algorithm SHA256).Hash.ToUpperInvariant()
        if ($actual -ne $expected) {
            throw "SHA-256 mismatch: expected $expected, got $actual"
        }

        New-Item -ItemType Directory -Force -Path $adapterDir | Out-Null
        Expand-Archive -LiteralPath $zip -DestinationPath $adapterDir -Force
    }
}

if (-not (Test-Path $x64) -or -not (Test-Path $x86)) {
    throw "Adapter DLLs are missing under $adapterDir"
}

$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
$isAdmin = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $isAdmin) {
    Write-Host "Requesting administrator rights to register the adapter..."
    $elevatedArgs = @(
        '-NoProfile'
        '-ExecutionPolicy', 'Bypass'
        '-File', "`"$PSCommandPath`""
        '-AdapterDir', "`"$adapterDir`""
        '-Elevated'
    )
    $process = Start-Process powershell.exe -Verb RunAs -Wait -PassThru -ArgumentList $elevatedArgs
    exit $process.ExitCode
}

Write-Host "Registering x64 adapter..."
Start-Process regsvr32.exe -ArgumentList @('/s', $x64) -Wait | Out-Null
Write-Host "Registering x86 adapter..."
Start-Process regsvr32.exe -ArgumentList @('/s', $x86) -Wait | Out-Null

$key = 'HKCU:\Software\NaturalVoiceSAPIAdapter\Enumerator'
New-Item $key -Force | Out-Null
Set-ItemProperty $key NoNarratorVoices 0 -Type DWord
Set-ItemProperty $key NoEdgeVoices 1 -Type DWord
Set-ItemProperty $key NoAzureVoices 1 -Type DWord

$clsid = '{013ab33b-ad1a-401c-8bee-f6e2b046a94e}'
$p64 = (Get-ItemProperty "HKLM:\SOFTWARE\Classes\CLSID\$clsid\InprocServer32" -ErrorAction SilentlyContinue).'(default)'
$p86 = (Get-ItemProperty "HKLM:\SOFTWARE\WOW6432Node\Classes\CLSID\$clsid\InprocServer32" -ErrorAction SilentlyContinue).'(default)'

Write-Host "x64: $p64"
Write-Host "x86: $p86"

if ($p64 -ne $x64) {
    throw "x64 registration mismatch: expected $x64"
}
if ($p86 -ne $x86) {
    throw "x86 registration mismatch: expected $x86"
}

Write-Host "NaturalVoiceSAPIAdapter registered from $adapterDir"
Write-Host "Restart any SAPI apps (ReadAloud, winTTSas) so they re-enumerate voices."
