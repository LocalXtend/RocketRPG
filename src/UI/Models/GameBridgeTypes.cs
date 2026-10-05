#nullable enable
using System.Collections.Generic;
using System.ComponentModel;
using System.Text.Json.Serialization;

namespace RocketRPG.Models;

// 모든 엔진 브리지(IGameBridge)가 함께 쓰는 게임 상태·ESP·데이터 관리자 항목 (예전에는 WebViewRenderer.cs 안에 있었음)

public class GameState
{
    [JsonPropertyName("scene")]
    public string Scene { get; set; } = "";

    [JsonPropertyName("mapId")]
    public int MapId { get; set; }

    [JsonPropertyName("playerX")]
    public int PlayerX { get; set; }

    [JsonPropertyName("playerY")]
    public int PlayerY { get; set; }

    [JsonPropertyName("displayX")]
    public double DisplayX { get; set; }

    [JsonPropertyName("displayY")]
    public double DisplayY { get; set; }

    [JsonPropertyName("noclip")]
    public bool Noclip { get; set; }
}

public class EspItem
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("trigger")]
    public int Trigger { get; set; }

    [JsonPropertyName("x")]
    public double X { get; set; }

    [JsonPropertyName("y")]
    public double Y { get; set; }

    [JsonPropertyName("w")]
    public double W { get; set; }

    [JsonPropertyName("h")]
    public double H { get; set; }

    /// <summary>
    /// 네이티브(mkxp-z/EasyRPG) ESP: 이벤트 위치를 화면 좌표 대신 맵 타일 좌표(TileX/TileY)로 받고,
    /// 화면 위치는 View의 카메라로 매 프레임 계산합니다. null이면 X/Y가 게임 화면 좌표입니다.
    /// </summary>
    [JsonIgnore] public EspView? View { get; set; }
    [JsonIgnore] public double TileX { get; set; }
    [JsonIgnore] public double TileY { get; set; }
}

/// <summary>맵 좌표 ESP의 카메라/맵 정보. 카메라(CamX/CamY)는 스크롤할 때마다 갱신됩니다.</summary>
public sealed class EspView
{
    public double Tile = 32;          // 타일 한 칸의 게임 화면 픽셀
    public double CamX, CamY;         // 화면 왼쪽 위의 맵 타일 좌표
    public double OffX, OffY;         // 맵이 화면보다 작을 때 가운데 정렬 오프셋(픽셀)
    public int ScreenW, ScreenH;      // 게임 화면 크기(픽셀)
    public int MapW, MapH;            // 맵 크기(타일)
    public bool LoopX, LoopY;
}

public class TileInfo
{
    [JsonPropertyName("mapX")]
    public int MapX { get; set; }

    [JsonPropertyName("mapY")]
    public int MapY { get; set; }

    [JsonPropertyName("passable")]
    public bool Passable { get; set; }

    [JsonPropertyName("tileIds")]
    public List<int>? TileIds { get; set; }

    [JsonPropertyName("events")]
    public string Events { get; set; } = "";

    [JsonPropertyName("screenX")]
    public double ScreenX { get; set; }

    [JsonPropertyName("screenY")]
    public double ScreenY { get; set; }
}

public class SwitchItem : INotifyPropertyChanged
{
    private int _id;
    private string _name = "";
    private bool _value;
    private bool _isFrozen;

    [JsonPropertyName("id")]
    public int Id
    {
        get => _id;
        set { if (_id != value) { _id = value; OnPropertyChanged(nameof(Id)); } }
    }

    [JsonPropertyName("name")]
    public string Name
    {
        get => _name;
        set { if (_name != value) { _name = value; OnPropertyChanged(nameof(Name)); } }
    }

    [JsonPropertyName("val")]
    public bool Value
    {
        get => _value;
        set { if (_value != value) { _value = value; OnPropertyChanged(nameof(Value)); } }
    }

    [JsonPropertyName("frozen")]
    public bool IsFrozen
    {
        get => _isFrozen;
        set { if (_isFrozen != value) { _isFrozen = value; OnPropertyChanged(nameof(IsFrozen)); } }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged(string propName) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));
}

public class VariableItem : INotifyPropertyChanged
{
    private int _id;
    private string _name = "";
    private string _value = "";
    private bool _isFrozen;

    [JsonPropertyName("id")]
    public int Id
    {
        get => _id;
        set { if (_id != value) { _id = value; OnPropertyChanged(nameof(Id)); } }
    }

    [JsonPropertyName("name")]
    public string Name
    {
        get => _name;
        set { if (_name != value) { _name = value; OnPropertyChanged(nameof(Name)); } }
    }

    [JsonPropertyName("val")]
    public string Value
    {
        get => _value;
        set { if (_value != value) { _value = value; OnPropertyChanged(nameof(Value)); } }
    }

    [JsonPropertyName("frozen")]
    public bool IsFrozen
    {
        get => _isFrozen;
        set { if (_isFrozen != value) { _isFrozen = value; OnPropertyChanged(nameof(IsFrozen)); } }
    }

    private bool _isPinned;

    [JsonPropertyName("pinned")]
    public bool IsPinned
    {
        get => _isPinned;
        set { if (_isPinned != value) { _isPinned = value; OnPropertyChanged(nameof(IsPinned)); } }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged(string propName) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));
}

public class KeyMessage
{
    [JsonPropertyName("key")]
    public string Key { get; set; } = "";

    [JsonPropertyName("code")]
    public string Code { get; set; } = "";

    [JsonPropertyName("ctrl")]
    public bool Ctrl { get; set; }

    [JsonPropertyName("alt")]
    public bool Alt { get; set; }

    [JsonPropertyName("shift")]
    public bool Shift { get; set; }
}
