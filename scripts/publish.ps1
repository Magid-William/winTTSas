[CmdletBinding()]
param(
    [string]$Output = 'dist\winTTSas',
    [string]$FfmpegSource = 'tools\ffmpeg'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root $Output
$ffmpegDir = Join-Path $root $FfmpegSource
$ffmpegExe = Join-Path $ffmpegDir 'ffmpeg.exe'

if (-not (Test-Path $ffmpegExe)) {
    throw "ffmpeg.exe not found in '$ffmpegDir'. Copy ffmpeg.exe and its LICENSE into $FfmpegSource before publishing."
}

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
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -o $out

dotnet publish (Join-Path $root 'src\WinTtsAs.Server\WinTtsAs.Server.csproj') `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -o $out

$adapter = Join-Path $root 'tools\NaturalVoiceSAPIAdapter'
if (Test-Path $adapter) {
    $adapterOut = Join-Path $out 'tools\NaturalVoiceSAPIAdapter'
    New-Item -ItemType Directory -Force -Path (Split-Path $adapterOut) | Out-Null
    Copy-Item $adapter $adapterOut -Recurse -Force
}

$ffmpegOut = Join-Path $out 'tools\ffmpeg'
New-Item -ItemType Directory -Force -Path $ffmpegOut | Out-Null
Copy-Item (Join-Path $ffmpegDir '*') $ffmpegOut -Recurse -Force

$scriptsOut = Join-Path $out 'scripts'
New-Item -ItemType Directory -Force -Path $scriptsOut | Out-Null
Copy-Item (Join-Path $root 'scripts\install-adapter.ps1') $scriptsOut -Force

$docsOut = Join-Path $out 'docs'
New-Item -ItemType Directory -Force -Path $docsOut | Out-Null
Copy-Item (Join-Path $root 'docs\*') $docsOut -Force

Copy-Item (Join-Path $root 'README.md') $out -Force
Copy-Item (Join-Path $root 'LICENSE') $out -Force

# The self-contained exes embed the MIT-licensed .NET runtime. Keep Microsoft's
# license and third-party notices with the release.
$nugetRoot = if ($env:NUGET_PACKAGES) {
    $env:NUGET_PACKAGES
} else {
    Join-Path $env:USERPROFILE '.nuget\packages'
}

$licenseOut = Join-Path $out 'licenses'
New-Item -ItemType Directory -Force -Path $licenseOut | Out-Null

function Copy-RuntimeFile {
    param(
        [string]$Package,
        [string]$FileName,
        [string]$TargetName
    )

    $packageDir = Join-Path $nugetRoot $Package
    if (-not (Test-Path $packageDir)) {
        Write-Warning "runtime pack not found: $packageDir"
        return
    }

    $versionDir = Get-ChildItem $packageDir -Directory |
        Sort-Object { [version]$_.Name } -Descending |
        Select-Object -First 1

    $source = Join-Path $versionDir.FullName $FileName
    if (Test-Path $source) {
        Copy-Item $source (Join-Path $licenseOut $TargetName) -Force
    }
    else {
        Write-Warning "license file not found: $source"
    }
}

Copy-RuntimeFile 'microsoft.netcore.app.runtime.win-x64' 'LICENSE.TXT' 'dotnet-LICENSE.TXT'
Copy-RuntimeFile 'microsoft.netcore.app.runtime.win-x64' 'THIRD-PARTY-NOTICES.TXT' 'dotnet-THIRD-PARTY-NOTICES.TXT'
Copy-RuntimeFile 'microsoft.aspnetcore.app.runtime.win-x64' 'THIRD-PARTY-NOTICES.TXT' 'aspnetcore-THIRD-PARTY-NOTICES.TXT'

Write-Host "Published to $out"
$total = (Get-ChildItem $out -Recurse -File | Measure-Object Length -Sum).Sum
Write-Host ("Total size: {0:N0} MB" -f ($total / 1MB))
Get-ChildItem $out | Select-Object Name, Length
