#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace RocketRPG.Models;

public class UpdateService
{
    public const string CurrentVersion = "1.1.5";
    const string RepoApiUrl = "https://api.github.com/repos/LocalXtend/RocketRPG-Release/releases/latest";
    // 베타도 받을 때: 최근 릴리즈 목록(시험판 포함)에서 가장 높은 버전
    const string RepoListUrl = "https://api.github.com/repos/LocalXtend/RocketRPG-Release/releases?per_page=30";

    /// <summary>지금 버전이 베타(시험판)인지 (예: 1.0.0-beta)</summary>
    public static bool IsPrerelease(string version) => version.Trim().TrimStart('v', 'V').Contains('-');

    /// <summary>설정에서 정하지 않았으면: 베타를 쓰고 있으면 베타도 받음</summary>
    public static bool WantsBeta(string channel) =>
        channel == "beta" || (channel != "stable" && IsPrerelease(CurrentVersion));

    public static bool IsInstalled()
    {
        try
        {
            var exeDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
            if (File.Exists(Path.Combine(exeDir, "unist000.exe")) || File.Exists(Path.Combine(exeDir, "unins000.exe")))
                return true;

            using var hklm = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\RocketRPG");
            if (hklm?.GetValue("InstallLocation") is string loc && !string.IsNullOrWhiteSpace(loc))
            {
                if (string.Equals(Path.GetFullPath(loc).TrimEnd('\\'), Path.GetFullPath(exeDir).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            using var hkcu = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\RocketRPG");
            if (hkcu?.GetValue("InstallLocation") is string loc2 && !string.IsNullOrWhiteSpace(loc2))
            {
                if (string.Equals(Path.GetFullPath(loc2).TrimEnd('\\'), Path.GetFullPath(exeDir).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch { }
        return false;
    }

    public class ReleaseInfo
    {
        [JsonPropertyName("tag_name")]
        public string TagName { get; set; } = "";

        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("body")]
        public string Body { get; set; } = "";

        [JsonPropertyName("html_url")]
        public string HtmlUrl { get; set; } = "";

        [JsonPropertyName("prerelease")]
        public bool Prerelease { get; set; }

        [JsonPropertyName("draft")]
        public bool Draft { get; set; }

        [JsonPropertyName("assets")]
        public List<AssetInfo> Assets { get; set; } = new();
    }

    public class AssetInfo
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("browser_download_url")]
        public string DownloadUrl { get; set; } = "";

        [JsonPropertyName("size")]
        public long Size { get; set; }
    }

    public class CheckResult
    {
        public bool HasUpdate { get; set; }
        public bool IsError { get; set; }
        public string Message { get; set; } = "";
        public ReleaseInfo? Release { get; set; }
        public AssetInfo? TargetAsset { get; set; }
        public bool IsInstalled { get; set; }
        /// <summary>업데이트 내용 (릴리즈 본문, 마크다운)</summary>
        public string ReleaseNotes { get; set; } = "";
    }

    /// <param name="includeBeta">베타(시험판) 릴리즈도 받을지 (설정 > 업데이트 받을 버전)</param>
    public static async Task<CheckResult> CheckForUpdateAsync(string? currentVersion = null, bool includeBeta = false)
    {
        currentVersion ??= CoreInterop.Version();
        try
        {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.UserAgent.ParseAdd("RocketRPG");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github.v3+json");

            if (includeBeta)
            {
                var listResp = await client.GetAsync(RepoListUrl);
                listResp.EnsureSuccessStatusCode();
                var all = JsonSerializer.Deserialize<List<ReleaseInfo>>(await listResp.Content.ReadAsStringAsync()) ?? new();
                var best = all.Where(r => !r.Draft && !string.IsNullOrWhiteSpace(r.TagName))
                              .Aggregate((ReleaseInfo?)null, (b, r) => b == null || IsNewerVersion(r.TagName, b.TagName) ? r : b);
                return Result(best, currentVersion);
            }

            var resp = await client.GetAsync(RepoApiUrl);
            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return new CheckResult
                {
                    HasUpdate = false,
                    Message = $"현재 최신 버전(v{currentVersion})을 사용 중입니다."
                };
            }

            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync();
            return Result(JsonSerializer.Deserialize<ReleaseInfo>(json), currentVersion);
        }
        catch (Exception ex)
        {
            return new CheckResult
            {
                HasUpdate = false,
                IsError = true,
                Message = $"업데이트 확인 중 오류가 발생했습니다:\n{ex.Message}"
            };
        }
    }

    static CheckResult Result(ReleaseInfo? release, string currentVersion)
    {
        if (release == null || string.IsNullOrWhiteSpace(release.TagName) || !IsNewerVersion(release.TagName, currentVersion))
        {
            return new CheckResult
            {
                HasUpdate = false,
                Message = $"현재 최신 버전(v{currentVersion})을 사용 중입니다."
            };
        }
        bool installed = IsInstalled();
        return new CheckResult
        {
            HasUpdate = true,
            Release = release,
            TargetAsset = SelectAsset(release, installed),
            IsInstalled = installed,
            ReleaseNotes = release.Body ?? "",
            Message = $"새로운 {(release.Prerelease ? "베타 " : "")}버전({release.TagName})이 출시되었습니다."
        };
    }

    /// <summary>
    /// 버전 비교 (x.y.z[-시험판]). 숫자가 같으면 정식이 베타보다 높고(1.0.0 > 1.0.0-beta),
    /// 베타끼리는 이름을 점으로 나눠 숫자는 숫자로, 글자는 글자로 비교합니다 (beta < beta.2 < rc).
    /// </summary>
    public static bool IsNewerVersion(string remoteTag, string currentVersion) => CompareVersions(remoteTag, currentVersion) > 0;

    public static int CompareVersions(string a, string b)
    {
        static (int[] core, string[] pre) Split(string v)
        {
            v = v.Trim().TrimStart('v', 'V');
            int plus = v.IndexOf('+');
            if (plus >= 0) v = v[..plus];
            int dash = v.IndexOf('-');
            string core = dash >= 0 ? v[..dash] : v, pre = dash >= 0 ? v[(dash + 1)..] : "";
            var nums = core.Split('.').Select(p => int.TryParse(p, out int n) ? n : 0).ToArray();
            return (nums, pre.Length == 0 ? [] : pre.Split('.'));
        }
        var (ac, ap) = Split(a);
        var (bc, bp) = Split(b);
        for (int i = 0; i < Math.Max(ac.Length, bc.Length); i++)
        {
            int x = i < ac.Length ? ac[i] : 0, y = i < bc.Length ? bc[i] : 0;
            if (x != y) return x.CompareTo(y);
        }
        if (ap.Length == 0 || bp.Length == 0) return (ap.Length == 0 ? 1 : 0) - (bp.Length == 0 ? 1 : 0);   // 정식 > 베타
        for (int i = 0; i < Math.Min(ap.Length, bp.Length); i++)
        {
            bool xn = int.TryParse(ap[i], out int xi), yn = int.TryParse(bp[i], out int yi);
            int c = xn && yn ? xi.CompareTo(yi) : xn ? -1 : yn ? 1 : string.CompareOrdinal(ap[i], bp[i]);
            if (c != 0) return Math.Sign(c);
        }
        return ap.Length.CompareTo(bp.Length);
    }

    public static AssetInfo? SelectAsset(ReleaseInfo release, bool isInstalled)
    {
        if (release.Assets == null || release.Assets.Count == 0) return null;
        if (isInstalled)
        {
            return release.Assets.FirstOrDefault(a => a.Name.Contains("installer", StringComparison.OrdinalIgnoreCase) && a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                ?? release.Assets.FirstOrDefault(a => a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            return release.Assets.FirstOrDefault(a => a.Name.Contains("portable", StringComparison.OrdinalIgnoreCase) && a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                ?? release.Assets.FirstOrDefault(a => a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>빠른 업데이트에 쓰는 포터블 zip (설치판도 같은 파일을 씀)</summary>
    public static AssetInfo? PortableAsset(ReleaseInfo release) =>
        release.Assets?.FirstOrDefault(a => a.Name.Contains("portable", StringComparison.OrdinalIgnoreCase) && a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && a.Size > 0);

    public static string AppDir() => Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;

    /// <summary>새 버전에서 바뀐 파일을 풀어 둘 임시 폴더</summary>
    public static string StagingDir(string tag) => Path.Combine(Path.GetTempPath(), "RocketRPG_update_" + string.Concat(tag.Where(char.IsLetterOrDigit)));

    static bool CanWrite(string dir)
    {
        try
        {
            string probe = Path.Combine(dir, $".rr_write_{Guid.NewGuid():N}");
            File.WriteAllBytes(probe, []);
            File.Delete(probe);
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// 풀어 둔 파일(staging)을 RocketRPG가 꺼진 뒤 앱 폴더에 덮어쓰고 다시 켭니다 (robocopy: 바뀐 파일만이라 몇 초면 끝남).
    /// 앱 폴더에 쓸 수 없으면(Program Files 설치) 관리자 권한을 한 번 묻습니다. 다시 켜는 RocketRPG는 관리자 권한 없이 켜집니다.
    /// 시작했으면 true (호출한 쪽이 앱을 끔), 사용자가 권한을 거절하는 등으로 시작하지 못하면 false.
    /// </summary>
    public static bool ApplyStagedUpdate(string staging, string version)
    {
        string appDir = AppDir();
        bool elevate = !CanWrite(appDir);
        string scriptPath = Path.Combine(Path.GetTempPath(), "RocketRPG_apply_update.ps1");
        const string script = @"# RocketRPG update: copy changed files after RocketRPG exits, then start it again
param([string]$Staging, [string]$TargetDir, [int]$ProcessId, [string]$Version, [int]$Elevated)
$log = Join-Path $env:TEMP 'RocketRPG_update.log'
function Log($s) { try { Add-Content -Path $log -Value ((Get-Date -Format 's') + ' ' + $s) -Encoding UTF8 } catch { } }
if ($ProcessId -gt 0) { try { Wait-Process -Id $ProcessId -Timeout 30 -ErrorAction SilentlyContinue } catch { } }
$ok = $false
for ($i = 0; $i -lt 5 -and -not $ok; $i++) {
    & robocopy $Staging $TargetDir /E /IS /IT /R:5 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null
    $ok = $LASTEXITCODE -lt 8
    if (-not $ok) { Log ('robocopy exit ' + $LASTEXITCODE); Start-Sleep -Seconds 2 }
}
if ($ok) {
    Log ('updated to ' + $Version)
    foreach ($root in 'HKLM:', 'HKCU:') {
        $k = $root + '\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\RocketRPG'
        try {
            $loc = (Get-ItemProperty -Path $k -ErrorAction Stop).InstallLocation
            if ($loc -and ($loc.TrimEnd('\') -ieq $TargetDir.TrimEnd('\'))) { Set-ItemProperty -Path $k -Name DisplayVersion -Value $Version -ErrorAction SilentlyContinue }
        } catch { }
    }
    Remove-Item -Recurse -Force $Staging -ErrorAction SilentlyContinue
}
$exe = Join-Path $TargetDir 'RocketRPG.exe'
if ($Elevated -eq 1) { Start-Process -FilePath 'explorer.exe' -ArgumentList ('""' + $exe + '""') }
else { Start-Process -FilePath $exe -WorkingDirectory $TargetDir }
Remove-Item -Force $PSCommandPath -ErrorAction SilentlyContinue
";
        File.WriteAllText(scriptPath, script, new System.Text.UTF8Encoding(true));
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{scriptPath}\" -Staging \"{staging}\" -TargetDir \"{appDir}\" " +
                        $"-ProcessId {Environment.ProcessId} -Version \"{version.TrimStart('v', 'V')}\" -Elevated {(elevate ? 1 : 0)}",
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            Verb = elevate ? "runas" : "",
        };
        try
        {
            Process.Start(psi);
            UiLog.Write($"update: applying {version} from {staging} (elevated {elevate})");
            return true;
        }
        catch (Exception ex)
        {
            UiLog.Write($"update: could not start the updater ({ex.Message})");
            return false;
        }
    }

    public static void ApplyInstallerUpdate(string installerPath)
    {
        var psi = new ProcessStartInfo(installerPath, "/update") { UseShellExecute = true };
        try { Process.Start(psi); } catch { }
        Application.Current.Shutdown();
    }

    public static void ApplyPortableUpdate(string zipPath)
    {
        var appDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
        int pid = Environment.ProcessId;
        string scriptPath = Path.Combine(Path.GetTempPath(), "RocketRPG_update.ps1");
        string psScript = @"# RocketRPG Portable Auto-Updater
param([string]$TargetDir, [string]$ZipPath, [int]$ProcessId)

if ($ProcessId -gt 0) {
    try {
        $p = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
        if ($p) { $p.WaitForExit(10000) }
    } catch { }
}
Start-Sleep -Milliseconds 500

$userCfg = Join-Path $TargetDir 'config\global.json'
$cfgBackup = $null
if (Test-Path $userCfg) {
    try { $cfgBackup = Get-Content $userCfg -Raw -Encoding UTF8 } catch { }
}

$tempExt = Join-Path $env:TEMP ('rr_upd_' + [System.Guid]::NewGuid().ToString('N'))
try {
    Expand-Archive -Path $ZipPath -DestinationPath $tempExt -Force
    $items = Get-ChildItem -Path $tempExt
    $sourceDir = if ($items.Count -eq 1 -and $items[0].PSIsContainer) { $items[0].FullName } else { $tempExt }
    
    $copied = $false
    for ($i = 0; $i -lt 10; $i++) {
        try {
            Copy-Item -Path (Join-Path $sourceDir '*') -Destination $TargetDir -Recurse -Force -ErrorAction Stop
            $copied = $true
            break
        } catch {
            Start-Sleep -Milliseconds 500
        }
    }
    if (-not $copied) {
        Copy-Item -Path (Join-Path $sourceDir '*') -Destination $TargetDir -Recurse -Force
    }

    if ($cfgBackup) {
        $targetCfgDir = Join-Path $TargetDir 'config'
        if (-not (Test-Path $targetCfgDir)) { New-Item -ItemType Directory -Path $targetCfgDir -Force | Out-Null }
        Set-Content -Path $userCfg -Value $cfgBackup -Encoding UTF8 -Force
    }
} catch {
    Start-Sleep -Seconds 1
    try {
        Expand-Archive -Path $ZipPath -DestinationPath $TargetDir -Force
        if ($cfgBackup) {
            Set-Content -Path $userCfg -Value $cfgBackup -Encoding UTF8 -Force
        }
    } catch { }
} finally {
    Remove-Item -Recurse -Force $tempExt -ErrorAction SilentlyContinue
    Remove-Item -Force $ZipPath -ErrorAction SilentlyContinue
}

$exe = Join-Path $TargetDir 'RocketRPG.exe'
if (Test-Path $exe) {
    Start-Process -FilePath $exe -WorkingDirectory $TargetDir
}

Remove-Item -Force $PSCommandPath -ErrorAction SilentlyContinue
";
        File.WriteAllText(scriptPath, psScript, System.Text.Encoding.UTF8);

        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{scriptPath}\" -TargetDir \"{appDir}\" -ZipPath \"{zipPath}\" -ProcessId {pid}",
            UseShellExecute = true,
            CreateNoWindow = true
        };
        Process.Start(psi);
        Application.Current.Shutdown();
    }
}
