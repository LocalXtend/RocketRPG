# Builds the RocketRPG edition of EasyRPG Player into build/easyrpg/dist (used by setup_runtimes.ps1 / package.ps1).
# The modified Player source lives in https://github.com/LocalXtend/EasyRPG_Patch and is checked out to
# build/easyrpg/Player — edit, commit and push Player changes there. Run with pwsh (PowerShell 7).
#   -Ref <tag|branch>  check out a specific version first (e.g. v0.6.1); default: keep the current checkout
param([string]$Ref = '')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$work = Join-Path $root 'build\easyrpg'
$player = Join-Path $work 'Player'
$repo = 'https://github.com/LocalXtend/EasyRPG_Patch.git'
New-Item -ItemType Directory -Force $work | Out-Null

# 예전 방식(업스트림 복제본 + 패치 적용) 폴더면 EasyRPG_Patch 복제본으로 바꿉니다.
$origin = if (Test-Path (Join-Path $player '.git')) { git -C $player remote get-url origin 2>$null } else { '' }
if ($origin -notlike '*LocalXtend/EasyRPG_Patch*') {
    if (Test-Path $player) {
        $old = "$player.old-$(Get-Date -Format yyyyMMddHHmmss)"
        Rename-Item $player $old
        Write-Host "  moved the old Player folder to $old"
    }
    git -C $work clone -q $repo Player
    if ($LASTEXITCODE -ne 0) { throw "clone failed: $repo" }
}
# 커밋은 항상 LocalXtend 계정으로
git -C $player config user.name LocalXtend
git -C $player config user.email 285211027+LocalXtend@users.noreply.github.com
if ($Ref) {
    git -C $player fetch -q --tags origin
    git -C $player checkout -q $Ref
    if ($LASTEXITCODE -ne 0) { throw "checkout failed: $Ref" }
}

& (Join-Path $player 'rocketrpg\build.ps1') -Work $work
