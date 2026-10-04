# mkxp-z 자동 호환성 테스트
#   .\mkxp_autotest.ps1                       GameSample\xp, vx, vxace 전체
#   .\mkxp_autotest.ps1 -Match 'SLIME|genkaku'      이름이 맞는 샘플만
#   .\mkxp_autotest.ps1 -Games 'GameSample\xp\청색경보' -MaxMaps 200
# 실제 실행과 같은 mkxp.json/프리로드로 게임을 띄우고, 새 게임 → 모든 맵 순회(이벤트 옆에 세워 상호작용)를 자동으로 진행합니다.
# 스크립트 오류 창이 뜨거나 게임이 멈추면 그 내용을 결과로 남깁니다. 결과: build\autotest\<게임>.log
param([string[]]$Games, [string]$Match, [int]$MaxMaps = 60, [int]$FramesPerMap = 150, [int]$TimeoutSec = 300, [int]$Parallel = 4)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root
try {
if (-not $Games) {
    $Games = foreach ($e in 'xp', 'vx', 'vxace') { Get-ChildItem "GameSample\$e" -Directory | Where-Object { -not $Match -or $_.Name -match $Match } | ForEach-Object FullName }
}
dotnet build src\Tests\Phase1Tests.csproj -c Debug -v q -nologo | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'test build failed' }
$tests = (Resolve-Path 'src\Tests\bin\Debug\net10.0-windows\Phase1Tests.exe').Path
$outDir = Join-Path $root 'build\autotest'
New-Item -ItemType Directory -Force $outDir | Out-Null

Add-Type @"
using System;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public static class AutoDlg {
  public delegate bool P(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] static extern bool EnumWindows(P p, IntPtr l);
  [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent, P p, IntPtr l);
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  public static string Text(uint pid) {
    var o = new List<string>();
    EnumWindows((h, l) => {
      uint p; GetWindowThreadProcessId(h, out p);
      var c = new StringBuilder(64); GetClassName(h, c, 64);
      if (p == pid && c.ToString() == "#32770")
        EnumChildWindows(h, (ch, l2) => { var t = new StringBuilder(4096); GetWindowText(ch, t, 4096); if (t.Length > 0) o.Add(t.ToString()); return true; }, IntPtr.Zero);
      return true;
    }, IntPtr.Zero);
    return string.Join(" | ", o);
  }
}
"@

function Start-One([string]$game) {
    $name = Split-Path $game -Leaf
    $log = Join-Path $outDir (($name -replace '[\\/:*?"<>|]', '_') + '.log')
    if (Test-Path -LiteralPath $log) { [IO.File]::Delete($log) }
    $env:RR_AUTOTEST = $log
    $env:RR_AUTOTEST_MAX = "$MaxMaps"
    $env:RR_AUTOTEST_FRAMES = "$FramesPerMap"
    $prep = (& $tests --mkxp-prepare $game | Select-Object -Last 1)
    $engine, $exe = $prep -split '\|', 2
    if (-not $exe) { return [pscustomobject]@{ Name = $name; Log = $log; P = $null; Note = "prepare failed ($prep)"; Start = Get-Date } }
    $env:RR_RGSS = switch ($engine) { '3' { '1' } '4' { '2' } default { '3' } }
    $p = Start-Process $exe -WorkingDirectory (Split-Path $exe) -PassThru
    $null = $p.Handle
    [pscustomobject]@{ Name = $name; Log = $log; P = $p; Note = ''; Start = Get-Date }
}

function Finish-One($r) {
    $lines = if (Test-Path -LiteralPath $r.Log) { Get-Content -LiteralPath $r.Log -Encoding UTF8 } else { @() }
    $maps = ($lines | Where-Object { $_ -match "`tMAP " }).Count
    $miss = ($lines | Where-Object { $_ -match 'not reached' }).Count
    $crash = $lines | Where-Object { $_ -match "`tCRASH |`t  at |`tFORCE NEW GAME failed|`tAUTOTEST " }
    if (-not $r.Note -and $r.P -and $r.P.HasExited -and -not ($lines -match "`tDONE")) { $gx = @($lines | Where-Object { $_ -match "`tGAME EXIT" } | Select-Object -First 1); $r.Note = "process ended (exit code $($r.P.ExitCode)) $gx" }
    $timedOut = $r.Note -like 'timeout*'
    $verdict = if ($timedOut -and $maps -gt 0 -and -not $crash) { 'PASS(시간제한)' } elseif ($r.Note) { 'FAIL' } elseif ($crash) { 'FAIL' } elseif ($lines -match "`tDONE") { 'PASS' } else { 'FAIL' }
    if ($lines -match "`tSTUCK") { $verdict = 'STUCK' }
    $detail = @($r.Note) + @($crash | Select-Object -First 4) | Where-Object { $_ }
    [pscustomobject]@{ Game = $r.Name; Verdict = $verdict; Maps = $maps; NotReached = $miss; Detail = ($detail -join ' / ') }
}

$queue = [Collections.Generic.Queue[string]]::new([string[]]$Games)
$running = @()
$results = @()
while ($queue.Count -gt 0 -or $running.Count -gt 0) {
    while ($running.Count -lt $Parallel -and $queue.Count -gt 0) { $running += Start-One $queue.Dequeue() }
    Start-Sleep -Milliseconds 500
    $still = @()
    foreach ($r in $running) {
        $done = $false
        if (-not $r.P) { $done = $true }
        elseif ($r.P.HasExited) { $done = $true }
        else {
            $dlg = [AutoDlg]::Text([uint32]$r.P.Id)
            if ($dlg) { $r.Note = "error box: $dlg"; $done = $true }
            elseif (((Get-Date) - $r.Start).TotalSeconds -gt $TimeoutSec) {
                $last = if (Test-Path -LiteralPath $r.Log) { Get-Content -LiteralPath $r.Log -Tail 1 -Encoding UTF8 } else { '(no log)' }
                $r.Note = "timeout; last: $last"; $done = $true
            }
            if ($done) { Stop-Process -Id $r.P.Id -Force -ErrorAction SilentlyContinue; Start-Sleep -Milliseconds 300 }
        }
        if ($done) {
            $res = Finish-One $r
            $results += $res
            Write-Host ("{0,-6} {1} (maps {2}, not reached {3}) {4}" -f $res.Verdict, $res.Game, $res.Maps, $res.NotReached, $res.Detail)
        } else { $still += $r }
    }
    $running = $still
}
Remove-Item Env:RR_AUTOTEST, Env:RR_AUTOTEST_MAX, Env:RR_AUTOTEST_FRAMES, Env:RR_RGSS -ErrorAction SilentlyContinue
$results | Export-Csv (Join-Path $outDir 'summary.csv') -NoTypeInformation -Encoding UTF8
$bad = @($results | Where-Object { $_.Verdict -notlike 'PASS*' }).Count
Write-Host "autotest: $($results.Count - $bad)/$($results.Count) passed"
exit $bad
} finally { Pop-Location }
