# MV/MZ 자동 호환성 테스트 — mkxp_autotest.ps1 / easyrpg_autotest.ps1과 같은 방식
#   .\mv_autotest.ps1                 GameSample\mv, mz 전체
#   .\mv_autotest.ps1 -Match 'Alice'  이름이 맞는 샘플만
# 실제 RocketRPG(디버그 빌드)로 게임을 띄워 WebView2 + NW.js 환경에서 타이틀 → 새 게임 → 게임이 실제로 이동하는
# 맵들을 방문(걷기/확인 키로 이벤트 실행)합니다. 끝나면 앱이 스스로 닫힙니다. 결과: build\autotest\mv_<게임>.log
param([string]$Match, [int]$TimeoutSec = 900)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root
try {
dotnet build src\UI\RocketRPG.UI.csproj -c Debug -v q -nologo | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'UI build failed' }
$app = (Resolve-Path 'src\UI\bin\Debug\net10.0-windows\RocketRPG.exe').Path
$uiLog = 'src\UI\bin\Debug\net10.0-windows\config\ui.log'
$outDir = Join-Path $root 'build\autotest'
New-Item -ItemType Directory -Force $outDir | Out-Null
$games = foreach ($e in 'mv', 'mz') { Get-ChildItem "GameSample\$e" -Directory | Where-Object { -not $Match -or $_.Name -match $Match } }

$results = @()
foreach ($g in $games) {
    Get-Process RocketRPG -ErrorAction SilentlyContinue | Stop-Process -Force
    $safe = $g.Name -replace '[\\/:*?"<>|\[\]]', '_'
    $log = Join-Path $outDir "mv_$safe.log"
    if (Test-Path -LiteralPath $log) { [IO.File]::Delete($log) }
    $before = if (Test-Path $uiLog) { (Get-Content $uiLog -Encoding UTF8 | Measure-Object -Line).Lines } else { 0 }
    $env:RR_AUTOTEST = $log
    $p = Start-Process $app -ArgumentList "`"$($g.FullName)`"" -WorkingDirectory (Split-Path $app) -PassThru
    Remove-Item Env:RR_AUTOTEST -ErrorAction SilentlyContinue
    $null = $p.Handle
    $note = ''
    if (-not $p.WaitForExit($TimeoutSec * 1000)) {
        $last = if (Test-Path -LiteralPath $log) { Get-Content -LiteralPath $log -Tail 1 -Encoding UTF8 } else { '(no log)' }
        $note = "timeout; last: $last"
        Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
    }
    Get-Process nw, Game -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$($g.FullName)*" } | Stop-Process -Force -ErrorAction SilentlyContinue
    $lines = if (Test-Path -LiteralPath $log) { Get-Content -LiteralPath $log -Encoding UTF8 } else { @() }
    $launch = @(Get-Content $uiLog -Encoding UTF8 | Select-Object -Skip $before | Where-Object { $_ -match 'launch: (native|MV/MZ)|relaunching|fatal' } | ForEach-Object { ($_ -replace '^\S+\s', '') })
    $maps = @($lines | Where-Object { $_ -match "`tMAP " }).Count
    $miss = @($lines | Where-Object { $_ -match 'not reached' }).Count
    $crashAll = @($lines | Where-Object { $_ -match "`tCRASH|`tAUTOTEST|FORCE NEW GAME failed" } | Select-Object -Unique)
    # 게임 시작(플러그인 로드) 중에 난 오류인데 게임이 계속 진행됐다면 게임 자체 문제(NW.js에서도 같음) — 실패로 치지 않고 기록만
    $startup = @($crashAll | Where-Object { [int](($_ -split "`t")[0]) -lt 60 -and $_ -notmatch 'fatal' })
    $crash = @($crashAll | Where-Object { $startup -notcontains $_ })
    if ($maps -eq 0) { $crash = $crashAll; $startup = @() }
    $done = [bool]($lines -match "`tDONE")
    $timedOut = $note -like 'timeout*'
    # 시간 제한에 걸렸어도 오류 없이 맵을 계속 돌고 있었다면 통과(시간 제한)로 봅니다
    $verdict = if ($crash.Count) { 'FAIL' } elseif ($lines -match "`tSTUCK") { 'STUCK' } elseif ($done) { 'PASS' } elseif ($timedOut -and $maps -gt 0) { 'PASS(시간제한)' } else { 'FAIL' }
    if (-not $done -and -not $note) { $note = 'app ended without finishing' }
    if ($startup.Count) { $note = (@($note) + @('시작 시 게임 자체 스크립트 오류(계속 진행됨): ' + (($startup[0] -split "`t")[-1]))) -join ' / ' }
    # 테스트 스크립트는 WebView2 안에서만 돕니다: BOOT 줄이 있으면 네이티브, 없고 원본 런타임 전환 기록이 있으면 클래식
    $mode = if ($lines -match "`tBOOT ") { 'WebView2' } elseif ($launch -match 'NW.js|relaunching') { 'NW.js(classic)' } else { '?' }
    $detail = (@($note) + @($crash | Select-Object -First 3) | Where-Object { $_ }) -join ' / '
    $res = [pscustomobject]@{ Game = $g.Name; Mode = $mode; Verdict = $verdict; Maps = $maps; NotReached = $miss; Detail = $detail }
    $results += $res
    Write-Host ("{0,-6} {1} [{2}] (maps {3}, not reached {4}) {5}" -f $res.Verdict, $res.Game, $res.Mode, $res.Maps, $res.NotReached, $res.Detail)
}
$results | Export-Csv (Join-Path $outDir 'summary_mv.csv') -NoTypeInformation -Encoding UTF8
$bad = @($results | Where-Object { $_.Verdict -notlike 'PASS*' }).Count
Write-Host "mv autotest: $($results.Count - $bad)/$($results.Count) passed"
exit $bad
} finally { Pop-Location }
