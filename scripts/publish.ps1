[CmdletBinding()]
param(
    [string]$Output = 'dist\winTTSas'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root $Output

if (Test-Path $out) {
    try {
        Remove-Item $out -Recurse -Force -ErrorAction Stop
    }
    catch {
        # A shell or editor can hold the directory itself open. Deleting the
        # contents is enough; publish will recreate the files.
        Get-ChildItem $out -Force -ErrorAction SilentlyContinue |
            Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
    }
}
New-Item -ItemType Directory -Force -Path $out | Out-Null

dotnet publish (Join-Path $root 'src\WinTtsAs.Cli\WinTtsAs.Cli.csproj') `
    -c Release `
    -r win-x64 `
    --self-contained false `
    -p:PublishSingleFile=true `
    -o $out

dotnet publish (Join-Path $root 'src\WinTtsAs.Server\WinTtsAs.Server.csproj') `
    -c Release `
    -r win-x64 `
    --self-contained false `
    -p:PublishSingleFile=true `
    -o $out

$adapter = Join-Path $root 'tools\NaturalVoiceSAPIAdapter'
if (Test-Path $adapter) {
    $adapterOut = Join-Path $out 'tools\NaturalVoiceSAPIAdapter'
    New-Item -ItemType Directory -Force -Path (Split-Path $adapterOut) | Out-Null
    Copy-Item $adapter $adapterOut -Recurse -Force
}

$scriptsOut = Join-Path $out 'scripts'
New-Item -ItemType Directory -Force -Path $scriptsOut | Out-Null
Copy-Item (Join-Path $root 'scripts\install-adapter.ps1') $scriptsOut -Force

$docsOut = Join-Path $out 'docs'
New-Item -ItemType Directory -Force -Path $docsOut | Out-Null
Copy-Item (Join-Path $root 'docs\*') $docsOut -Force

Copy-Item (Join-Path $root 'README.md') $out -Force

Write-Host "Published to $out"
Get-ChildItem $out | Select-Object Name, Length
