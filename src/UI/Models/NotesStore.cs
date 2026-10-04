#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Threading;

namespace RocketRPG.Models;

/// <summary>게임 노트의 한 항목: 메모(text), 글상자(label, 배경 없는 글), 이미지/GIF(image). 좌표는 캔버스(무한 평면) 좌표입니다.</summary>
public sealed class NoteItem
{
    [JsonPropertyName("id")] public string Id { get; set; } = Guid.NewGuid().ToString("N");
    [JsonPropertyName("kind")] public string Kind { get; set; } = "text";   // text | label | image
    [JsonPropertyName("x")] public double X { get; set; }
    [JsonPropertyName("y")] public double Y { get; set; }
    [JsonPropertyName("w")] public double W { get; set; } = 220;
    [JsonPropertyName("h")] public double H { get; set; } = 140;
    [JsonPropertyName("z")] public int Z { get; set; }
    [JsonPropertyName("text")] public string Text { get; set; } = "";
    [JsonPropertyName("color")] public string Color { get; set; } = "#FFFFF4B0";   // 메모지 색
    [JsonPropertyName("font_size")] public double FontSize { get; set; } = 13;
    [JsonPropertyName("font")] public string FontFamily { get; set; } = "";               // "" = 메모 설정의 기본 글꼴
    [JsonPropertyName("text_color")] public string TextColor { get; set; } = "";          // 글상자 글자 색 ("" = 기본)
    [JsonPropertyName("image")] public string Image { get; set; } = "";              // images/ 안의 파일 이름
    [JsonPropertyName("title")] public string Title { get; set; } = "";              // 항목 이름 (비어 있으면 표시하지 않음)
    // 이미지 자르기: 원본에 대한 비율(0~1). 원본 파일은 그대로 두고 보여 줄 때만 잘라서, 언제든 되돌릴 수 있습니다.
    [JsonPropertyName("crop_l")] public double CropL { get; set; }
    [JsonPropertyName("crop_t")] public double CropT { get; set; }
    [JsonPropertyName("crop_r")] public double CropR { get; set; } = 1;
    [JsonPropertyName("crop_b")] public double CropB { get; set; } = 1;
    [JsonIgnore] public bool HasCrop => CropL > 0.0001 || CropT > 0.0001 || CropR < 0.9999 || CropB < 0.9999;
}

public sealed class NotePage
{
    [JsonPropertyName("id")] public string Id { get; set; } = Guid.NewGuid().ToString("N");
    [JsonPropertyName("title")] public string Title { get; set; } = "노트";
    [JsonPropertyName("items")] public List<NoteItem> Items { get; set; } = new();
    // 마지막으로 보던 위치/배율
    [JsonPropertyName("view_x")] public double ViewX { get; set; }
    [JsonPropertyName("view_y")] public double ViewY { get; set; }
    [JsonPropertyName("zoom")] public double Zoom { get; set; } = 1.0;
}

public sealed class NotesDocument
{
    [JsonPropertyName("version")] public int Version { get; set; } = 1;
    [JsonPropertyName("game_title")] public string GameTitle { get; set; } = "";
    [JsonPropertyName("game_dir")] public string GameDir { get; set; } = "";
    [JsonPropertyName("active_page")] public int ActivePage { get; set; }
    [JsonPropertyName("pages")] public List<NotePage> Pages { get; set; } = new();
}

/// <summary>
/// 게임별 노트 저장소: RocketRPG\config\notes\&lt;게임키&gt;\notes.json + images\.
/// 변경은 모아서 0.8초 뒤 한 번에 저장합니다 (입력할 때마다 디스크에 쓰지 않음).
/// </summary>
public sealed class NotesStore
{
    public string GameDir { get; }
    public string Folder { get; }
    public string ImageFolder => Path.Combine(Folder, "images");
    public NotesDocument Doc { get; private set; } = new();

    readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(800) };
    static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public NotesStore(string gameDir, string gameTitle)
    {
        GameDir = gameDir;
        Folder = FolderFor(gameDir);
        Load(gameTitle);
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); SaveNow(); };
    }

    NotesStore(string folder, string title, bool room)
    {
        GameDir = "";
        Folder = folder;
        IsRoom = room;
        Doc = new NotesDocument { GameTitle = title };
        Doc.Pages.Add(new NotePage { Title = "노트" });
        _saveTimer.Tick += (_, _) => _saveTimer.Stop();   // 방 노트는 방장의 것이라 저장하지 않음 (이미지만 받아 둠)
    }

    /// <summary>
    /// 멀티 참가자가 보는 방장의 게임 노트 (사본). 받은 이미지는 config\notes\_room에 두고, 방을 나가면 지웁니다.
    /// </summary>
    public static NotesStore ForRoom(string title)
    {
        string folder = Path.Combine(SettingsService.Root(), "config", "notes", "_room");
        try { if (Directory.Exists(folder)) Directory.Delete(folder, true); } catch { }
        return new NotesStore(folder, title, true);
    }

    /// <summary>멀티 방 노트 (방장의 게임 노트 사본)</summary>
    public bool IsRoom { get; }

    /// <summary>바뀐 것이 있을 때 (멀티 노트 공유가 차이를 찾아 보냄)</summary>
    public event Action? Dirty;

    /// <summary>게임과 상관없는 '일반 메모' (게임이 꺼져 있을 때 보임)</summary>
    public bool IsGeneral => string.IsNullOrEmpty(GameDir) && !IsRoom;

    public static string FolderFor(string gameDir) =>
        Path.Combine(SettingsService.Root(), "config", "notes",
            string.IsNullOrEmpty(gameDir) ? "_general" : GameSettingsService.ComputeSafeKey(gameDir));

    void Load(string gameTitle)
    {
        try
        {
            string file = Path.Combine(Folder, "notes.json");
            if (File.Exists(file)) Doc = JsonSerializer.Deserialize<NotesDocument>(File.ReadAllText(file)) ?? new();
        }
        catch (Exception ex)
        {
            UiLog.Write($"notes: load failed ({ex.Message}), starting a new note");
            Doc = new();
        }
        Doc.GameTitle = gameTitle;
        Doc.GameDir = GameDir;
        if (Doc.Pages.Count == 0) Doc.Pages.Add(new NotePage { Title = "노트 1" });
        Doc.ActivePage = Math.Clamp(Doc.ActivePage, 0, Doc.Pages.Count - 1);
    }

    public NotePage ActivePage => Doc.Pages[Math.Clamp(Doc.ActivePage, 0, Doc.Pages.Count - 1)];

    /// <summary>변경 표시: 잠시 뒤 저장합니다.</summary>
    public void MarkDirty()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
        Dirty?.Invoke();
    }

    public void Flush()
    {
        if (!_saveTimer.IsEnabled) return;
        _saveTimer.Stop();
        SaveNow();
    }

    void SaveNow()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            string file = Path.Combine(Folder, "notes.json");
            string tmp = file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Doc, JsonOpts));
            File.Move(tmp, file, overwrite: true);   // 저장 도중 꺼져도 이전 노트가 깨지지 않게
        }
        catch (Exception ex)
        {
            UiLog.Write($"notes: save failed {ex.Message}");
        }
    }

    /// <summary>이미지 파일(png/jpg/gif/bmp)을 노트 폴더로 복사하고 새 파일 이름을 돌려줍니다.</summary>
    public string ImportImageFile(string sourcePath)
    {
        Directory.CreateDirectory(ImageFolder);
        string ext = Path.GetExtension(sourcePath).ToLowerInvariant();
        if (ext is not (".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp")) ext = ".png";
        string name = $"{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}{ext}";
        File.Copy(sourcePath, Path.Combine(ImageFolder, name));
        return name;
    }

    /// <summary>PNG 바이트(스크린샷, 붙여넣기)를 저장하고 새 파일 이름을 돌려줍니다.</summary>
    public string SavePng(byte[] png, string prefix = "shot")
    {
        Directory.CreateDirectory(ImageFolder);
        string name = $"{prefix}-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..4]}.png";
        File.WriteAllBytes(Path.Combine(ImageFolder, name), png);
        return name;
    }

    public string ImagePath(string name) => Path.Combine(ImageFolder, name);

    /// <summary>어느 페이지에서도 쓰지 않는 이미지 파일을 지웁니다 (항목을 지운 뒤 남은 파일 정리).</summary>
    public void CleanupUnusedImages()
    {
        try
        {
            if (!Directory.Exists(ImageFolder)) return;
            var used = new HashSet<string>(Doc.Pages.SelectMany(p => p.Items).Where(i => i.Kind == "image").Select(i => i.Image),
                                           StringComparer.OrdinalIgnoreCase);
            foreach (var f in Directory.GetFiles(ImageFolder))
                if (!used.Contains(Path.GetFileName(f))) File.Delete(f);
        }
        catch { }
    }
}
