# RocketRPG 테스트 실행기
#   .\run_tests.ps1          관리 코드 테스트 + 코어 단위 테스트 (수 초)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root
$sw = [Diagnostics.Stopwatch]::StartNew()
$fail = @()

Write-Host '[build] core (+tests) and managed tests...'
powershell -NoProfile -ExecutionPolicy Bypass -File src\Core\build.ps1 -Tests | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'core build failed' }
dotnet build src\Tests\Phase1Tests.csproj -c Debug -v q -nologo | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'test build failed' }

# 서로 독립적인 테스트는 동시에 실행합니다.
$jobs = @(
    @{ Name = 'managed';       Exe = 'src\Tests\bin\Debug\net10.0-windows\Phase1Tests.exe'; Args = @() },
    @{ Name = 'test_core';     Exe = 'build\core\test_core.exe';     Args = @() }
)

$procs = foreach ($j in $jobs) {
    $out = Join-Path $env:TEMP ("rr_test_" + ($j.Name -replace '[^\w]', '_') + ".log")
    $spArgs = @{ FilePath = (Resolve-Path $j.Exe).Path; PassThru = $true; NoNewWindow = $true
                 RedirectStandardOutput = $out; RedirectStandardError = "$out.err" }
    if ($j.Args.Count -gt 0) { $spArgs.ArgumentList = $j.Args }
    $p = Start-Process @spArgs
    $null = $p.Handle   # PS 5.1: 핸들을 바로 잡아 두지 않으면 ExitCode가 비어 있음
    [pscustomobject]@{ Job = $j; P = $p; Log = $out }
}
foreach ($r in $procs) {
    if (-not $r.P.WaitForExit(600000)) { $r.P.Kill(); $fail += "$($r.Job.Name) (timeout)"; continue }
    $r.P.WaitForExit()
    $tail = Get-Content $r.Log -Tail 3 -ErrorAction SilentlyContinue | Where-Object { $_ -match 'Results|VERDICT|PASS|FAIL|ok' } | Select-Object -Last 1
    $status = if ($r.P.ExitCode -eq 0) { 'PASS' } else { $fail += $r.Job.Name; 'FAIL' }
    Write-Host ("  {0,-5} {1,-18} {2}" -f $status, $r.Job.Name, $tail)
}
Get-Process RPG_RT, Game, Player, mkxp-z, nw -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$root\GameSample\*" } | Stop-Process -Force -ErrorAction SilentlyContinue

Write-Host ("tests: {0:0.0}s" -f $sw.Elapsed.TotalSeconds)
Pop-Location
if ($fail.Count -gt 0) { Write-Host "FAILED: $($fail -join ', ')"; exit 1 }
