# 배포 폴더 라이선스·구성 점검 (L01/L05)
#   pwsh -File scripts\license_audit.ps1                 dist\portable 점검 → build\license_audit.md
#   pwsh -File scripts\license_audit.ps1 -Dir <폴더>
# 하는 일:
#   - 배포 파일마다 SHA-256과 어느 구성요소(src\Licenses\components.json)에 속하는지 표로 남김
#   - 목록에 없는 파일(출처 미확인), 라이선스 원문이 없는 구성요소, 들어가면 안 되는 파일(RTP·게임 데이터·
#     RPG Maker 원본 실행 파일/DLL·재배포 불가 글꼴)을 찾으면 실패(종료 코드 1)
param([string]$Dir = '', [string]$Out = '')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if (-not $Dir) { $Dir = Join-Path $root 'dist\portable' }
if (-not $Out) { $Out = Join-Path $root 'build\license_audit.md' }
if (-not (Test-Path $Dir)) { throw "배포 폴더가 없습니다: $Dir (scripts\package.ps1로 먼저 만드세요)" }
$Dir = (Resolve-Path $Dir).Path
$manifest = Get-Content (Join-Path $root 'src\Licenses\components.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$texts = Join-Path $root 'src\Licenses\texts'

# 배포 대상이 아닌 사용자 데이터 (실행하면 생기는 것)
$skip = '^(config|crash|screenshots|webview2_data|multi_webview2|asset_webview2|notes|logs|licenses)\\'
$files = Get-ChildItem $Dir -Recurse -File | ForEach-Object { $_.FullName.Substring($Dir.Length + 1) } | Where-Object { $_ -notmatch $skip }

function Owner([string]$rel) {
    foreach ($c in $manifest.components) {
        foreach ($pat in @($c.files)) {
            if ($rel -like ($pat -replace '/', '\')) { return $c }
        }
    }
    return $null
}

$problems = @()
$rows = @()
foreach ($f in $files | Sort-Object) {
    $c = Owner $f
    $hash = (Get-FileHash (Join-Path $Dir $f) -Algorithm SHA256).Hash.ToLower()
    $size = (Get-Item (Join-Path $Dir $f)).Length
    if (-not $c) { $problems += "출처 미확인: $f" }
    $rows += [pscustomobject]@{ File = $f; Size = $size; Sha = $hash; Comp = if ($c) { $c.name } else { '(없음)' }; Lic = if ($c) { $c.license } else { '' } }
}

# 들어가면 안 되는 것: RPG Maker 원본 런타임·RTP·게임 데이터·Microsoft 글꼴
$forbidden = @(
    @{ Re = '(^|\\)(RPG_RT\.exe|RGSS\d+\w*\.dll|Game\.ini|RPG_RT\.ini)$'; Why = 'RPG Maker 원본 실행 파일/설정' },
    @{ Re = '\.(rgssad|rgss2a|rgss3a|rxdata|rvdata2?|lmu|ldb|lmt|lsd|xyz|rpgmvp|rpgmvo|rpgmvm|png_|ogg_|m4a_)$'; Why = '게임 데이터' },
    @{ Re = '(^|\\)(RTP|GameRTPSample|CharSet|ChipSet|Battle2?|FaceSet|Graphics|Audio)\\'; Why = 'RTP·게임 그림/소리 폴더' },
    @{ Re = '(^|\\)(malgun\w*|arial\w*|gulim\w*|batang\w*|msgothic\w*|meiryo\w*)\.tt[cf]$'; Why = '재배포할 수 없는 Windows 글꼴' }
)
foreach ($f in $files) {
    foreach ($rule in $forbidden) { if ($f -match $rule.Re) { $problems += "포함하면 안 됨 ($($rule.Why)): $f" } }
}
foreach ($c in $manifest.components) {
    foreach ($t in @($c.texts)) { if (-not (Test-Path (Join-Path $texts $t))) { $problems += "라이선스 원문 없음: $($c.name) → texts\$t" } }
}
if (-not (Test-Path (Join-Path $Dir 'licenses\THIRD_PARTY_NOTICES.md'))) { $problems += '배포 폴더에 licenses\THIRD_PARTY_NOTICES.md 없음' }

$md = @("# 배포 구성 점검 ($(Get-Date -Format 'yyyy-MM-dd HH:mm'))", '', "대상: ``$Dir``", '')
$md += if ($problems) { @('## 문제', '') + ($problems | ForEach-Object { "- $_" }) + '' } else { @('## 문제', '', '- 없음', '') }
$md += @('## 구성요소', '', '| 구성요소 | 버전 | 라이선스 | 소스 | 수정 |', '|---|---|---|---|---|')
$md += $manifest.components | ForEach-Object { "| $($_.name) | $($_.version) | $($_.license) | $($_.source) | $(if ($_.modified) { '수정' } else { '' }) |" }
$md += @('', '## 파일', '', '| 파일 | 크기 | SHA-256 | 구성요소 |', '|---|---:|---|---|')
$md += $rows | ForEach-Object { "| $($_.File) | $($_.Size) | ``$($_.Sha)`` | $($_.Comp) |" }
New-Item -ItemType Directory -Force (Split-Path $Out) | Out-Null
Set-Content -Path $Out -Value $md -Encoding UTF8
Write-Host "files: $($files.Count), problems: $($problems.Count) -> $Out"
$problems | ForEach-Object { Write-Host "  $_" -ForegroundColor Yellow }
if ($problems) { exit 1 }
