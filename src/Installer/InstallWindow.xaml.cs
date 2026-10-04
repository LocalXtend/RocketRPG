#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Windows;

namespace RocketRPG.Installer;

public partial class InstallWindow : Window
{
    public const string TargetDir = @"C:\Program Files\RocketRPG";

    public InstallWindow()
    {
        InitializeComponent();
    }

    public static string GetInstalledDirectory()
    {
        try
        {
            using var hklm = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\RocketRPG");
            if (hklm?.GetValue("InstallLocation") is string loc && !string.IsNullOrWhiteSpace(loc) && Directory.Exists(loc))
                return loc;
        }
        catch { }
        try
        {
            using var hkcu = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\RocketRPG");
            if (hkcu?.GetValue("InstallLocation") is string loc2 && !string.IsNullOrWhiteSpace(loc2) && Directory.Exists(loc2))
                return loc2;
        }
        catch { }

        var selfDir = Path.GetDirectoryName(Environment.ProcessPath);
        if (!string.IsNullOrEmpty(selfDir) && Directory.Exists(selfDir))
        {
            if (File.Exists(Path.Combine(selfDir, "RocketRPG.exe")) ||
                File.Exists(Path.Combine(selfDir, "RocketRPGCore.dll")))
                return selfDir;
        }
        return TargetDir;
    }

    public static void RunUninstall()
    {
        try
        {
            if (MessageBox.Show("RocketRPG를 제거하시겠습니까?", "RocketRPG 제거",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            {
                return;
            }

            // 실행 중인 RocketRPG 프로세스 종료
            foreach (var p in Process.GetProcessesByName("RocketRPG"))
            {
                try { p.Kill(); p.WaitForExit(1000); } catch { }
            }

            string targetDir = GetInstalledDirectory();
            string selfPath = Environment.ProcessPath ?? "";
            bool selfInTarget = !string.IsNullOrEmpty(selfPath) &&
                                string.Equals(Path.GetDirectoryName(selfPath)!.TrimEnd('\\'), Path.GetFullPath(targetDir).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

            if (Directory.Exists(targetDir))
            {
                if (!selfInTarget)
                {
                    try { Directory.Delete(targetDir, true); } catch { }
                }
                else
                {
                    // 언인스톨러 자체를 제외한 나머지 파일 및 서브 디렉터리 정리
                    try
                    {
                        foreach (var f in Directory.EnumerateFiles(targetDir))
                        {
                            if (!string.Equals(f, selfPath, StringComparison.OrdinalIgnoreCase))
                                try { File.Delete(f); } catch { }
                        }
                        foreach (var d in Directory.EnumerateDirectories(targetDir))
                        {
                            try { Directory.Delete(d, true); } catch { }
                        }
                    }
                    catch { }

                    // 언인스톨러 자체 프로세스가 완전히 종료될 때까지 대기 후 대상 폴더 삭제
                    int pid = Environment.ProcessId;
                    string script = $"Start-Sleep -Milliseconds 300; while (Get-Process -Id {pid} -ErrorAction SilentlyContinue) {{ Start-Sleep -Milliseconds 300 }}; for ($i=0; $i -lt 10; $i++) {{ try {{ Remove-Item -Recurse -Force '{targetDir}' -ErrorAction Stop; break }} catch {{ Start-Sleep -Seconds 1 }} }}";
                    var psi = new ProcessStartInfo("powershell.exe", $"-NoProfile -NonInteractive -WindowStyle Hidden -Command \"{script}\"")
                    {
                        CreateNoWindow = true,
                        UseShellExecute = false
                    };
                    try { Process.Start(psi); } catch { }
                }
            }

            // 레지스트리 제거 (HKLM 및 HKCU)
            try
            {
                Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", true)?.DeleteSubKeyTree("RocketRPG", false);
            }
            catch { }
            try
            {
                Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", true)?.DeleteSubKeyTree("RocketRPG", false);
            }
            catch { }

            // 바로가기 제거
            string desktop = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "RocketRPG.lnk");
            if (File.Exists(desktop)) try { File.Delete(desktop); } catch { }
            string sm = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "RocketRPG");
            if (Directory.Exists(sm)) try { Directory.Delete(sm, true); } catch { }

            MessageBox.Show("제거가 완료되었습니다.", "RocketRPG 제거", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "제거 실패", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    void Log(string s) { LogText.Text += s + "\n"; Progress.Value = Math.Min(100, Progress.Value + 20); }

    void OnInstall(object sender, RoutedEventArgs e)
    {
        try
        {
            Cursor = System.Windows.Input.Cursors.Wait;
            bool ok = PerformInstall(s => Log(s), DesktopCheck.IsChecked == true, StartMenuCheck.IsChecked == true);
            if (ok)
            {
                Progress.Value = 100;
                MessageBox.Show(this, $"설치가 완료되었습니다.\n{TargetDir}", "RocketRPG 설치");
                Close();
            }
            else
            {
                MessageBox.Show(this, "설치 패키지(payload)를 찾을 수 없습니다.", "오류", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "설치 실패", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { Cursor = null; }
    }

    public static bool PerformInstall(Action<string>? log = null, bool createDesktop = true, bool createStartMenu = true)
    {
        void Log(string s) => log?.Invoke(s);

        // 1. 실행 중인 RocketRPG 프로세스 종료 (DLL 및 실행 파일 락 방지)
        Log("실행 중인 RocketRPG 프로세스 확인 중...");
        foreach (var p in Process.GetProcessesByName("RocketRPG"))
        {
            try { p.Kill(); p.WaitForExit(2000); } catch { }
        }

        // 2. 임베디드 리소스 또는 외부 payload 확인
        var asm = Assembly.GetExecutingAssembly();
        Stream? embeddedStream = asm.GetManifestResourceStream("payload.zip");
        if (embeddedStream == null)
        {
            var resName = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("payload.zip", StringComparison.OrdinalIgnoreCase));
            if (resName != null) embeddedStream = asm.GetManifestResourceStream(resName);
        }
        var payloadDir = FindPayload();

        if (embeddedStream == null && payloadDir == null)
        {
            return false;
        }

        Log("이전 버전 확인 및 설정 백업 중...");
        string existingCfgFile = Path.Combine(TargetDir, "config", "global.json");
        string appDataCfgFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RocketRPG", "global.json");
        string? backupJson = null;

        InstallConfig? cfgTarget = null;
        InstallConfig? cfgAppData = null;
        DateTime targetMtime = DateTime.MinValue;
        DateTime appDataMtime = DateTime.MinValue;

        if (File.Exists(existingCfgFile))
        {
            try
            {
                cfgTarget = JsonSerializer.Deserialize<InstallConfig>(File.ReadAllText(existingCfgFile));
                targetMtime = File.GetLastWriteTimeUtc(existingCfgFile);
            }
            catch { }
        }

        if (File.Exists(appDataCfgFile))
        {
            try
            {
                cfgAppData = JsonSerializer.Deserialize<InstallConfig>(File.ReadAllText(appDataCfgFile));
                appDataMtime = File.GetLastWriteTimeUtc(appDataCfgFile);
            }
            catch { }
        }

        // 설정 파일 전체를 그대로 보존합니다. 예전에는 몇 가지 값(기록·밝기·볼륨·비율·필터)만 아는 InstallConfig로 읽었다가
        // 그대로 다시 써서, 단축키·노트·글꼴·디스코드 같은 나머지 설정이 업데이트할 때마다 초기화되었습니다.
        // 어느 쪽 파일을 쓸지 고를 때와 기록(History)을 합칠 때만 InstallConfig를 씁니다.
        string? rawTarget = null, rawAppData = null;
        try { if (cfgTarget != null) rawTarget = File.ReadAllText(existingCfgFile); } catch { }
        try { if (cfgAppData != null) rawAppData = File.ReadAllText(appDataCfgFile); } catch { }

        string? bestRaw = null;
        List<string>? mergedHistory = null;
        if (cfgTarget != null && cfgAppData != null)
        {
            bool targetDef = cfgTarget.IsDefault();
            bool appDataDef = cfgAppData.IsDefault();
            if (targetDef && !appDataDef) bestRaw = rawAppData;
            else if (!targetDef && appDataDef) bestRaw = rawTarget;
            else if (!targetDef && !appDataDef)
            {
                bestRaw = targetMtime >= appDataMtime ? rawTarget : rawAppData;
                mergedHistory = new List<string>();
                void AddH(List<string>? list)
                {
                    if (list == null) return;
                    foreach (var h in list)
                    {
                        if (!string.IsNullOrWhiteSpace(h) && !mergedHistory.Contains(h))
                            mergedHistory.Add(h);
                    }
                }
                if (targetMtime >= appDataMtime) { AddH(cfgTarget.History); AddH(cfgAppData.History); }
                else { AddH(cfgAppData.History); AddH(cfgTarget.History); }
                if (mergedHistory.Count > 10) mergedHistory.RemoveRange(10, mergedHistory.Count - 10);
            }
            else bestRaw = targetMtime >= appDataMtime ? rawTarget : rawAppData;
        }
        else
        {
            bestRaw = rawTarget ?? rawAppData;
        }
        backupJson = MergeHistory(bestRaw, mergedHistory);

        Log("이전 바이너리 정리 중...");
        var oldBin = Path.Combine(TargetDir, "bin");
        if (Directory.Exists(oldBin)) try { Directory.Delete(oldBin, true); } catch { }
        var uiLog = Path.Combine(TargetDir, "config", "ui.log");
        if (File.Exists(uiLog)) try { File.Delete(uiLog); } catch { }

        foreach (var f in new[] { "RocketRPG.exe", "RocketRPG.dll", "unist000.exe", "unins000.exe", "RocketRPGCore.dll", "RocketRPGCore32.dll" })
        {
            var p = Path.Combine(TargetDir, f);
            SafeDeleteOrMove(p);
        }

        Log("파일 복사 및 설치 중...");
        Directory.CreateDirectory(TargetDir);

        if (embeddedStream != null)
        {
            using (embeddedStream)
            using (var archive = new ZipArchive(embeddedStream, ZipArchiveMode.Read))
            {
                foreach (var entry in archive.Entries)
                {
                    if (string.IsNullOrEmpty(entry.Name) && (entry.FullName.EndsWith("/") || entry.FullName.EndsWith("\\")))
                    {
                        Directory.CreateDirectory(Path.Combine(TargetDir, entry.FullName));
                        continue;
                    }
                    string destPath = Path.Combine(TargetDir, entry.FullName);
                    if (Path.GetFileName(destPath).Equals("global.json", StringComparison.OrdinalIgnoreCase) && File.Exists(destPath))
                        continue;

                    SafeExtractEntry(entry, destPath);
                }
            }
        }
        else if (payloadDir != null)
        {
            string srcDir = Directory.Exists(Path.Combine(payloadDir, "portable"))
                ? Path.Combine(payloadDir, "portable")
                : payloadDir;
            CopyAll(new DirectoryInfo(srcDir), new DirectoryInfo(TargetDir));
        }

        CleanOldTempFiles(TargetDir);

        // 사용자 설정 및 히스토리 복원 / 보존
        if (!string.IsNullOrWhiteSpace(backupJson))
        {
            try
            {
                string targetCfgDir = Path.Combine(TargetDir, "config");
                Directory.CreateDirectory(targetCfgDir);
                File.WriteAllText(Path.Combine(targetCfgDir, "global.json"), backupJson);
            }
            catch { }

            try
            {
                string appDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RocketRPG");
                Directory.CreateDirectory(appDataDir);
                File.WriteAllText(Path.Combine(appDataDir, "global.json"), backupJson);
            }
            catch { }
        }

        // 일반 사용자 권한에서도 config 쓰기가 가능하도록 권한 부여
        try
        {
            string targetCfgDir = Path.Combine(TargetDir, "config");
            if (Directory.Exists(targetCfgDir))
            {
                var dInfo = new DirectoryInfo(targetCfgDir);
                var sec = dInfo.GetAccessControl();
                var usersSid = new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.BuiltinUsersSid, null);
                sec.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                    usersSid,
                    System.Security.AccessControl.FileSystemRights.Modify,
                    System.Security.AccessControl.InheritanceFlags.ContainerInherit | System.Security.AccessControl.InheritanceFlags.ObjectInherit,
                    System.Security.AccessControl.PropagationFlags.None,
                    System.Security.AccessControl.AccessControlType.Allow));
                dInfo.SetAccessControl(sec);
            }
        }
        catch { }

        WriteUninstaller();

        if (createStartMenu) CreateShortcut("RocketRPG", ShortcutKind.StartMenu);
        if (createDesktop) CreateShortcut("RocketRPG", ShortcutKind.Desktop);

        Log("설치 완료!");
        return true;
    }

    public static string? FindPayload()
    {
        var exe = Path.GetDirectoryName(Environment.ProcessPath ?? AppContext.BaseDirectory) ?? AppContext.BaseDirectory;
        foreach (var cand in new[] { Path.Combine(exe, "payload"), Path.Combine(Path.GetDirectoryName(exe) ?? "", "payload"), Path.Combine(AppContext.BaseDirectory, "payload") })
            if (!string.IsNullOrEmpty(cand) && Directory.Exists(cand)) return cand;
        return null;
    }

    static void SafeDeleteOrMove(string path)
    {
        if (!File.Exists(path)) return;
        try
        {
            File.Delete(path);
        }
        catch
        {
            try
            {
                string temp = path + ".old." + Guid.NewGuid().ToString("N");
                File.Move(path, temp);
                try { File.Delete(temp); } catch { }
            }
            catch { }
        }
    }

    static void SafeExtractEntry(ZipArchiveEntry entry, string destPath)
    {
        string? dir = Path.GetDirectoryName(destPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        SafeDeleteOrMove(destPath);
        entry.ExtractToFile(destPath, overwrite: true);
    }

    static void CleanOldTempFiles(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return;
            foreach (var f in Directory.EnumerateFiles(dir, "*.old.*"))
            {
                try { File.Delete(f); } catch { }
            }
        }
        catch { }
    }

    static void CopyAll(DirectoryInfo src, DirectoryInfo dst)
    {
        dst.Create();
        foreach (var f in src.GetFiles())
        {
            string destFile = Path.Combine(dst.FullName, f.Name);
            // global.json이 이미 대상에 존재하면 덮어쓰지 않고 보존
            if (f.Name.Equals("global.json", StringComparison.OrdinalIgnoreCase) && File.Exists(destFile))
                continue;
            SafeDeleteOrMove(destFile);
            f.CopyTo(destFile, true);
        }
        foreach (var d in src.GetDirectories()) CopyAll(d, new DirectoryInfo(Path.Combine(dst.FullName, d.Name)));
    }

    public static void WriteUninstaller()
    {
        var self = Environment.ProcessPath ?? Assembly.GetExecutingAssembly().Location;
        var dest = Path.Combine(TargetDir, "unist000.exe");
        var oldDest = Path.Combine(TargetDir, "unins000.exe");
        if (File.Exists(oldDest)) try { File.Delete(oldDest); } catch { }

        if (!string.Equals(Path.GetFullPath(self), Path.GetFullPath(dest), StringComparison.OrdinalIgnoreCase))
        {
            SafeDeleteOrMove(dest);
            File.Copy(self, dest, true);
        }

        string ver = (FileVersionInfo.GetVersionInfo(self).FileVersion ?? "0.5.0").Trim();
        Microsoft.Win32.RegistryKey? rootKey = null;
        try
        {
            rootKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", true);
        }
        catch { }
        if (rootKey == null)
        {
            rootKey = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", true) ??
                Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
        }

        using (rootKey)
        {
            using var k = rootKey?.CreateSubKey("RocketRPG");
            if (k != null)
            {
                k.SetValue("DisplayName", "RocketRPG");
                k.SetValue("DisplayVersion", ver);
                k.SetValue("Publisher", "iyu.e");
                k.SetValue("InstallLocation", TargetDir);
                k.SetValue("UninstallString", $"\"{dest}\" /uninstall");
                k.SetValue("QuietUninstallString", $"\"{dest}\" /uninstall");
                k.SetValue("DisplayIcon", Path.Combine(TargetDir, "RocketRPG.exe"));
                k.SetValue("NoModify", 1);
                k.SetValue("NoRepair", 1);
            }
        }
    }

    public enum ShortcutKind { Desktop, StartMenu }

    public static void CreateShortcut(string name, ShortcutKind kind)
    {
        string dir = kind == ShortcutKind.Desktop
            ? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "RocketRPG");
        Directory.CreateDirectory(dir);
        var lnk = Path.Combine(dir, name + ".lnk");
        var t = new StreamWriter(lnk) { }; t.Close();
        // .lnk binary format is complex: use COM via dynamic-free approach
        try
        {
            var shType = Type.GetTypeFromProgID("WScript.Shell")!;
            dynamic shell = Activator.CreateInstance(shType)!;
            dynamic sc = shell.CreateShortcut(lnk);
            sc.TargetPath = Path.Combine(TargetDir, "RocketRPG.exe");
            sc.WorkingDirectory = TargetDir;
            sc.Description = "RPG Maker universal launcher";
            sc.Save();
        }
        catch
        {
            File.Delete(lnk); // leave no broken stub
        }
    }

    /// <summary>설정 JSON의 다른 값은 건드리지 않고 기록(History)만 바꿉니다.</summary>
    static string? MergeHistory(string? rawJson, List<string>? history)
    {
        if (string.IsNullOrWhiteSpace(rawJson) || history == null) return rawJson;
        try
        {
            if (System.Text.Json.Nodes.JsonNode.Parse(rawJson) is not System.Text.Json.Nodes.JsonObject obj) return rawJson;
            var arr = new System.Text.Json.Nodes.JsonArray();
            foreach (var h in history) arr.Add(h);
            obj["History"] = arr;
            return obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        }
        catch { return rawJson; }
    }
}

class InstallConfig
{
    public List<string> History { get; set; } = new();
    public double Gamma { get; set; } = 1.0;
    public int Volume { get; set; } = 100;
    public string Ratio { get; set; } = "none";
    public string Filter { get; set; } = "none";
    public bool IsDefault() => (History == null || History.Count == 0) &&
                               Math.Abs(Gamma - 1.0) < 0.001 &&
                               Volume == 100 &&
                               (string.IsNullOrEmpty(Ratio) || Ratio.Equals("none", StringComparison.OrdinalIgnoreCase)) &&
                               (string.IsNullOrEmpty(Filter) || Filter.Equals("none", StringComparison.OrdinalIgnoreCase));
}
