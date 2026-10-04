# RocketRPG 멀티 시험: 한 PC에서 방장 1명 + 참가자 N명을 띄워 바로 같은 방에 넣습니다.
#
#   pwsh -File scripts\multi_test.ps1                          2000 샘플 게임, 로컬 서버, 참가자 1명
#   pwsh -File scripts\multi_test.ps1 -Game GameSample\mv\Alice -Guests 2 -Control
#   pwsh -File scripts\multi_test.ps1 -Server Prod             운영 서버 (비공개 방이라 방 목록에 안 뜸)
#   pwsh -File scripts\multi_test.ps1 -Build                   먼저 빌드
#   pwsh -File scripts\multi_test.ps1 -Logs                    방장·참가자 로그 마지막 줄 보기
#   pwsh -File scripts\multi_test.ps1 -Stop                    모두 끄기 (로컬 서버 포함)
#
# 각자 따로 설정 폴더(build\multitest\host, guest1...)를 써서 서로·평소 설정과 섞이지 않습니다.
# 로그: build\multitest\<이름>\config\ui.log
param(
    [string]$Game = 'GameSample\2000\7thJojo',  # 방장이 켤 게임 폴더 (저장소 기준 상대 경로 또는 절대 경로, '' = 게임 없이)
    [int]$Guests = 1,                            # 참가자 수 (1~3)
    [string]$Server = 'Local',                   # Local (wrangler dev, 127.0.0.1:8787) | Prod | 서버 주소
    [switch]$Control,                            # 방장이 처음부터 조종 권한을 켬
    [switch]$Fps60,                              # 방송 60fps
    [switch]$Build,                              # dotnet build 먼저
    [switch]$Fresh,                              # 설정 폴더를 지우고 시작 (처음 쓰는 사람처럼)
    [string]$Exe = '',                           # RocketRPG.exe 경로 (기본: Debug 빌드)
    [switch]$Stop,
    [switch]$Logs,
    [int]$LogLines = 25
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$work = Join-Path $root 'build\multitest'
$serverDir = Join-Path $root 'src\Server'
$stateFile = Join-Path $work 'state.json'
New-Item -ItemType Directory -Force $work | Out-Null

function Get-State { if (Test-Path $stateFile) { Get-Content $stateFile -Raw | ConvertFrom-Json } else { $null } }

function Stop-All {
    $st = Get-State
    if ($st) {
        # 각 RocketRPG와 그것이 켠 게임(Player/mkxp-z/nw)까지
        foreach ($id in @($st.pids)) { taskkill /F /T /PID $id 2>$null | Out-Null }
        if ($st.server) { taskkill /F /T /PID $st.server 2>$null | Out-Null }
    }
    Remove-Item $stateFile -ErrorAction SilentlyContinue
    Write-Host 'stopped'
}

if ($Stop) { Stop-All; return }

$names = @('host') + (1..([Math]::Clamp($Guests, 1, 3)) | ForEach-Object { "guest$_" })

if ($Logs) {
    foreach ($n in $names + @('guest2', 'guest3') | Select-Object -Unique) {
        $log = Join-Path $work "$n\config\ui.log"
        if (-not (Test-Path $log)) { continue }
        Write-Host "===== $n" -ForegroundColor Cyan
        Get-Content $log -Tail $LogLines
    }
    return
}

if (Get-State) { Stop-All }

if ($Build) {
    Write-Host '[build] RocketRPG (Debug)...'
    dotnet build (Join-Path $root 'src\UI\RocketRPG.UI.csproj') -c Debug -v q -nologo
    if ($LASTEXITCODE -ne 0) { throw 'build failed' }
}
if (-not $Exe) { $Exe = Join-Path $root 'src\UI\bin\Debug\net10.0-windows\RocketRPG.exe' }
if (-not (Test-Path $Exe)) { throw "RocketRPG.exe 없음: $Exe (-Build 로 먼저 빌드하세요)" }

# ── 서버 ──
$serverPid = $null
switch ($Server) {
    'Local' {
        $url = 'http://127.0.0.1:8787'
        $up = $false
        try { $up = (Invoke-WebRequest "$url/api" -UseBasicParsing -TimeoutSec 2).StatusCode -eq 200 } catch { }
        if (-not $up) {
            Write-Host '[server] wrangler dev (local)...'
            $log = Join-Path $work 'wrangler.log'
            $p = Start-Process cmd.exe -ArgumentList '/c', "npx wrangler dev --local --port 8787 > `"$log`" 2>&1" -WorkingDirectory $serverDir -WindowStyle Hidden -PassThru
            $serverPid = $p.Id
            for ($i = 0; $i -lt 60 -and -not $up; $i++) {
                Start-Sleep 1
                try { $up = (Invoke-WebRequest "$url/api" -UseBasicParsing -TimeoutSec 2).StatusCode -eq 200 } catch { }
            }
            if (-not $up) { throw "로컬 서버가 뜨지 않았습니다. $log 를 확인하세요." }
        }
    }
    'Prod' { $url = '' }
    default { $url = $Server }
}

# ── 게임 ──
$gamePath = ''
if ($Game) {
    $gamePath = if ([IO.Path]::IsPathRooted($Game)) { $Game } else { Join-Path $root $Game }
    if (-not (Test-Path $gamePath)) { throw "게임 폴더 없음: $gamePath" }
}

# ── RocketRPG 띄우기 ──
$codeFile = Join-Path $work 'room_code.txt'
Remove-Item $codeFile -ErrorAction SilentlyContinue
$pids = @()
foreach ($n in $names) {
    $prof = Join-Path $work $n
    if ($Fresh -and (Test-Path $prof)) { Remove-Item $prof -Recurse -Force }
    New-Item -ItemType Directory -Force (Join-Path $prof 'config') | Out-Null
    Remove-Item (Join-Path $prof 'config\multi_resume.json') -ErrorAction SilentlyContinue   # 지난 시험 방으로 돌아가지 않게
    Remove-Item (Join-Path $prof 'config\ui.log') -ErrorAction SilentlyContinue
    $spec = if ($n -eq 'host') {
        "host|$gamePath|$(if ($Fps60) { '60' } else { '30' })$(if ($Control) { '|control' })"
    } else { "guest|$n" }
    # 환경 변수는 잠시 이 창에 두었다가 되돌림 (자식 프로세스가 물려받음). 콘솔 출력을 물려주지 않도록 Start-Process로 띄움.
    $saved = @{}
    $vars = @{ RR_PROFILE_ROOT = $prof; RR_MULTI_TEST = $spec; RR_MULTI_TEST_CODEFILE = $codeFile; RR_MULTI_SERVER = $url }
    foreach ($k in $vars.Keys) { $saved[$k] = [Environment]::GetEnvironmentVariable($k); [Environment]::SetEnvironmentVariable($k, $(if ($vars[$k]) { $vars[$k] } else { $null })) }
    try { $p = Start-Process $Exe -PassThru }
    finally { foreach ($k in $saved.Keys) { [Environment]::SetEnvironmentVariable($k, $saved[$k]) } }
    $pids += $p.Id
    Write-Host "[$n] pid $($p.Id)"
    if ($n -eq 'host') { Start-Sleep 4 }   # 방장이 방을 먼저 만들도록
}
@{ pids = $pids; server = $serverPid } | ConvertTo-Json | Set-Content $stateFile

# ── 창을 나란히 ──
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class RrMultiTestWin { [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int ht, bool r);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c); }
"@
Add-Type -AssemblyName System.Windows.Forms
$area = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea
$cols = [Math]::Min($pids.Count, 2); $rows = [Math]::Ceiling($pids.Count / $cols)
$w = [int]($area.Width / $cols); $h = [int]($area.Height / $rows)
for ($i = 0; $i -lt $pids.Count; $i++) {
    $hwnd = [IntPtr]::Zero
    for ($t = 0; $t -lt 40 -and $hwnd -eq [IntPtr]::Zero; $t++) {
        Start-Sleep -Milliseconds 250
        $proc = Get-Process -Id $pids[$i] -ErrorAction SilentlyContinue
        if ($proc) { $proc.Refresh(); $hwnd = $proc.MainWindowHandle }
    }
    if ($hwnd -eq [IntPtr]::Zero) { continue }
    [RrMultiTestWin]::ShowWindow($hwnd, 9) | Out-Null
    [RrMultiTestWin]::MoveWindow($hwnd, $area.X + ($i % $cols) * $w, $area.Y + [Math]::Floor($i / $cols) * $h, $w, $h, $true) | Out-Null
}

Write-Host ''
Write-Host "server: $(if ($url) { $url } else { 'production' })   game: $(if ($gamePath) { $gamePath } else { '(none)' })"
Write-Host "logs : pwsh -File scripts\multi_test.ps1 -Logs      stop: pwsh -File scripts\multi_test.ps1 -Stop"
