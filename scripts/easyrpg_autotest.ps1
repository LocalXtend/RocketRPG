# EasyRPG(2000/2003) 자동 호환성 테스트 — XP/VX/Ace의 mkxp_autotest.ps1과 같은 방식
#   .\easyrpg_autotest.ps1                 GameSample\2000, 2003 전체
#   .\easyrpg_autotest.ps1 -Match 'death'  이름이 맞는 샘플만
# RocketRPG와 같은 조건(패치된 Player, 인코딩 감지, RTP 경로)으로 게임을 띄워
# 타이틀 → 새 게임 → 게임이 실제로 이동하는 맵들을 차례로 방문(걷기/확인 키로 이벤트 실행)합니다.
# 게임 하나당 창 하나. 결과: build\autotest\erpg_<게임>.log (진행) / summary_easyrpg.csv
# RTP: PC에 설치된 RTP를 Player가 직접 찾습니다 (설치 기록). 설치하지 않았으면 -RtpDir로 RTP가 있는 폴더(그 아래 2000, 2003)를 줍니다.
param([string]$Match, [int]$MaxMaps = 40, [int]$FramesPerMap = 150, [int]$TimeoutSec = 300, [int]$Parallel = 2,
      [string]$Player = 'runtimes\easyrpg\Player.exe', [string]$RtpDir = '')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root
try {
$player = (Resolve-Path $Player).Path
dotnet build src\Tests\Phase1Tests.csproj -c Debug -v q -nologo | Out-Null
$tests = (Resolve-Path 'src\Tests\bin\Debug\net10.0-windows\Phase1Tests.exe').Path
$outDir = Join-Path $root 'build\autotest'
New-Item -ItemType Directory -Force $outDir | Out-Null
$games = foreach ($e in '2000', '2003') { Get-ChildItem "GameSample\$e" -Directory | Where-Object { -not $Match -or $_.Name -match $Match } }
# EasyRPG가 기본으로 찾아보는 선택 파일들 (없어도 정상)
# Unknown event / ThisEvent: 순간이동으로 이벤트 도중 맵이 바뀌어 생기는 테스트 부산물
$benign = 'Unknown event with id|Can''t use ThisEvent|Font/Font|Font/Font2|Logo/LOGO|System/Exfont|ExFont|easyrpg|Soundfont|lcf \(Error\)|Bad encoding'

function Start-One($g) {
    $safe = $g.Name -replace '[\\/:*?"<>|\[\]]', '_'
    $prog = Join-Path $outDir "erpg_$safe.log"
    $elog = Join-Path $outDir "erpg_$safe.player.log"
    foreach ($f in $prog, $elog) { if (Test-Path -LiteralPath $f) { [IO.File]::Delete($f) } }
    $enc = ((& $tests --rpg2k-encoding $g.FullName) -split "`t")[-1]
    $a = "--project-path `"$($g.FullName)`" --window --no-pause-focus-lost --log-file `"$elog`""
    if ($enc -and $enc -ne '(auto)') { $a += " --encoding $enc" }
    $env:RR_AUTOTEST = $prog; $env:RR_AUTOTEST_MAX = "$MaxMaps"; $env:RR_AUTOTEST_FRAMES = "$FramesPerMap"
    if ($RtpDir) {
        $rtp = Join-Path $RtpDir $g.Parent.Name
        if ($g.Parent.Name -eq '2000') { $env:RPG2K_RTP_PATH = $rtp } else { $env:RPG2K3_RTP_PATH = $rtp }
    }
    $p = Start-Process $player -ArgumentList $a -PassThru
    $null = $p.Handle
    Remove-Item Env:RR_AUTOTEST, Env:RR_AUTOTEST_MAX, Env:RR_AUTOTEST_FRAMES, Env:RPG2K_RTP_PATH, Env:RPG2K3_RTP_PATH -ErrorAction SilentlyContinue
    [pscustomobject]@{ G = $g; P = $p; Prog = $prog; ELog = $elog; Enc = $enc; Start = Get-Date; Note = '' }
}

function Finish-One($r) {
    $lines = if (Test-Path -LiteralPath $r.Prog) { Get-Content -LiteralPath $r.Prog -Encoding UTF8 } else { @() }
    $elines = if (Test-Path -LiteralPath $r.ELog) { Get-Content -LiteralPath $r.ELog -Encoding UTF8 } else { @() }
    $maps = @($lines | Where-Object { $_ -match "`tMAP " }).Count
    $miss = @($lines | Where-Object { $_ -match 'not reached' }).Count
    $issues = @($elines | Where-Object { $_ -match 'Error:|Warning:' -and $_ -notmatch $benign } |
        ForEach-Object { ($_ -replace '^\[[^\]]+\]\s*', '').Trim() } | Select-Object -Unique)
    $done = [bool]($lines -match "`tDONE")
    $timedOut = $r.Note -like 'timeout*'
    $verdict = if ($timedOut -and $maps -gt 0 -and -not ($issues -match 'Error:')) { 'PASS(시간제한)' } elseif ($r.Note) { 'FAIL' } elseif (-not $done) { 'FAIL' } elseif ($lines -match "`tSTUCK") { 'STUCK' } elseif ($issues.Count) { 'ISSUE' } else { 'PASS' }
    if (-not $done -and -not $r.Note) { $r.Note = "process ended (exit code $($r.P.ExitCode))" }
    $detail = (@($r.Note) + @($issues | Select-Object -First 4) | Where-Object { $_ }) -join ' / '
    [pscustomobject]@{ Game = $r.G.Name; Encoding = $r.Enc; Verdict = $verdict; Maps = $maps; NotReached = $miss; Detail = $detail }
}

$queue = [Collections.Generic.Queue[object]]::new([object[]]$games)
$running = @(); $results = @()
while ($queue.Count -gt 0 -or $running.Count -gt 0) {
    while ($running.Count -lt $Parallel -and $queue.Count -gt 0) { $running += Start-One $queue.Dequeue() }
    Start-Sleep -Milliseconds 500
    $still = @()
    foreach ($r in $running) {
        $done = $r.P.HasExited
        if (-not $done -and ((Get-Date) - $r.Start).TotalSeconds -gt $TimeoutSec) {
            $last = if (Test-Path -LiteralPath $r.Prog) { Get-Content -LiteralPath $r.Prog -Tail 1 -Encoding UTF8 } else { '(no log)' }
            $r.Note = "timeout; last: $last"; $done = $true
            Stop-Process -Id $r.P.Id -Force -ErrorAction SilentlyContinue; Start-Sleep -Milliseconds 300
        }
        if ($done) {
            $res = Finish-One $r; $results += $res
            Write-Host ("{0,-6} {1} (encoding {2}, maps {3}, not reached {4}) {5}" -f $res.Verdict, $res.Game, $res.Encoding, $res.Maps, $res.NotReached, $res.Detail)
        } else { $still += $r }
    }
    $running = $still
}
$results | Export-Csv (Join-Path $outDir 'summary_easyrpg.csv') -NoTypeInformation -Encoding UTF8
$bad = @($results | Where-Object { $_.Verdict -notlike 'PASS*' }).Count
Write-Host "easyrpg autotest: $($results.Count - $bad)/$($results.Count) passed"
exit $bad
} finally { Pop-Location }
