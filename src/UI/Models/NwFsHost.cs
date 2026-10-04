#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace RocketRPG.Models;

/// <summary>
/// WebView2 안의 MV/MZ 게임에 NW.js와 같은 파일 접근(require('fs'))을 제공하는 호스트 객체.
/// NW.js에서 게임은 www/save 폴더에 직접 저장하지만, 일반 웹 페이지로 돌리면 모든 게임이 같은 localStorage를
/// 나눠 써서 세이브가 섞이고 원래 세이브도 보이지 않습니다. 이 객체로 실제 게임 폴더에 읽고 씁니다.
/// 접근은 현재 게임 폴더 안으로만 제한합니다. 메서드는 JS 쪽 shim(NwShimScript)이 동기 호출합니다.
/// </summary>
[ComVisible(true)]
[ClassInterface(ClassInterfaceType.AutoDual)]
public class NwFsHost
{
    string _root = "";
    string? _overlay;

    /// <summary>현재 게임 폴더 (StartGameAsync에서 설정). readOverlay: 게임 파일을 푼 캐시 — 읽기만 여기로 대체합니다.</summary>
    public void SetRoot(string root, string? readOverlay = null)
    {
        _root = Path.GetFullPath(root).TrimEnd('\\', '/');
        _overlay = string.IsNullOrEmpty(readOverlay) ? null : Path.GetFullPath(readOverlay).TrimEnd('\\', '/');
        if (string.Equals(_overlay, _root, StringComparison.OrdinalIgnoreCase)) _overlay = null;
        _steamLanguage = SteamLanguage.ForGame(root);
        if (_steamLanguage != null) UiLog.Write($"NwFsHost: Steam install, game language '{_steamLanguage}'");
    }

    string? _steamLanguage;

    /// <summary>스팀으로 설치한 게임이면 그 게임의 스팀 언어 이름("koreana" 등), 아니면 빈 문자열 (greenworks 대용)</summary>
    public string GetSteamLanguage() => _steamLanguage ?? "";

    /// <summary>읽기용: 게임 폴더에 없으면 푼 캐시의 같은 경로</summary>
    string ResolveRead(string path)
    {
        string f = Resolve(path);
        if (_overlay == null || File.Exists(f) || Directory.Exists(f)) return f;
        string alt = _overlay + f.Substring(_root.Length);
        return File.Exists(alt) || Directory.Exists(alt) ? alt : f;
    }

    /// <summary>게임 폴더 (JS의 process.mainModule.filename 기준)</summary>
    public string Root() => _root.Replace('\\', '/');

    string Resolve(string path)
    {
        if (string.IsNullOrEmpty(_root)) throw new UnauthorizedAccessException("no game");
        string p = path.Replace('/', '\\');
        // 드라이브 없는 절대 경로("/save/")는 게임 폴더 기준 (구버전 MV가 location.pathname으로 만든 경로)
        bool hasDrive = p.Length >= 2 && p[1] == ':';
        if (!hasDrive && p.StartsWith('\\')) p = p.TrimStart('\\');
        string full = Path.GetFullPath(hasDrive ? p : Path.Combine(_root, p));
        if (!full.Equals(_root, StringComparison.OrdinalIgnoreCase) &&
            !full.StartsWith(_root + "\\", StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException($"EACCES: outside game folder: {path}");
        return full;
    }

    // 오류는 JSON {"e":"코드: 메시지"} 로 돌려 JS 쪽에서 Node와 같은 모양의 Error로 던집니다.
    static string Err(Exception ex) => JsonSerializer.Serialize(new
    {
        e = ex switch
        {
            FileNotFoundException or DirectoryNotFoundException => "ENOENT: no such file or directory",
            UnauthorizedAccessException => "EACCES: " + ex.Message,
            IOException => "EIO: " + ex.Message,
            _ => ex.Message
        }
    });

    public bool Exists(string path)
    {
        try { var f = ResolveRead(path); return File.Exists(f) || Directory.Exists(f); } catch { return false; }
    }

    public string ReadText(string path)
    {
        try { return JsonSerializer.Serialize(new { d = File.ReadAllText(ResolveRead(path), new UTF8Encoding(false)) }); }
        catch (Exception ex) { return Err(ex); }
    }

    public string ReadBase64(string path)
    {
        try { return JsonSerializer.Serialize(new { d = Convert.ToBase64String(File.ReadAllBytes(ResolveRead(path))) }); }
        catch (Exception ex) { return Err(ex); }
    }

    public string WriteText(string path, string text, bool append)
    {
        try
        {
            string f = Resolve(path);
            if (append) File.AppendAllText(f, text, new UTF8Encoding(false));
            else File.WriteAllText(f, text, new UTF8Encoding(false));
            return "{}";
        }
        catch (Exception ex) { return Err(ex); }
    }

    public string WriteBase64(string path, string b64)
    {
        try { File.WriteAllBytes(Resolve(path), Convert.FromBase64String(b64)); return "{}"; }
        catch (Exception ex) { return Err(ex); }
    }

    public string Mkdir(string path)
    {
        try { Directory.CreateDirectory(Resolve(path)); return "{}"; }
        catch (Exception ex) { return Err(ex); }
    }

    public string Unlink(string path)
    {
        try
        {
            string f = Resolve(path);
            if (!File.Exists(f)) throw new FileNotFoundException(path);
            File.Delete(f);
            return "{}";
        }
        catch (Exception ex) { return Err(ex); }
    }

    public string Rmdir(string path)
    {
        try { Directory.Delete(Resolve(path), false); return "{}"; }
        catch (Exception ex) { return Err(ex); }
    }

    public string Rename(string from, string to)
    {
        try
        {
            string a = Resolve(from), b = Resolve(to);
            if (Directory.Exists(a)) Directory.Move(a, b);
            else File.Move(a, b, true);
            return "{}";
        }
        catch (Exception ex) { return Err(ex); }
    }

    public string Readdir(string path)
    {
        try
        {
            var names = Directory.EnumerateFileSystemEntries(ResolveRead(path)).Select(Path.GetFileName).ToArray();
            return JsonSerializer.Serialize(new { d = names });
        }
        catch (Exception ex) { return Err(ex); }
    }

    public string Stat(string path)
    {
        try
        {
            string f = ResolveRead(path);
            if (Directory.Exists(f))
            {
                var di = new DirectoryInfo(f);
                return JsonSerializer.Serialize(new { d = new { dir = true, size = 0L, mtime = new DateTimeOffset(di.LastWriteTimeUtc).ToUnixTimeMilliseconds() } });
            }
            var fi = new FileInfo(f);
            if (!fi.Exists) throw new FileNotFoundException(path);
            return JsonSerializer.Serialize(new { d = new { dir = false, size = fi.Length, mtime = new DateTimeOffset(fi.LastWriteTimeUtc).ToUnixTimeMilliseconds() } });
        }
        catch (Exception ex) { return Err(ex); }
    }
}
