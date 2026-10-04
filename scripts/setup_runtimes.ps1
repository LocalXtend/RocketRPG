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
# mkxp-z 2.4.2 (원본 a5d5749)을 RocketRPG 포크의 GitHub Actions가 빌드한 릴리즈에서 받습니다.
# 소스·의존 라이브러리 소스는 같은 릴리즈에 있습니다. 태그를 바꾸면 해시도 함께 바꾸세요.
$mkxpTag = 'rocketrpg-2.4.2-r1'
$mkxpZipSha256 = 'd0224e31554caee9e2d3ceb7f230eb16350014ccc4d1ec44a38bdf32a67b2a9d'
$mkxpZipUrl = "https://github.com/LocalXtend/mkxp-z-copy/releases/download/$mkxpTag/mkxp-z-$mkxpTag-win64.zip"
$mkxpFiles = @('mkxp-z.exe', 'x64-msvcrt-ruby310.dll', 'zlib1.dll')
$mkxpExe = Join-Path $mkxpDir 'mkxp-z.exe'
$mkxpAlt = Join-Path $mkxpDir 'RocketRenderMKXP.exe'
$mkxpStamp = Join-Path $mkxpDir 'mkxp-z.version.txt'
$cacheStamp = Join-Path $cacheMkxpDir 'mkxp-z.version.txt'

function Test-MkxpSet([string]$dir, [string]$stamp) {
    if (-not (Test-Path $stamp) -or (Get-Content $stamp -Raw).Trim() -ne $mkxpTag) { return $false }
    foreach ($f in $mkxpFiles) { if (-not (Test-Path (Join-Path $dir $f))) { return $false } }
    return $true
}

# 예전에 받던 Ruby 3.0 DLL은 mkxp-z.exe가 쓰지 않으므로 배포 폴더에 남기지 않습니다.
$oldRuby = Join-Path $mkxpDir 'x64-msvcrt-ruby300.dll'
if (Test-Path $oldRuby) { [IO.File]::Delete($oldRuby) }

if ($Force -or -not (Test-MkxpSet $mkxpDir $mkxpStamp)) {
    Write-Host "[2/2] Provisioning mkxp-z ($mkxpTag)..."
    if (-not $Force -and (Test-MkxpSet $cacheMkxpDir $cacheStamp)) {
        Write-Host "  Using cached mkxp-z binaries from persistent cache..."
    } else {
        $tmpZip = Join-Path $env:TEMP "mkxp-z-$mkxpTag-win64.zip"
        $tmpExtract = Join-Path $env:TEMP "mkxp-z-$mkxpTag"
        Write-Host "  Downloading $mkxpZipUrl"
        Invoke-WebRequest -Uri $mkxpZipUrl -OutFile $tmpZip -TimeoutSec 120 -UseBasicParsing
        $hash = (Get-FileHash $tmpZip -Algorithm SHA256).Hash.ToLower()
        if ($hash -ne $mkxpZipSha256) { throw "mkxp-z download hash mismatch: $hash (expected $mkxpZipSha256)" }
        if (Test-Path $tmpExtract) { [IO.Directory]::Delete($tmpExtract, $true) }
        Expand-Archive -Path $tmpZip -DestinationPath $tmpExtract -Force
        foreach ($f in $mkxpFiles) {
            $src = Get-ChildItem -Path $tmpExtract -Filter $f -Recurse -File | Select-Object -First 1
            if (-not $src) { throw "$f not found in $mkxpZipUrl" }
            Copy-Item $src.FullName (Join-Path $cacheMkxpDir $f) -Force
        }
        Set-Content -Path $cacheStamp -Value $mkxpTag -Encoding ASCII
        [IO.File]::Delete($tmpZip)
        [IO.Directory]::Delete($tmpExtract, $true)
    }
    foreach ($f in $mkxpFiles) { Copy-Item (Join-Path $cacheMkxpDir $f) (Join-Path $mkxpDir $f) -Force }
    Set-Content -Path $mkxpStamp -Value $mkxpTag -Encoding ASCII
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
