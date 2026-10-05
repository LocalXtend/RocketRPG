# RocketRPG packaging: dist/portable + installer payload
#   .\package.ps1            빠른 개발용: dist/portable만 갱신 (증분 코어 빌드, 런타임 미러, UPX 캐시)
#   .\package.ps1 -Release   배포용: + 실행 스모크 검사, 포터블 zip, 설치 프로그램
param([switch]$Release, [switch]$Smoke)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$totalSw = [Diagnostics.Stopwatch]::StartNew()

$root = Split-Path $PSScriptRoot -Parent
Push-Location $root

# 1. Provision native runtimes first so dotnet publish includes them
Write-Host '[1/7] provisioning native runtimes...'
powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'scripts\setup_runtimes.ps1')
if ($LASTEXITCODE -ne 0) { throw 'Runtime setup failed' }

Write-Host '[2/7] building core libraries...'
powershell -NoProfile -ExecutionPolicy Bypass -File src\Core\build.ps1 | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Core build failed' }

Write-Host '[3/7] publishing UI...'
dotnet publish src\UI\RocketRPG.UI.csproj -c Release -r win-x64 --self-contained true `
  /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true /p:EnableCompressionInSingleFile=true /p:DebugType=none -v q | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'UI publish failed' }

$portable = Join-Path $root 'dist\portable'
$cfg = Join-Path $portable 'config'

Write-Host '[4/7] assembling dist/portable...'
Stop-Process -Name RocketRPG, Player, EasyRPG, mkxp-z, RocketRenderMKXP, RPG_RT -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 500
# 개발 모드는 사용자 데이터(웹뷰 프로필, 로그, 크래시)를 남기고 배포 파일만 갱신합니다.
# 배포(-Release)는 개인 데이터가 zip/설치 파일에 섞이지 않도록 깨끗한 폴더에서 시작합니다.
if ($Release) { Remove-Item -Recurse -Force $portable -ErrorAction SilentlyContinue }
New-Item -ItemType Directory -Force -Path $cfg | Out-Null

$pub = Join-Path $root 'src\UI\bin\Release\net10.0-windows\win-x64\publish'
Copy-Item "$pub\RocketRPG.exe" $portable -Force
Copy-Item (Join-Path $root 'build\core\RocketRPGCore.dll') $portable -Force
# 라이선스 원문·고지 (함께 배포하는 구성요소의 라이선스 의무)
robocopy (Join-Path $pub 'licenses') (Join-Path $portable 'licenses') /MIR /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "robocopy licenses failed ($LASTEXITCODE)" }
$global:LASTEXITCODE = 0
if (-not (Test-Path (Join-Path $portable 'licenses\THIRD_PARTY_NOTICES.md'))) { throw 'Portable verification failed: licenses missing' }
# 클래식(원본 실행 + 주입) 모드를 없애 32비트 주입 DLL은 더 이상 없습니다. 예전 빌드의 사본은 지웁니다.
Remove-Item (Join-Path $portable 'RocketRPGCore32.dll') -Force -ErrorAction SilentlyContinue
Set-Content -Encoding UTF8 (Join-Path $cfg 'global.json') '{"gamma":1.0,"volume":100,"ratio":"none","filter":"none","history":[]}'

# Bundle runtimes into dist/portable (robocopy /MIR: 바뀐 파일만 복사, 사라진 파일 정리)
$runtimesSrc = Join-Path $root 'runtimes'
if (-not (Test-Path $runtimesSrc)) { throw "Runtimes directory missing: $runtimesSrc" }
$destRuntimes = Join-Path $portable 'runtimes'
robocopy $runtimesSrc $destRuntimes /MIR /NFL /NDL /NJH /NJS /NP /XF mkxp.json Game.ini | Out-Null
if ($LASTEXITCODE -ge 8) { throw "robocopy runtimes failed ($LASTEXITCODE)" }
$global:LASTEXITCODE = 0

# Bundle tools/fd into dist/portable
$fdSrc = Join-Path $root 'tools\fd\fd.exe'
if (Test-Path $fdSrc) {
    $destFdDir = Join-Path $portable 'tools\fd'
    New-Item -ItemType Directory -Force -Path $destFdDir | Out-Null
    Copy-Item $fdSrc $destFdDir -Force
    Write-Host "  tools/fd bundled successfully ($((Get-Item $fdSrc).Length)B)"
}

# Verify runtime binaries and fonts exist in portable distribution
$mkxpBin = Join-Path $portable 'runtimes\mkxp-z\mkxp-z.exe'
$easyRpgBin = Join-Path $portable 'runtimes\easyrpg\Player.exe'
if (-not (Test-Path $mkxpBin)) { throw "Portable verification failed: mkxp-z.exe not found in $mkxpBin" }
if (-not (Test-Path $easyRpgBin)) { throw "Portable verification failed: Player.exe not found in $easyRpgBin" }
# Microsoft 글꼴은 재배포하지 않습니다 (실행 시 사용자 PC에서 복사).
$msFonts = @(Get-ChildItem (Join-Path $portable 'runtimes\mkxp-z\Fonts') -EA SilentlyContinue | Where-Object { $_.Name -match '^(malgun|arial|arialbd|gulim)' })
if ($msFonts.Count) { throw "Portable verification failed: non-redistributable fonts bundled: $($msFonts.Name -join ', ')" }
Write-Host "  runtimes and fonts bundled successfully (mkxp-z: $((Get-Item $mkxpBin).Length)B, easyrpg: $((Get-Item $easyRpgBin).Length)B)"

Get-ChildItem $portable -Recurse -Include *.pdb,*.xml -File | Remove-Item -Force

# UPX compression on native PE binaries with SHA256 caching (배포용에서만: 개발 빌드는 속도 우선)
if ($Release) {
    Write-Host '  compressing native binaries with UPX (cached)...'
    $upxExe = & (Join-Path $root 'scripts\setup_upx.ps1')
    $cacheDir = Join-Path $root 'tools\upx\cache'
    New-Item -ItemType Directory -Force -Path $cacheDir | Out-Null

    # 배포 대상 폴더만 (사용자 데이터 폴더의 DLL은 건드리지 않음)
    $nativeFiles = @(Get-ChildItem -Path (Join-Path $portable 'runtimes'), (Join-Path $portable 'tools') -Recurse -Include *.dll,*.exe -File -ErrorAction SilentlyContinue) +
                   @(Get-ChildItem -Path $portable -Filter 'RocketRPGCore*.dll' -File)
    foreach ($nf in $nativeFiles) {
        $origSize = $nf.Length
        $uncompressedHash = (Get-FileHash -Path $nf.FullName -Algorithm SHA256).Hash
        $cachedCompressed = Join-Path $cacheDir "$uncompressedHash.bin"
        if ((Test-Path $cachedCompressed) -and ((Get-Item $cachedCompressed).Length -gt 0)) {
            Copy-Item $cachedCompressed $nf.FullName -Force
            Write-Host "    UPX [cache hit]: $($nf.Name) ($origSize -> $((Get-Item $nf.FullName).Length) bytes)"
        } else {
            $out = & $upxExe -9 -q $nf.FullName 2>&1
            if ($LASTEXITCODE -ne 0) {
                Write-Host "    UPX [skip]: $($nf.Name) ($(($out | Select-Object -Last 1) -replace '^.*Exception: ',''))"
                $global:LASTEXITCODE = 0
                continue
            }
            Copy-Item $nf.FullName $cachedCompressed -Force
            Write-Host "    UPX [compressed]: $($nf.Name) ($origSize -> $((Get-Item $nf.FullName).Length) bytes)"
        }
    }
}

if (-not ($Release -or $Smoke)) {
    Write-Host ("done (dev). {0:0.0}s  -> {1}" -f $totalSw.Elapsed.TotalSeconds, $portable)
    Write-Host '  배포용 zip/설치 파일은 -Release 로 만듭니다.'
    Pop-Location
    return
}

Write-Host '[5/7] smoke-launching portable exe and native engine verification...'
Remove-Item (Join-Path $cfg 'ui.log') -ErrorAction SilentlyContinue
$sampleGame = Join-Path $root 'GameSample\2000\7thJojo'
if (Test-Path $sampleGame) {
    $env:RR_SWITCHTEST = $sampleGame   # 게임 자동 실행 진단 (RR_AUTOTEST는 호환성 자동 테스트용)
}
$proc = Start-Process -FilePath (Join-Path $portable 'RocketRPG.exe') -WorkingDirectory $portable -PassThru
$uilog = Join-Path $cfg 'ui.log'
$logText = ''
for ($i = 0; $i -lt 30; $i++) {
    Start-Sleep -Milliseconds 500
    if ($proc.HasExited) { throw "RocketRPG.exe exited immediately (code $($proc.ExitCode))" }
    if (Test-Path $uilog) {
        $logText = Get-Content $uilog -Raw
        if ($logText.Contains('RocketRenderEasyRPG: native surface detected')) {
            break
        }
    }
}
if (-not (Test-Path $uilog)) { taskkill /pid $proc.Id /T /F 2>&1 | Out-Null; Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue; throw 'smoke gate: ui.log missing - app never initialized' }
taskkill /pid $proc.Id /T /F 2>&1 | Out-Null
Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
try { $proc.WaitForExit(5000) } catch { }
Get-CimInstance Win32_Process -ErrorAction SilentlyContinue | Where-Object { $_.Name -like 'msedgewebview2*' -and $_.CommandLine -like '*webview2_data*' } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
Remove-Item env:RR_SWITCHTEST -ErrorAction SilentlyContinue
for ($retry = 0; $retry -lt 15; $retry++) {
    try {
        $testStream = [System.IO.File]::Open("$portable\RocketRPG.exe", [System.IO.FileMode]::Open, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
        $testStream.Close()
        $testStream.Dispose()
        break
    } catch {
        Start-Sleep -Milliseconds 500
    }
}
Start-Sleep -Milliseconds 500
if ($logText -match 'dispatcher EX' -or $logText -match 'OnExit code=-1') { throw 'smoke gate: startup exception detected in ui.log' }
if ($logText -notmatch 'ctor: begin') { throw 'smoke gate: MainWindow ctor never reached' }
if (Test-Path $sampleGame) {
    if (-not $logText.Contains('launch: EasyRPG pipeline') -or -not $logText.Contains('RocketRenderEasyRPG: native surface detected')) {
        throw "smoke gate: portable native EasyRPG verification failed in ui.log: $logText"
    }
}
Write-Host '  launch OK (window ctor reached, native runtime verified, no startup exceptions)'
Remove-Item $uilog -Force -ErrorAction SilentlyContinue
# 스모크 실행이 만든 사용자 데이터는 배포물에서 제외
# 스모크 실행이 만든 설정(히스토리 = 이 PC의 경로)이 배포 파일에 들어가지 않게 config도 지웁니다.
foreach ($d in 'webview2_data', 'crash', 'config') { Remove-Item -Recurse -Force (Join-Path $portable $d) -ErrorAction SilentlyContinue }

if (-not $Release) {
    Write-Host ("done (dev + smoke). {0:0.0}s" -f $totalSw.Elapsed.TotalSeconds)
    Pop-Location
    return
}

Write-Host '[6/7] license audit, GPL source archives, portable zip...'
# 배포 파일이 모두 구성요소 목록에 있고, RTP·게임 데이터·원본 런타임·재배포 불가 글꼴이 없어야 함
pwsh -NoProfile -File (Join-Path $root 'scripts\license_audit.ps1') -Dir $portable
if ($LASTEXITCODE -ne 0) { throw 'License audit failed (build\license_audit.md)' }
Add-Type -AssemblyName System.IO.Compression.FileSystem
$ver = (Get-Content (Join-Path $root 'ver.txt') -Raw).Trim()
$dist = Join-Path $root 'dist'
# GPL 구성요소의 대응 소스: EasyRPG Player 수정본(지금 빌드한 커밋)과 mkxp-z(배포 바이너리의 커밋)
$playerRepo = Join-Path $root 'build\easyrpg\Player'
$erpgZip = Join-Path $dist "RocketRPG-$ver-easyrpg-source.zip"
git -C $playerRepo archive --format=zip --prefix="EasyRPG_Patch-$ver/" -o $erpgZip HEAD
if ($LASTEXITCODE -ne 0) { throw 'EasyRPG source archive failed' }
# mkxp-z는 setup_runtimes.ps1이 받는 포크 태그의 소스 (의존 라이브러리 소스는 그 태그의 릴리즈에 있음)
$mkxpTag = (Select-String -Path (Join-Path $PSScriptRoot 'setup_runtimes.ps1') -Pattern "^\`$mkxpTag = '(.+)'").Matches[0].Groups[1].Value
$mkxpCache = Join-Path $root "build\mkxp-z-$mkxpTag.zip"
if (-not (Test-Path $mkxpCache)) { Invoke-WebRequest "https://github.com/LocalXtend/mkxp-z-copy/archive/refs/tags/$mkxpTag.zip" -OutFile $mkxpCache -UseBasicParsing }
Copy-Item $mkxpCache (Join-Path $dist "RocketRPG-$ver-mkxp-z-source.zip") -Force
$zipFile = Join-Path $dist "RocketRPG-$ver-portable.zip"
$payloadZip = Join-Path $dist 'payload.zip'
Remove-Item -Force $zipFile, $payloadZip -ErrorAction SilentlyContinue
[System.IO.Compression.ZipFile]::CreateFromDirectory($portable, $payloadZip, [System.IO.Compression.CompressionLevel]::Optimal, $false)
Copy-Item $payloadZip $zipFile -Force
# 포터블 zip은 끝 기록을 zip64로 (1.1.0~1.1.2의 빠른 업데이트 오류를 피해 '전체 받기'로 가게; scripts\zip64_end.ps1 설명 참고)
pwsh -NoProfile -File (Join-Path $PSScriptRoot 'zip64_end.ps1') $zipFile
if ($LASTEXITCODE -ne 0) { throw 'zip64 end records failed' }

$payloadDir = Join-Path $dist 'payload'
Remove-Item -Recurse -Force $payloadDir -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $payloadDir | Out-Null
Copy-Item "$portable" "$payloadDir\" -Recurse -Force

Write-Host '[7/7] building standalone installer...'
dotnet publish src\Installer\RocketRPG.Installer.csproj -c Release -r win-x64 --self-contained true `
  /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true /p:EnableCompressionInSingleFile=true /p:DebugType=none -v q | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Installer publish failed' }

Copy-Item (Join-Path $root 'src\Installer\bin\Release\net10.0-windows\win-x64\publish\RocketRPGInstaller.exe') $dist -Force
Remove-Item -Force $payloadZip -ErrorAction SilentlyContinue

Write-Host ("done (release). {0:0.0}s" -f $totalSw.Elapsed.TotalSeconds)
Write-Host "  $portable"
Write-Host "  $dist\RocketRPGInstaller.exe  (standalone embedded payload)"
Write-Host "  $zipFile"
Pop-Location
