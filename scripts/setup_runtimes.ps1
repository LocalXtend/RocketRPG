# RocketRPG Runtime Provisioner
# Downloads and bundles official/stable mkxp-z and EasyRPG Player native binaries.
param(
    [switch]$Force = $false
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

# Ensure TLS 1.2 / 1.3 is enabled for web requests on all PowerShell versions
try {
    [System.Net.ServicePointManager]::SecurityProtocol = [System.Net.SecurityProtocolType]::Tls12 -bor [System.Net.SecurityProtocolType]::Tls13
} catch { }

$root = Split-Path $PSScriptRoot -Parent
$runtimesDir = Join-Path $root 'runtimes'
$easyrpgDir = Join-Path $runtimesDir 'easyrpg'
$mkxpDir = Join-Path $runtimesDir 'mkxp-z'

$persistentCacheDir = Join-Path $env:LOCALAPPDATA 'RocketRPG\cache\runtimes'
$cacheEasyRpgDir = Join-Path $persistentCacheDir 'easyrpg'
$cacheMkxpDir = Join-Path $persistentCacheDir 'mkxp-z'

New-Item -ItemType Directory -Force -Path $easyrpgDir | Out-Null
New-Item -ItemType Directory -Force -Path $mkxpDir | Out-Null
New-Item -ItemType Directory -Force -Path $cacheEasyRpgDir | Out-Null
New-Item -ItemType Directory -Force -Path $cacheMkxpDir | Out-Null

Write-Host "=== RocketRPG Runtime Provisioner ==="
Write-Host "Target directory: $runtimesDir"

# 1. EasyRPG Player
$playerExe = Join-Path $easyrpgDir 'Player.exe'
$easyRpgExe = Join-Path $easyrpgDir 'EasyRPG.exe'

# RocketRPG 패치 Player(대화 감지/배속/노클립/ESP/퀵세이브 브릿지)를 우선 사용합니다.
$patchedDist = Join-Path $root 'build\easyrpg\dist'
if (-not (Test-Path (Join-Path $patchedDist 'Player.exe')) -and (Test-Path 'C:\msys64\usr\bin\bash.exe')) {
    Write-Host "[1/2] Building RocketRPG-patched EasyRPG Player (MSYS2)..."
    powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'scripts\build_easyrpg.ps1')
    if ($LASTEXITCODE -ne 0) { Write-Host "  WARN: patched EasyRPG build failed, falling back to the official Player (no bridge features)" }
}
if (Test-Path (Join-Path $patchedDist 'Player.exe')) {
    $patchedExe = Get-Item (Join-Path $patchedDist 'Player.exe')
    if ($Force -or -not (Test-Path $playerExe) -or (Get-Item $playerExe).LastWriteTimeUtc -lt $patchedExe.LastWriteTimeUtc) {
        Write-Host "[1/2] Installing RocketRPG-patched EasyRPG Player..."
        Get-ChildItem -LiteralPath $patchedDist -File | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $easyrpgDir -Force }
    }
    if (Test-Path $easyRpgExe) { [IO.File]::Delete($easyRpgExe) } # 예전 공식 복사본이 먼저 선택되지 않도록
}

$needEasyRpg = $Force -or (-not (Test-Path $playerExe)) -or ((Get-Item $playerExe).Length -lt 1000000)

if ($needEasyRpg) {
    Write-Host "[1/2] Provisioning EasyRPG Player..."
    $cachedPlayer = Join-Path $cacheEasyRpgDir 'Player.exe'
    $legacyTempPlayer = Join-Path $env:TEMP 'easyrpg_test\Player.exe'

    if ((Test-Path $cachedPlayer) -and ((Get-Item $cachedPlayer).Length -ge 1000000)) {
        Write-Host "  Using cached EasyRPG binary from persistent cache..."
        Copy-Item $cachedPlayer $playerExe -Force
    } elseif ((Test-Path $legacyTempPlayer) -and ((Get-Item $legacyTempPlayer).Length -ge 1000000)) {
        Write-Host "  Using cached EasyRPG binary from temp..."
        Copy-Item $legacyTempPlayer $playerExe -Force
        Copy-Item $legacyTempPlayer $cachedPlayer -Force -ErrorAction SilentlyContinue
    } else {
        $zipUrl = 'https://easyrpg.org/downloads/player/0.8.1.1/easyrpg-player-0.8.1.1-windows-x64.zip'
        $fallbackUrl = 'https://easyrpg.org/downloads/player/latest/easyrpg-player-latest-windows-x64.zip'
        $tmpZip = Join-Path $env:TEMP 'easyrpg_download.zip'
        $tmpExtract = Join-Path $env:TEMP 'easyrpg_download_extract'
        Remove-Item -Recurse -Force $tmpExtract -ErrorAction SilentlyContinue

        try {
            Write-Host "  Downloading EasyRPG Player 0.8.1.1..."
            Invoke-WebRequest -Uri $zipUrl -OutFile $tmpZip -TimeoutSec 60 -UseBasicParsing
        } catch {
            Write-Host "  Primary URL failed, trying fallback latest..."
            Invoke-WebRequest -Uri $fallbackUrl -OutFile $tmpZip -TimeoutSec 60 -UseBasicParsing
        }

        Expand-Archive -Path $tmpZip -DestinationPath $tmpExtract -Force
        $extractedExe = Get-ChildItem -Path $tmpExtract -Filter "*.exe" -Recurse | Select-Object -First 1
        if (-not $extractedExe) {
            throw "Failed to find Player.exe inside EasyRPG zip package"
        }
        Copy-Item $extractedExe.FullName $playerExe -Force
        Copy-Item $extractedExe.FullName $cachedPlayer -Force -ErrorAction SilentlyContinue
        Remove-Item -Force $tmpZip -ErrorAction SilentlyContinue
        Remove-Item -Recurse -Force $tmpExtract -ErrorAction SilentlyContinue
    }
}

if (Test-Path $playerExe) {
    $size = (Get-Item $playerExe).Length
    Write-Host "  [OK] EasyRPG Player ready: $playerExe ($size bytes)"
} else {
    throw "EasyRPG Player binary provisioning failed"
}

# 2. mkxp-z Native Runtime
$mkxpExe = Join-Path $mkxpDir 'mkxp-z.exe'
$mkxpAlt = Join-Path $mkxpDir 'RocketRenderMKXP.exe'
$ruby300 = Join-Path $mkxpDir 'x64-msvcrt-ruby300.dll'
$ruby310 = Join-Path $mkxpDir 'x64-msvcrt-ruby310.dll'
$zlib = Join-Path $mkxpDir 'zlib1.dll'

$needMkxp = $Force -or (-not (Test-Path $mkxpExe)) -or ((Get-Item $mkxpExe).Length -lt 1000000) `
    -or (-not (Test-Path $ruby300)) -or (-not (Test-Path $ruby310)) -or (-not (Test-Path $zlib))

if ($needMkxp) {
    Write-Host "[2/2] Provisioning mkxp-z..."
    $legacyTempDir = Join-Path $env:TEMP 'mkxp_test'

    $hasPersistentCache = (Test-Path (Join-Path $cacheMkxpDir 'mkxp-z.exe')) -and `
                          (Test-Path (Join-Path $cacheMkxpDir 'x64-msvcrt-ruby300.dll')) -and `
                          (Test-Path (Join-Path $cacheMkxpDir 'x64-msvcrt-ruby310.dll')) -and `
                          (Test-Path (Join-Path $cacheMkxpDir 'zlib1.dll'))

    $hasLegacyTemp = (Test-Path (Join-Path $legacyTempDir 'mkxp-z.exe')) -and `
                     (Test-Path (Join-Path $legacyTempDir 'x64-msvcrt-ruby300.dll')) -and `
                     (Test-Path (Join-Path $legacyTempDir 'x64-msvcrt-ruby310.dll')) -and `
                     (Test-Path (Join-Path $legacyTempDir 'zlib1.dll'))

    if ($hasPersistentCache) {
        Write-Host "  Using cached mkxp-z binaries from persistent cache..."
        Copy-Item (Join-Path $cacheMkxpDir 'mkxp-z.exe') $mkxpExe -Force
        Copy-Item (Join-Path $cacheMkxpDir 'x64-msvcrt-ruby300.dll') $ruby300 -Force
        Copy-Item (Join-Path $cacheMkxpDir 'x64-msvcrt-ruby310.dll') $ruby310 -Force
        Copy-Item (Join-Path $cacheMkxpDir 'zlib1.dll') $zlib -Force
    } elseif ($hasLegacyTemp) {
        Write-Host "  Using cached mkxp-z binaries from temp..."
        Copy-Item (Join-Path $legacyTempDir 'mkxp-z.exe') $mkxpExe -Force
        Copy-Item (Join-Path $legacyTempDir 'x64-msvcrt-ruby300.dll') $ruby300 -Force
        Copy-Item (Join-Path $legacyTempDir 'x64-msvcrt-ruby310.dll') $ruby310 -Force
        Copy-Item (Join-Path $legacyTempDir 'zlib1.dll') $zlib -Force
        # Save to persistent cache
        Copy-Item (Join-Path $legacyTempDir 'mkxp-z.exe') (Join-Path $cacheMkxpDir 'mkxp-z.exe') -Force -ErrorAction SilentlyContinue
        Copy-Item (Join-Path $legacyTempDir 'x64-msvcrt-ruby300.dll') (Join-Path $cacheMkxpDir 'x64-msvcrt-ruby300.dll') -Force -ErrorAction SilentlyContinue
        Copy-Item (Join-Path $legacyTempDir 'x64-msvcrt-ruby310.dll') (Join-Path $cacheMkxpDir 'x64-msvcrt-ruby310.dll') -Force -ErrorAction SilentlyContinue
        Copy-Item (Join-Path $legacyTempDir 'zlib1.dll') (Join-Path $cacheMkxpDir 'zlib1.dll') -Force -ErrorAction SilentlyContinue
    } else {
        $rawBase = 'https://raw.githubusercontent.com/kurayamiblackheart/kurayshinyrevamp/main'
        Write-Host "  Downloading mkxp-z executable..."
        Invoke-WebRequest -Uri "$rawBase/Game.exe" -OutFile $mkxpExe -TimeoutSec 60 -UseBasicParsing
        Write-Host "  Downloading Ruby 3.0 runtime..."
        Invoke-WebRequest -Uri "$rawBase/x64-msvcrt-ruby300.dll" -OutFile $ruby300 -TimeoutSec 60 -UseBasicParsing
        Write-Host "  Downloading Ruby 3.1 runtime..."
        Invoke-WebRequest -Uri "$rawBase/x64-msvcrt-ruby310.dll" -OutFile $ruby310 -TimeoutSec 60 -UseBasicParsing
        Write-Host "  Downloading zlib1..."
        Invoke-WebRequest -Uri "$rawBase/zlib1.dll" -OutFile $zlib -TimeoutSec 60 -UseBasicParsing

        # Save to persistent cache
        Copy-Item $mkxpExe (Join-Path $cacheMkxpDir 'mkxp-z.exe') -Force -ErrorAction SilentlyContinue
        Copy-Item $ruby300 (Join-Path $cacheMkxpDir 'x64-msvcrt-ruby300.dll') -Force -ErrorAction SilentlyContinue
        Copy-Item $ruby310 (Join-Path $cacheMkxpDir 'x64-msvcrt-ruby310.dll') -Force -ErrorAction SilentlyContinue
        Copy-Item $zlib (Join-Path $cacheMkxpDir 'zlib1.dll') -Force -ErrorAction SilentlyContinue
    }
}

if (Test-Path $mkxpExe) {
    Copy-Item $mkxpExe $mkxpAlt -Force
    $size = (Get-Item $mkxpExe).Length
    Write-Host "  [OK] mkxp-z ready: $mkxpExe ($size bytes)"
} else {
    throw "mkxp-z binary provisioning failed"
}

# 3. MIDI SoundFont (GeneralUser GS)
$sfDir = Join-Path $runtimesDir 'soundfonts'
New-Item -ItemType Directory -Force -Path $sfDir | Out-Null
$sfPath = Join-Path $sfDir 'GeneralUser_GS.sf2'
$cacheSfPath = Join-Path $persistentCacheDir 'GeneralUser_GS.sf2'

$needSf = $Force -or (-not (Test-Path $sfPath)) -or ((Get-Item $sfPath).Length -lt 30000000)

if ($needSf) {
    Write-Host "[3/3] Provisioning GeneralUser GS SoundFont..."
    if ((Test-Path $cacheSfPath) -and ((Get-Item $cacheSfPath).Length -ge 30000000)) {
        Write-Host "  Using cached SoundFont from persistent cache..."
        Copy-Item $cacheSfPath $sfPath -Force
    } else {
        $sfUrl = 'https://raw.githubusercontent.com/ROCKNIX/generaluser-gs/main/GeneralUser%20GS%20v1.471.sf2'
        Write-Host "  Downloading GeneralUser GS v1.471 SoundFont..."
        curl.exe -L -o $sfPath $sfUrl
        if ((Test-Path $sfPath) -and ((Get-Item $sfPath).Length -ge 30000000)) {
            Copy-Item $sfPath $cacheSfPath -Force -ErrorAction SilentlyContinue
        }
    }
}

if (Test-Path $sfPath) {
    $sfSize = (Get-Item $sfPath).Length
    Write-Host "  [OK] SoundFont ready: $sfPath ($sfSize bytes)"
}

# 4. fd Fast Search CLI
$fdDir = Join-Path $root 'tools\fd'
New-Item -ItemType Directory -Force -Path $fdDir | Out-Null
$fdPath = Join-Path $fdDir 'fd.exe'
if (-not (Test-Path $fdPath)) {
    $wingetFd = Join-Path $env:LOCALAPPDATA 'Microsoft\WinGet\Links\fd.exe'
    if (Test-Path $wingetFd) {
        Copy-Item $wingetFd $fdPath -Force
    }
}
if (Test-Path $fdPath) {
    Write-Host "  [OK] fd CLI ready: $fdPath ($((Get-Item $fdPath).Length) bytes)"
}

# 5. mkxp-z 글꼴: 재배포 가능한 NanumGothic(OFL)만 동봉합니다.
#    맑은 고딕/굴림 등 Microsoft 글꼴은 재배포할 수 없으므로, RocketRPG가 실행할 때 사용자 PC의
#    C:\Windows\Fonts 에서 캐시로 복사해 씁니다 (MkxpFontCatalog). 예전에 넣던 사본은 지웁니다.
$mkxpFontsDir = Join-Path $mkxpDir 'Fonts'
New-Item -ItemType Directory -Force -Path $mkxpFontsDir | Out-Null
foreach ($ms in 'malgun.ttf', 'Arial.ttf', 'Arialbd.ttf', 'Gulim.ttf') {
    $p = Join-Path $mkxpFontsDir $ms
    if (Test-Path $p) { [IO.File]::Delete($p); Write-Host "  removed non-redistributable font copy: $ms" }
}
$winNanum = 'C:\Windows\Fonts\NanumGothicExtraBold.ttf'
if (Test-Path $winNanum) {
    $dstNanum = Join-Path $mkxpFontsDir 'NanumGothic.ttf'
    if (-not (Test-Path $dstNanum)) { Copy-Item $winNanum $dstNanum -Force }
}
Write-Host "  [OK] mkxp-z font bundle ready in $mkxpFontsDir (NanumGothic only)"

Write-Host "=== Runtime provisioning complete ==="
Get-ChildItem -Recurse $runtimesDir | Select-Object FullName, Length | Format-Table -AutoSize
