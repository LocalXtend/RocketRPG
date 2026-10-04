#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace RocketRPG.Models;

/// <summary>
/// 멀티: 방장이 스팀 게임을 할 때 참가자도 같은 게임을 가지고 있는지 확인합니다 (사지 않은 게임을 함께 하는 논란을 피하려고).
/// 게임은 실행 exe와 게임 고유 데이터 파일(RPG_RT.ldb, Game.rgss3a, www/data/System.json 등) 두 파일의 SHA-256으로 알아봅니다.
/// exe만으로는 안 됩니다: MV/MZ(nw.js)와 XP/VX/Ace의 Game.exe는 게임마다 똑같은 파일입니다.
/// 해시 자체는 주고받지 않습니다. 방장이 무작위 값(nonce)을 보내면 참가자가 HMAC(두 해시, nonce)로 답해,
/// 파일을 가진 사람만 맞는 답을 만들 수 있습니다.
/// </summary>
public static class GameOwnership
{
    /// <summary>게임을 알아보는 정보. Key(두 해시)는 이 PC 밖으로 보내지 않습니다.</summary>
    public sealed record Identity(string InstallDir, string Sub, string ExeRel, long ExeSize, string DataRel, long DataSize, byte[] Key)
    {
        /// <summary>참가자가 자기 PC에서 같은 게임을 찾는 데 쓰는 정보 (해시 없음)</summary>
        public JsonObject Describe() => new()
        {
            ["installDir"] = InstallDir, ["sub"] = Sub, ["exe"] = ExeRel, ["exeSize"] = ExeSize, ["data"] = DataRel, ["dataSize"] = DataSize,
        };
    }

    /// <summary>스팀 라이브러리(steamapps\common\폴더) 안의 게임이면 (설치 폴더 이름, 그 안의 게임 폴더 상대 경로)</summary>
    public static (string installDir, string sub)? SteamLocation(string gameDir)
    {
        var parts = Path.GetFullPath(gameDir).TrimEnd('\\', '/').Split('\\', '/');
        for (int i = 0; i + 1 < parts.Length; i++)
        {
            if (parts[i].Equals("steamapps", StringComparison.OrdinalIgnoreCase) && parts[i + 1].Equals("common", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 2 >= parts.Length) return null;
                return (parts[i + 2], string.Join("/", parts.Skip(i + 3)));
            }
        }
        return null;
    }

    /// <summary>게임 고유 데이터 파일 (게임 폴더 기준 상대 경로). 엔진마다 게임 내용이 담긴 파일</summary>
    public static string? DataFile(string gameDir, int engine)
    {
        string[] candidates = engine switch
        {
            1 or 2 => ["RPG_RT.ldb"],
            3 => ["Game.rgssad", "Data/System.rxdata"],
            4 => ["Game.rgss2a", "Data/System.rvdata"],
            5 => ["Game.rgss3a", "Data/System.rvdata2"],
            6 or 7 => ["www/data/System.json", "data/System.json", "Game_boxed.exe"],   // _boxed: 데이터가 exe 안에 묶인 게임
            _ => ["Game.rgss3a", "Game.rgss2a", "Game.rgssad", "RPG_RT.ldb", "www/data/System.json", "data/System.json"],
        };
        return candidates.FirstOrDefault(c => File.Exists(Path.Combine(gameDir, c)));
    }

    static readonly Dictionary<string, (long size, DateTime time, byte[] hash)> HashCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>파일 SHA-256 (크기·수정 시각이 같으면 다시 계산하지 않음)</summary>
    public static byte[] FileHash(string path)
    {
        var fi = new FileInfo(path);
        lock (HashCache)
            if (HashCache.TryGetValue(fi.FullName, out var c) && c.size == fi.Length && c.time == fi.LastWriteTimeUtc) return c.hash;
        byte[] hash;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 20))
            hash = SHA256.HashData(fs);
        lock (HashCache) HashCache[fi.FullName] = (fi.Length, fi.LastWriteTimeUtc, hash);
        return hash;
    }

    /// <summary>두 파일로 만든 게임 열쇠 (exe 해시 + 데이터 해시)</summary>
    public static byte[] MakeKey(byte[] exeHash, byte[] dataHash) => SHA256.HashData(exeHash.Concat(dataHash).ToArray());

    /// <summary>방장의 지금 게임. 스팀 게임이 아니거나 파일을 못 찾으면 null (확인하지 않음). 큰 파일은 몇 초 걸릴 수 있음</summary>
    public static Identity? Identify(string gameDir, int engine, string exeName)
    {
        if (SteamLocation(gameDir) is not var (installDir, sub)) return null;
        string? data = DataFile(gameDir, engine);
        string exe = exeName.Length > 0 && File.Exists(Path.Combine(gameDir, exeName)) ? exeName
            : new[] { "Game.exe", "RPG_RT.exe", "nw.exe" }.FirstOrDefault(e => File.Exists(Path.Combine(gameDir, e))) ?? "";
        if (exe.Length == 0) return null;
        // 게임 데이터가 exe 안에 묶인 게임(Game_boxed.exe 등)은 데이터 파일이 따로 없음: exe가 곧 그 게임이라 exe로 확인
        data ??= exe;
        string exePath = Path.Combine(gameDir, exe), dataPath = Path.Combine(gameDir, data);
        return new Identity(installDir, sub, exe.Replace('\\', '/'), new FileInfo(exePath).Length, data, new FileInfo(dataPath).Length,
            MakeKey(FileHash(exePath), FileHash(dataPath)));
    }

    /// <summary>방장의 물음(nonce)에 대한 답: HMAC-SHA256(열쇠, nonce)</summary>
    public static string Proof(byte[] key, string nonce) => Convert.ToHexString(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(nonce)));

    public static string NewNonce() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

    /// <summary>이 PC의 스팀 라이브러리 폴더들 (스팀 설치 폴더 + libraryfolders.vdf에 적힌 곳)</summary>
    public static List<string> SteamLibraries()
    {
        var roots = new List<string>();
        // 개발용 (scripts\multi_test.ps1): 라이브러리 위치를 ';'로 지정
        if (Environment.GetEnvironmentVariable("RR_STEAM_LIBRARIES") is { Length: > 0 } test)
            return test.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        void Add(string? p)
        {
            if (string.IsNullOrWhiteSpace(p)) return;
            try
            {
                p = Path.GetFullPath(p.Replace('/', '\\'));
                if (Directory.Exists(Path.Combine(p, "steamapps")) && !roots.Contains(p, StringComparer.OrdinalIgnoreCase)) roots.Add(p);
            }
            catch { }
        }
        try
        {
            using var hkcu = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            Add(hkcu?.GetValue("SteamPath") as string);
            using var hklm = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam");
            Add(hklm?.GetValue("InstallPath") as string);
        }
        catch { }
        foreach (var steam in roots.ToList())
        {
            try
            {
                string vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
                if (!File.Exists(vdf)) continue;
                foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                    Add(m.Groups[1].Value.Replace("\\\\", "\\"));
            }
            catch { }
        }
        // 스팀 설정을 못 읽은 경우: 드라이브마다 흔한 자리
        foreach (var d in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
        {
            Add(Path.Combine(d.RootDirectory.FullName, "SteamLibrary"));
            Add(Path.Combine(d.RootDirectory.FullName, "Program Files (x86)", "Steam"));
            Add(Path.Combine(d.RootDirectory.FullName, "Steam"));
        }
        return roots;
    }

    /// <summary>방장이 알려 준 게임을 이 PC에서 찾음 (스팀 라이브러리의 같은 설치 폴더, 같은 크기의 exe·데이터 파일). 없으면 null</summary>
    public static string? FindLocalCopy(JsonObject want, IEnumerable<string>? libraries = null)
    {
        string installDir = want["installDir"]?.GetValue<string>() ?? "", sub = want["sub"]?.GetValue<string>() ?? "";
        string exe = want["exe"]?.GetValue<string>() ?? "", data = want["data"]?.GetValue<string>() ?? "";
        long exeSize = want["exeSize"]?.GetValue<long>() ?? -1, dataSize = want["dataSize"]?.GetValue<long>() ?? -1;
        if (!SafeRelative(installDir) || installDir.Contains('/') || installDir.Contains('\\') || (sub.Length > 0 && !SafeRelative(sub)) || !SafeRelative(exe) || !SafeRelative(data)) return null;
        foreach (var lib in libraries ?? SteamLibraries())
        {
            string dir = Path.Combine(lib, "steamapps", "common", installDir, sub.Replace('/', '\\'));
            try
            {
                var e = new FileInfo(Path.Combine(dir, exe.Replace('/', '\\')));
                var d = new FileInfo(Path.Combine(dir, data.Replace('/', '\\')));
                if (e.Exists && d.Exists && e.Length == exeSize && d.Length == dataSize) return dir;
            }
            catch { }
        }
        return null;
    }

    /// <summary>찾은 게임 폴더의 열쇠 (방장 것과 같은 방법)</summary>
    public static byte[] KeyOf(string dir, JsonObject want) =>
        MakeKey(FileHash(Path.Combine(dir, (want["exe"]?.GetValue<string>() ?? "").Replace('/', '\\'))),
                FileHash(Path.Combine(dir, (want["data"]?.GetValue<string>() ?? "").Replace('/', '\\'))));

    static bool SafeRelative(string p) =>
        p.Length > 0 && p.Length < 260 && !Path.IsPathRooted(p) && !p.Contains(':') && p.Split('/', '\\').All(s => s.Length > 0 && s != "." && s != "..");
}

/// <summary>
/// 방장 쪽 확인 상태: 지금 게임(세대 gen)마다 참가자에게 물음을 보내고, 맞게 답한 사람만 화면·조종을 받습니다.
/// 스팀 게임이 아니면(Required = false) 모두 그대로 받습니다.
/// </summary>
public sealed class OwnershipGate
{
    public GameOwnership.Identity? Game { get; private set; }
    public int Gen { get; private set; }
    /// <summary>스팀 게임을 알아보는 중 (그동안은 아무에게도 화면을 보내지 않음)</summary>
    public bool Pending { get; private set; }
    public bool Required => Game != null || Pending;
    readonly Dictionary<string, string> _nonces = new();          // 참가자 id → 보낸 물음
    readonly Dictionary<string, bool> _results = new();           // 참가자 id → 확인 결과

    /// <summary>새 게임 (null = 게임 없음 또는 스팀 게임 아님). 이전 결과는 모두 지움</summary>
    public void SetGame(GameOwnership.Identity? game, bool pending = false)
    {
        Game = game;
        Pending = game == null && pending;
        Gen++;
        _nonces.Clear();
        _results.Clear();
    }

    /// <summary>아직 묻지 않은 참가자에게 보낼 물음 (보낼 것이 없으면 null)</summary>
    public string? NonceFor(string guestId)
    {
        if (Game == null || _nonces.ContainsKey(guestId)) return null;
        return _nonces[guestId] = GameOwnership.NewNonce();
    }

    /// <summary>참가자의 답 확인. 이번 게임의 물음에 대한 답이 아니면 null (무시)</summary>
    public bool? Check(string guestId, int gen, string? proof)
    {
        if (Game == null || gen != Gen || !_nonces.TryGetValue(guestId, out var nonce) || _results.ContainsKey(guestId)) return null;
        bool ok = proof != null && CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(proof.ToUpperInvariant()), Encoding.ASCII.GetBytes(GameOwnership.Proof(Game.Key, nonce)));
        _results[guestId] = ok;
        return ok;
    }

    /// <summary>방장 화면·조종을 받아도 되는지</summary>
    public bool Allowed(string guestId) => !Required || (_results.TryGetValue(guestId, out bool ok) && ok);

    /// <summary>참가자 목록에 붙일 상태 ("" = 표시 안 함)</summary>
    public string Status(string guestId) =>
        !Required ? "" : _results.TryGetValue(guestId, out bool ok) ? ok ? "" : "게임 없음" : "게임 확인 중";

    /// <summary>나간 사람 정리</summary>
    public void Forget(IEnumerable<string> present)
    {
        var keep = new HashSet<string>(present);
        foreach (var id in _nonces.Keys.Where(k => !keep.Contains(k)).ToList()) { _nonces.Remove(id); _results.Remove(id); }
    }
}
