# RocketRPG UPX Tool Setup
# Downloads and caches official UPX binary for executable/DLL compression.
param(
    [switch]$Force = $false
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

try {
    [System.Net.ServicePointManager]::SecurityProtocol = [System.Net.SecurityProtocolType]::Tls12 -bor [System.Net.SecurityProtocolType]::Tls13
} catch { }

$root = Split-Path $PSScriptRoot -Parent
$toolsDir = Join-Path $root 'tools\upx'
$upxExe = Join-Path $toolsDir 'upx.exe'

$persistentCacheDir = Join-Path $env:LOCALAPPDATA 'RocketRPG\cache\tools'
$cachedUpx = Join-Path $persistentCacheDir 'upx.exe'

New-Item -ItemType Directory -Force -Path $toolsDir | Out-Null
New-Item -ItemType Directory -Force -Path $persistentCacheDir | Out-Null

$needUpx = $Force -or (-not (Test-Path $upxExe)) -or ((Get-Item $upxExe).Length -lt 100000)

if ($needUpx) {
    Write-Host "=== Provisioning UPX Binary ==="

    if ((Test-Path $cachedUpx) -and ((Get-Item $cachedUpx).Length -ge 100000) -and (-not $Force)) {
        Write-Host "  Using cached upx.exe from persistent cache..."
        Copy-Item $cachedUpx $upxExe -Force
    } else {
        $zipUrl = 'https://github.com/upx/upx/releases/download/v4.2.4/upx-4.2.4-win64.zip'
        $tmpZip = Join-Path $env:TEMP 'upx_download.zip'
        $tmpExtract = Join-Path $env:TEMP 'upx_download_extract'
        Remove-Item -Recurse -Force $tmpExtract -ErrorAction SilentlyContinue

        try {
            Write-Host "  Downloading UPX 4.2.4..."
            Invoke-WebRequest -Uri $zipUrl -OutFile $tmpZip -TimeoutSec 60 -UseBasicParsing
        } catch {
            Write-Warning "  Invoke-WebRequest failed: $_. Retrying with curl.exe..."
            curl.exe -L -s -o $tmpZip $zipUrl
            if ($LASTEXITCODE -ne 0 -or (-not (Test-Path $tmpZip))) {
                throw "Failed to download UPX from $zipUrl"
            }
        }

        Write-Host "  Extracting upx.exe..."
        Expand-Archive -Path $tmpZip -DestinationPath $tmpExtract -Force
        $extractedUpx = Get-ChildItem -Path $tmpExtract -Filter "upx.exe" -Recurse | Select-Object -First 1
        if (-not $extractedUpx) {
            throw "upx.exe not found in extracted archive"
        }

        Copy-Item $extractedUpx.FullName $upxExe -Force
        Copy-Item $extractedUpx.FullName $cachedUpx -Force -ErrorAction SilentlyContinue

        Remove-Item -Force $tmpZip -ErrorAction SilentlyContinue
        Remove-Item -Recurse -Force $tmpExtract -ErrorAction SilentlyContinue
    }
}

if (-not (Test-Path $upxExe)) {
    throw "UPX setup failed: $upxExe not found"
}

$verOutput = (& $upxExe --version | Select-Object -First 1)
Write-Host "  UPX ready: $verOutput ($upxExe)"
return $upxExe
