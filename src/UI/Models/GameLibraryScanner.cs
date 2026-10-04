#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace RocketRPG.Models;

public class ScannedGame
{
    public string Title { get; set; } = "";
    public string ExePath { get; set; } = "";
    public string ExeName { get; set; } = "";
    public string DirectoryPath { get; set; } = "";
    public int Engine { get; set; } = CoreInterop.EngineUnknown;
    public string EngineName { get; set; } = "Unknown";
    public ImageSource? IconSource { get; set; }
    public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? ExeName : $"{Title} ({ExeName})";
    public string DisplaySubtitle => $"{EngineName} • {DirectoryPath}";
}

public class GameLibraryScanner
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    [DllImport("shell32.dll", CharSet = CharSet.Auto)]
    private static extern int ExtractIconEx(string szFileName, int nIconIndex, IntPtr[]? phiconLarge, IntPtr[]? phiconSmall, int nIcons);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private const uint SHGFI_ICON = 0x000000100;
    private const uint SHGFI_LARGEICON = 0x000000000;

    private static ImageSource? _folderIcon;
    private static ImageSource? _upFolderIcon;
    private static ImageSource? _documentsIcon;
    private static ImageSource? _downloadsIcon;
    private static ImageSource? _steamIcon;

    /// <summary>
    /// Safely converts an unmanaged HICON into a fully detached, frozen WPF BitmapSource via PNG stream.
    /// Safely destroys the unmanaged HICON in finally block.
    /// </summary>
    public static BitmapSource? SafeHIconToBitmapSource(IntPtr hIcon)
    {
        if (hIcon == IntPtr.Zero) return null;
        try
        {
            using var ico = System.Drawing.Icon.FromHandle(hIcon);
            using var bmp = ico.ToBitmap();
            using var ms = new MemoryStream();
            bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            ms.Position = 0;

            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.StreamSource = ms;
            bi.EndInit();
            bi.Freeze();
            return bi;
        }
        catch
        {
            return null;
        }
        finally
        {
            try { DestroyIcon(hIcon); } catch { }
        }
    }

    public static ImageSource? GetFolderIcon()
    {
        if (_folderIcon != null) return _folderIcon;
        try
        {
            IntPtr[] large = new IntPtr[1];
            if (ExtractIconEx("shell32.dll", 3, large, null, 1) > 0 && large[0] != IntPtr.Zero)
            {
                var bs = SafeHIconToBitmapSource(large[0]);
                if (bs != null) { _folderIcon = bs; return bs; }
            }

            if (ExtractIconEx("imageres.dll", -3, large, null, 1) > 0 && large[0] != IntPtr.Zero)
            {
                var bs = SafeHIconToBitmapSource(large[0]);
                if (bs != null) { _folderIcon = bs; return bs; }
            }
        }
        catch { }
        return null;
    }

    public static ImageSource? GetUpFolderIcon()
    {
        if (_upFolderIcon != null) return _upFolderIcon;
        try
        {
            IntPtr[] large = new IntPtr[1];
            if (ExtractIconEx("shell32.dll", 45, large, null, 1) > 0 && large[0] != IntPtr.Zero)
            {
                var bs = SafeHIconToBitmapSource(large[0]);
                if (bs != null) { _upFolderIcon = bs; return bs; }
            }
        }
        catch { }
        return null;
    }

    public static ImageSource? GetDocumentsIcon()
    {
        if (_documentsIcon != null) return _documentsIcon;
        try
        {
            // 1. Native Windows 11 Shell icon via SHGetFileInfo
            string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (Directory.Exists(docs))
            {
                var sfi = new SHFILEINFO();
                IntPtr res = SHGetFileInfo(docs, 0, ref sfi, (uint)Marshal.SizeOf(sfi), SHGFI_ICON | SHGFI_LARGEICON);
                if (res != IntPtr.Zero && sfi.hIcon != IntPtr.Zero)
                {
                    var bs = SafeHIconToBitmapSource(sfi.hIcon);
                    if (bs != null) { _documentsIcon = bs; return bs; }
                }
            }

            // 2. Windows 11 imageres.dll Documents icon (-112)
            IntPtr[] large = new IntPtr[1];
            if (ExtractIconEx("imageres.dll", -112, large, null, 1) > 0 && large[0] != IntPtr.Zero)
            {
                var bs = SafeHIconToBitmapSource(large[0]);
                if (bs != null) { _documentsIcon = bs; return bs; }
            }

            // 3. Fallback: shell32.dll
            if (ExtractIconEx("shell32.dll", -235, large, null, 1) > 0 && large[0] != IntPtr.Zero)
            {
                var bs = SafeHIconToBitmapSource(large[0]);
                if (bs != null) { _documentsIcon = bs; return bs; }
            }

            // 4. Fallback search check on kernel32.dll (safe inspection)
            try
            {
                if (ExtractIconEx("kernel32.dll", 0, large, null, 1) > 0 && large[0] != IntPtr.Zero)
                {
                    var bs = SafeHIconToBitmapSource(large[0]);
                    if (bs != null) { _documentsIcon = bs; return bs; }
                }
            }
            catch { }
        }
        catch { }

        return GetFolderIcon();
    }

    public static ImageSource? GetDownloadsIcon()
    {
        if (_downloadsIcon != null) return _downloadsIcon;
        try
        {
            // 1. Native Windows 11 Shell icon via SHGetFileInfo
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string downloads = Path.Combine(userProfile, "Downloads");
            if (Directory.Exists(downloads))
            {
                var sfi = new SHFILEINFO();
                IntPtr res = SHGetFileInfo(downloads, 0, ref sfi, (uint)Marshal.SizeOf(sfi), SHGFI_ICON | SHGFI_LARGEICON);
                if (res != IntPtr.Zero && sfi.hIcon != IntPtr.Zero)
                {
                    var bs = SafeHIconToBitmapSource(sfi.hIcon);
                    if (bs != null) { _downloadsIcon = bs; return bs; }
                }
            }

            // 2. Windows 11 imageres.dll Downloads icon (-184)
            IntPtr[] large = new IntPtr[1];
            if (ExtractIconEx("imageres.dll", -184, large, null, 1) > 0 && large[0] != IntPtr.Zero)
            {
                var bs = SafeHIconToBitmapSource(large[0]);
                if (bs != null) { _downloadsIcon = bs; return bs; }
            }

            // 3. Fallback search check on kernel32.dll (safe inspection)
            try
            {
                if (ExtractIconEx("kernel32.dll", 0, large, null, 1) > 0 && large[0] != IntPtr.Zero)
                {
                    var bs = SafeHIconToBitmapSource(large[0]);
                    if (bs != null) { _downloadsIcon = bs; return bs; }
                }
            }
            catch { }
        }
        catch { }

        return GetFolderIcon();
    }

    public static string? GetSteamRoot()
    {
        string[] regKeys = new[]
        {
            @"HKEY_CURRENT_USER\Software\Valve\Steam",
            @"HKEY_LOCAL_MACHINE\SOFTWARE\Valve\Steam",
            @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam"
        };

        foreach (var key in regKeys)
        {
            try
            {
                var val = Registry.GetValue(key, "SteamPath", null) as string
                       ?? Registry.GetValue(key, "InstallPath", null) as string;
                if (!string.IsNullOrEmpty(val) && Directory.Exists(val))
                {
                    return val;
                }
            }
            catch { }
        }

        string[] defaultPaths = new[]
        {
            @"C:\Program Files (x86)\Steam",
            @"C:\Program Files\Steam"
        };
        foreach (var p in defaultPaths)
        {
            if (Directory.Exists(p)) return p;
        }

        return null;
    }

    public static ImageSource? GetSteamIcon()
    {
        if (_steamIcon != null) return _steamIcon;
        try
        {
            string? steamRoot = GetSteamRoot();
            if (!string.IsNullOrEmpty(steamRoot) && Directory.Exists(steamRoot))
            {
                string icoPath = Path.Combine(steamRoot, "steam.ico");
                if (File.Exists(icoPath))
                {
                    var img = ExtractIcon(icoPath);
                    if (img != null) { _steamIcon = img; return img; }
                }

                string exePath = Path.Combine(steamRoot, "Steam.exe");
                if (File.Exists(exePath))
                {
                    var img = ExtractIcon(exePath);
                    if (img != null) { _steamIcon = img; return img; }
                }
            }
        }
        catch { }
        return null;
    }

    private static readonly Dictionary<string, ImageSource?> _iconCache = new(StringComparer.OrdinalIgnoreCase);

    // 네이티브 게임 판별은 직렬화합니다 (탐색 중 폴더 이동 시 여러 스캔이 겹칠 수 있음).
    private static readonly object DetectLock = new();

    private static readonly HashSet<string> SkipFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".svn", ".vs", ".vscode", "node_modules", "AppData", "Application Data",
        "$Recycle.Bin", "System Volume Information", "Windows", "ProgramData",
        "Recovery", "Microsoft", "Temp", "logs", "cache", "Source", "Packages",
        "Steam", "SteamApps", "common",
        "Audio", "Graphics", "Data", "img", "fonts", "js", "plugins", "css",
        "icon", "save", "Save", "Pictures", "BGM", "BGS", "ME", "SE", "Titles",
        "Characters", "Battlers", "Panoramas", "System", "System2", "Title",
        "Windowskins", "Battle", "Animations", "Movie", "Movies", "Effect", "Voice"
    };

    private class QueueItem
    {
        public string Path { get; }
        public int Depth { get; }
        public QueueItem(string path, int depth)
        {
            Path = path;
            Depth = depth;
        }
    }

    public static List<string> DetectSteamLibraryFolders()
    {
        var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Registry lookup
        string[] regKeys = new[]
        {
            @"HKEY_CURRENT_USER\Software\Valve\Steam",
            @"HKEY_LOCAL_MACHINE\SOFTWARE\Valve\Steam",
            @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam"
        };

        string? steamRoot = null;
        foreach (var key in regKeys)
        {
            try
            {
                var val = Registry.GetValue(key, "SteamPath", null) as string
                       ?? Registry.GetValue(key, "InstallPath", null) as string;
                if (!string.IsNullOrEmpty(val) && Directory.Exists(val))
                {
                    steamRoot = val;
                    break;
                }
            }
            catch { }
        }

        if (string.IsNullOrEmpty(steamRoot))
        {
            var fallback = @"C:\Program Files (x86)\Steam";
            if (Directory.Exists(fallback)) steamRoot = fallback;
        }

        if (!string.IsNullOrEmpty(steamRoot))
        {
            var common = Path.Combine(steamRoot, "steamapps", "common");
            if (Directory.Exists(common)) dirs.Add(SettingsService.NormalizeGameDir(common));

            var vdfPath = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
            if (File.Exists(vdfPath))
            {
                try
                {
                    var lines = File.ReadAllLines(vdfPath);
                    var reg = new Regex("\"path\"\\s+\"([^\"]+)\"", RegexOptions.IgnoreCase);
                    foreach (var line in lines)
                    {
                        var m = reg.Match(line);
                        if (m.Success)
                        {
                            var libPath = m.Groups[1].Value.Replace(@"\\", @"\");
                            var libCommon = Path.Combine(libPath, "steamapps", "common");
                            if (Directory.Exists(libCommon)) dirs.Add(SettingsService.NormalizeGameDir(libCommon));
                        }
                    }
                }
                catch { }
            }
        }

        return dirs.ToList();
    }

    public static List<string> GetDefaultScanRoots(List<string>? customFolders = null)
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. Steam Common folders
        foreach (var steamDir in DetectSteamLibraryFolders())
        {
            roots.Add(steamDir);
        }

        // 2. Documents & Downloads
        try
        {
            var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (Directory.Exists(docs)) roots.Add(docs);
        }
        catch { }

        try
        {
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var downloads = Path.Combine(userProfile, "Downloads");
            if (Directory.Exists(downloads)) roots.Add(downloads);
        }
        catch { }

        // 3. Dev GameSample folder
        try
        {
            var sampleDir = Path.Combine(SettingsService.Root(), "GameSample");
            if (Directory.Exists(sampleDir)) roots.Add(sampleDir);
        }
        catch { }

        // 4. Custom folders
        if (customFolders != null)
        {
            foreach (var cf in customFolders)
            {
                if (!string.IsNullOrWhiteSpace(cf) && Directory.Exists(cf))
                    roots.Add(cf);
            }
        }

        return CollapseRoots(roots);
    }

    /// <summary>
    /// 경로 표기를 정규화하고, 다른 루트 안에 포함된 루트를 제거해 같은 폴더를 두 번 탐색하지 않게 합니다.
    /// </summary>
    public static List<string> CollapseRoots(IEnumerable<string> roots)
    {
        var norm = roots
            .Select(SettingsService.NormalizeGameDir)
            .Where(r => r.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(r => r.Length)
            .ToList();
        var result = new List<string>();
        foreach (var r in norm)
        {
            bool nested = result.Any(parent =>
                r.StartsWith(parent.EndsWith('\\') ? parent : parent + "\\", StringComparison.OrdinalIgnoreCase));
            if (!nested) result.Add(r);
        }
        return result;
    }

    public static string? ResolveFdPath()
    {
        string localTool = Path.Combine(SettingsService.Root(), "tools", "fd", "fd.exe");
        if (File.Exists(localTool)) return localTool;

        string wingetTool = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Links", "fd.exe");
        if (File.Exists(wingetTool)) return wingetTool;

        try
        {
            var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var part in pathEnv.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var cand = Path.Combine(part.Trim(), "fd.exe");
                if (File.Exists(cand)) return cand;
            }
        }
        catch { }

        return null;
    }

    public static List<ScannedGame> ScanWithFd(
        string fdPath,
        IEnumerable<string> scanRoots,
        Action<string>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        var games = new List<ScannedGame>();
        var seenGameDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in scanRoots)
        {
            if (cancellationToken.IsCancellationRequested) break;
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) continue;

            string rootName = Path.GetFileName(root.TrimEnd('\\', '/'));
            onProgress?.Invoke($"[fd] 초고속 탐색 중: {rootName}");

            try
            {
                // -i: 대소문자 무시 (game.ini, rpg_rt.ldb 등 소문자 배포본 누락 방지)
                // rpg_core.js / rmmz_core.js: package.json 없이 배포된 MV/MZ 게임도 탐지
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = fdPath,
                    Arguments = "-I -i -t f --exclude node_modules --exclude .git --exclude AppData --exclude \"$RECYCLE.BIN\" " +
                                "\"^(RPG_RT\\.ldb|RPG_RT\\.ini|Game\\.ini|package\\.json|Scripts\\.rxdata|Scripts\\.rvdata|Scripts\\.rvdata2|Game\\.rgssad|Game\\.rgss2a|Game\\.rgss3a|rpg_core\\.js|rmmz_core\\.js)$\" \"" + root + "\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = System.Text.Encoding.UTF8
                };

                using var proc = System.Diagnostics.Process.Start(psi);
                if (proc == null) continue;
                // stderr(권한 거부 경고 등)를 비워주지 않으면 파이프 버퍼가 가득 차 fd가 멈춥니다.
                proc.ErrorDataReceived += (_, _) => { };
                proc.BeginErrorReadLine();

                while (!proc.StandardOutput.EndOfStream)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        try { proc.Kill(); } catch { }
                        break;
                    }

                    string? line = proc.StandardOutput.ReadLine();
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    string candidateFile = line.Trim();
                    string? dir = Path.GetDirectoryName(candidateFile);
                    if (string.IsNullOrEmpty(dir)) continue;

                    string gameDir = dir;
                    // js/ → (www/) → 게임 루트, Data/ → 게임 루트
                    for (int up = 0; up < 2; up++)
                    {
                        string dirName = Path.GetFileName(gameDir);
                        if (!dirName.Equals("www", StringComparison.OrdinalIgnoreCase) &&
                            !dirName.Equals("Data", StringComparison.OrdinalIgnoreCase) &&
                            !dirName.Equals("js", StringComparison.OrdinalIgnoreCase) &&
                            !dirName.Equals("backup", StringComparison.OrdinalIgnoreCase))
                            break;
                        string? parent = Path.GetDirectoryName(gameDir);
                        if (string.IsNullOrEmpty(parent)) break;
                        gameDir = parent;
                    }

                    gameDir = SettingsService.NormalizeGameDir(gameDir);
                    if (!seenGameDirs.Add(gameDir)) continue;

                    var g = InspectGameDirectory(gameDir);
                    if (g != null)
                    {
                        games.Add(g);
                        onProgress?.Invoke($"발견 ({games.Count}개): {g.Title}");
                    }
                }

                proc.WaitForExit(5000);
            }
            catch { }
        }

        return games.OrderBy(g => g.Title).ToList();
    }

    /// <summary>
    /// Performs an ultra-fast, BFS-based directory scan for RPG Maker games.
    /// Traverses up to maxDepth (default 5 levels), prunes asset folders, and uses signature detection.
    /// </summary>
    public static List<ScannedGame> ScanDirectories(
        IEnumerable<string> scanRoots,
        Action<string>? onProgress = null,
        CancellationToken cancellationToken = default,
        int maxDepth = 5)
    {
        string? fdPath = ResolveFdPath();
        if (!string.IsNullOrEmpty(fdPath))
        {
            try
            {
                var fdResults = ScanWithFd(fdPath, scanRoots, onProgress, cancellationToken);
                if (fdResults.Count > 0 || cancellationToken.IsCancellationRequested)
                    return fdResults;
            }
            catch { }
        }

        var games = new List<ScannedGame>();
        var seenDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<QueueItem>();

        foreach (var root in scanRoots)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) continue;
            queue.Enqueue(new QueueItem(root, 0));
        }

        long lastReportMs = 0;

        while (queue.Count > 0)
        {
            if (cancellationToken.IsCancellationRequested) break;

            var item = queue.Dequeue();
            string currentDir = item.Path;
            int depth = item.Depth;

            if (seenDirs.Contains(currentDir)) continue;
            seenDirs.Add(currentDir);

            long now = Environment.TickCount64;
            if (now - lastReportMs > 100)
            {
                lastReportMs = now;
                string dirName = Path.GetFileName(currentDir.TrimEnd('\\', '/'));
                onProgress?.Invoke($"탐색 중 ({games.Count}개 발견): {dirName}");
            }

            // Check if this directory is an RPG Maker game
            if (HasRpgMakerSignature(currentDir))
            {
                var game = InspectGameDirectory(currentDir);
                if (game != null)
                {
                    games.Add(game);
                    // PRUNING: Once a game is found, do NOT recurse into its internal directories
                    continue;
                }
            }

            // Do not recurse beyond maxDepth
            if (depth >= maxDepth) continue;

            // Enumerate subdirectories safely
            string[] subDirs;
            try
            {
                subDirs = Directory.GetDirectories(currentDir);
            }
            catch
            {
                continue;
            }

            foreach (var sub in subDirs)
            {
                if (cancellationToken.IsCancellationRequested) break;

                string subName = Path.GetFileName(sub);
                if (string.IsNullOrEmpty(subName) || SkipFolders.Contains(subName))
                    continue;

                // Skip hidden / system folders
                try
                {
                    var di = new DirectoryInfo(sub);
                    if ((di.Attributes & FileAttributes.Hidden) != 0 || (di.Attributes & FileAttributes.System) != 0)
                    {
                        if (!subName.Equals("GameSample", StringComparison.OrdinalIgnoreCase))
                            continue;
                    }
                }
                catch { }

                queue.Enqueue(new QueueItem(sub, depth + 1));
            }
        }

        return games.OrderBy(g => g.Title).ToList();
    }

    /// <summary>
    /// Fast file-system check to determine if a directory could be an RPG Maker game.
    /// This prevents calling heavy operations on non-game directories.
    /// </summary>
    public static bool HasRpgMakerSignature(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return false;

            // 1. RPG Maker 2000 / 2003
            if (File.Exists(Path.Combine(dir, "RPG_RT.ldb")) || File.Exists(Path.Combine(dir, "RPG_RT.ini")))
                return true;

            // 2. RPG Maker XP / VX / VX Ace
            if (File.Exists(Path.Combine(dir, "Game.ini")) ||
                File.Exists(Path.Combine(dir, "Game.rgssad")) ||
                File.Exists(Path.Combine(dir, "Game.rgss2a")) ||
                File.Exists(Path.Combine(dir, "Game.rgss3a")))
                return true;

            string dataDir = Path.Combine(dir, "Data");
            if (Directory.Exists(dataDir))
            {
                if (File.Exists(Path.Combine(dataDir, "Scripts.rxdata")) ||
                    File.Exists(Path.Combine(dataDir, "Scripts.rvdata")) ||
                    File.Exists(Path.Combine(dataDir, "Scripts.rvdata2")) ||
                    File.Exists(Path.Combine(dataDir, "System.rxdata")) ||
                    File.Exists(Path.Combine(dataDir, "System.rvdata")) ||
                    File.Exists(Path.Combine(dataDir, "System.rvdata2")))
                    return true;
            }

            // 3. RPG Maker MV / MZ
            if (File.Exists(Path.Combine(dir, "package.json")))
            {
                if (File.Exists(Path.Combine(dir, "www", "data", "System.json")) ||
                    File.Exists(Path.Combine(dir, "data", "System.json")) ||
                    File.Exists(Path.Combine(dir, "www", "js", "rpg_core.js")) ||
                    File.Exists(Path.Combine(dir, "www", "js", "rmmz_core.js")) ||
                    File.Exists(Path.Combine(dir, "js", "rpg_core.js")) ||
                    File.Exists(Path.Combine(dir, "js", "rmmz_core.js")) ||
                    File.Exists(Path.Combine(dir, "index.html")) ||
                    File.Exists(Path.Combine(dir, "www", "index.html")))
                    return true;
            }

            if (File.Exists(Path.Combine(dir, "www", "data", "System.json")) ||
                File.Exists(Path.Combine(dir, "data", "System.json")))
                return true;
        }
        catch { }

        return false;
    }

    public static ScannedGame? InspectGameDirectory(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return null;

            var info = CoreInterop.GameInfo.Create();
            string exeFromCore;
            lock (DetectLock)
            {
                CoreInterop.rpg_detect_game(dir, ref info);
                exeFromCore = info.ExeName;
            }

            int engine = info.Engine;
            if (engine == CoreInterop.EngineUnknown)
            {
                // Fallback detection
                if (File.Exists(Path.Combine(dir, "RPG_RT.ldb")) || File.Exists(Path.Combine(dir, "RPG_RT.ini")))
                    engine = CoreInterop.Engine2000;
                else if (File.Exists(Path.Combine(dir, "Data", "Scripts.rxdata")))
                    engine = CoreInterop.EngineXp;
                else if (File.Exists(Path.Combine(dir, "Data", "Scripts.rvdata")))
                    engine = CoreInterop.EngineVx;
                else if (File.Exists(Path.Combine(dir, "Data", "Scripts.rvdata2")))
                    engine = CoreInterop.EngineAce;
                else if (File.Exists(Path.Combine(dir, "www", "data", "System.json")))
                    engine = CoreInterop.EngineMv;
                else if (File.Exists(Path.Combine(dir, "data", "System.json")))
                    engine = CoreInterop.EngineMz;
            }

            if (engine == CoreInterop.EngineUnknown) return null;

            string title = info.TitleUtf8;
            if (string.IsNullOrWhiteSpace(title))
            {
                title = ReadGameTitle(dir, engine);
            }
            if (string.IsNullOrWhiteSpace(title))
            {
                title = Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            }

            // Find executable
            string exeName = exeFromCore;
            string exePath = "";
            if (!string.IsNullOrWhiteSpace(exeName))
            {
                var candidate = Path.Combine(dir, exeName);
                if (File.Exists(candidate)) exePath = candidate;
            }

            if (string.IsNullOrEmpty(exePath))
            {
                string[] exes = Array.Empty<string>();
                try { exes = Directory.GetFiles(dir, "*.exe"); } catch { }

                if (exes.Length > 0)
                {
                    var preferred = exes.FirstOrDefault(e =>
                    {
                        var fn = Path.GetFileName(e);
                        return fn.Equals("Game.exe", StringComparison.OrdinalIgnoreCase) ||
                               fn.Equals("RPG_RT.exe", StringComparison.OrdinalIgnoreCase);
                    }) ?? exes[0];

                    exePath = preferred;
                    exeName = Path.GetFileName(preferred);
                }
                else
                {
                    exeName = (engine == CoreInterop.Engine2000 || engine == CoreInterop.Engine2003) ? "RPG_RT.exe" : "Game.exe";
                }
            }

            var scanned = new ScannedGame
            {
                Title = title,
                ExePath = exePath,
                ExeName = exeName,
                DirectoryPath = dir,
                Engine = engine,
                EngineName = GetEngineDisplayName(engine),
                IconSource = !string.IsNullOrEmpty(exePath) ? ExtractIcon(exePath) : null
            };

            return scanned;
        }
        catch
        {
            return null;
        }
    }

    private static string ReadGameTitle(string dir, int engine)
    {
        try
        {
            var iniPath = Path.Combine(dir, "Game.ini");
            if (File.Exists(iniPath))
            {
                foreach (var line in File.ReadAllLines(iniPath))
                {
                    var trimmed = line.Trim();
                    if (trimmed.StartsWith("Title=", StringComparison.OrdinalIgnoreCase))
                    {
                        var t = trimmed.Substring(6).Trim();
                        if (!string.IsNullOrWhiteSpace(t)) return t;
                    }
                }
            }
        }
        catch { }

        try
        {
            var riniPath = Path.Combine(dir, "RPG_RT.ini");
            if (File.Exists(riniPath))
            {
                foreach (var line in File.ReadAllLines(riniPath))
                {
                    var trimmed = line.Trim();
                    if (trimmed.StartsWith("GameTitle=", StringComparison.OrdinalIgnoreCase))
                    {
                        var t = trimmed.Substring(10).Trim();
                        if (!string.IsNullOrWhiteSpace(t)) return t;
                    }
                }
            }
        }
        catch { }

        try
        {
            var pkgPath = Path.Combine(dir, "package.json");
            if (File.Exists(pkgPath))
            {
                var text = File.ReadAllText(pkgPath);
                var reg = new Regex("\"title\"\\s*:\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase);
                var m = reg.Match(text);
                if (m.Success) return m.Groups[1].Value;

                var regName = new Regex("\"name\"\\s*:\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase);
                var m2 = regName.Match(text);
                if (m2.Success) return m2.Groups[1].Value;
            }
        }
        catch { }

        return "";
    }

    public static string GetEngineDisplayName(int engine) => engine switch
    {
        CoreInterop.Engine2000 => "RPG Maker 2000",
        CoreInterop.Engine2003 => "RPG Maker 2003",
        CoreInterop.EngineXp => "RPG Maker XP",
        CoreInterop.EngineVx => "RPG Maker VX",
        CoreInterop.EngineAce => "RPG Maker VX Ace",
        CoreInterop.EngineMv => "RPG Maker MV",
        CoreInterop.EngineMz => "RPG Maker MZ",
        _ => "RPG Maker"
    };

    /// <summary>
    /// 셸 아이콘 API(SHGetFileInfo/ExtractIconEx)는 COM이 초기화된 STA 스레드에서만 안전합니다.
    /// 스레드 풀(MTA)에서 호출하던 것을 전용 STA 스레드 하나로 모아 직렬 처리합니다.
    /// </summary>
    private static class StaIconWorker
    {
        static readonly System.Collections.Concurrent.BlockingCollection<Action> Queue = new();
        static readonly Thread Worker = Start();

        static Thread Start()
        {
            var t = new Thread(() =>
            {
                foreach (var job in Queue.GetConsumingEnumerable())
                {
                    try { job(); } catch { }
                }
            })
            { IsBackground = true, Name = "RocketRPG.IconWorker" };
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            return t;
        }

        public static T? Run<T>(Func<T?> work) where T : class
        {
            if (Thread.CurrentThread == Worker) return work();
            T? result = null;
            using var done = new ManualResetEventSlim(false);
            Queue.Add(() => { try { result = work(); } finally { done.Set(); } });
            done.Wait(TimeSpan.FromSeconds(5));
            return result;
        }
    }

    public static ImageSource? ExtractIcon(string exePath)
    {
        if (string.IsNullOrEmpty(exePath)) return null;

        lock (_iconCache)
        {
            if (_iconCache.TryGetValue(exePath, out var cached))
                return cached;
        }

        var result = StaIconWorker.Run(() => ExtractIconCore(exePath));
        lock (_iconCache)
        {
            _iconCache[exePath] = result;
        }
        return result;
    }

    private static ImageSource? ExtractIconCore(string exePath)
    {
        ImageSource? result = null;
        try
        {
            if (File.Exists(exePath))
            {
                var sfi = new SHFILEINFO();
                IntPtr res = SHGetFileInfo(exePath, 0, ref sfi, (uint)Marshal.SizeOf(sfi), SHGFI_ICON | SHGFI_LARGEICON);
                if (res != IntPtr.Zero && sfi.hIcon != IntPtr.Zero)
                {
                    result = SafeHIconToBitmapSource(sfi.hIcon);
                }

                if (result == null)
                {
                    IntPtr[] large = new IntPtr[1];
                    if (ExtractIconEx(exePath, 0, large, null, 1) > 0 && large[0] != IntPtr.Zero)
                    {
                        result = SafeHIconToBitmapSource(large[0]);
                    }
                }
            }
        }
        catch { }

        lock (_iconCache)
        {
            _iconCache[exePath] = result;
        }
        return result;
    }
}
