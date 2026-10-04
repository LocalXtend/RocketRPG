#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using RocketRPG.Models;

namespace RocketRPG.Views;

public class MapListItem : INotifyPropertyChanged
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int ParentId { get; set; }
    public int Order { get; set; }

    private bool _isCurrentPlayerMap;
    public bool IsCurrentPlayerMap
    {
        get => _isCurrentPlayerMap;
        set { _isCurrentPlayerMap = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsCurrentPlayerMap))); }
    }

    public string DisplayHeader => $"[{Id:D3}] {Name}";

    public event PropertyChangedEventHandler? PropertyChanged;
}

public partial class MapViewerWindow : Window
{
    private readonly string _gameDir;
    private readonly IGameBridge? _renderer;
    private readonly Action<int, int, int>? _onWarp;

    private int _currentMapId;
    private int _playerX;
    private int _playerY;

    private readonly ObservableCollection<MapListItem> _mapList = new();
    private ICollectionView? _mapListView;

    private int _selectedMapId;
    private int _mapWidth;
    private int _mapHeight;
    private double _zoomScale = 1.0;
    private const int BaseTileSize = 24;
    private int _currentTileSize = BaseTileSize;

    // 실제 타일 렌더링: 게임 리소스 접근(아카이브는 창이 열려 있는 동안 한 번만 엶)과 타일셋 캐시
    private readonly GameAssetSource _assets;
    private readonly Dictionary<int, MapTileRenderer.Tileset?> _tilesetCache = new();
    private int _renderGeneration;

    public MapViewerWindow(string gameDir, int currentMapId, int playerX, int playerY, IGameBridge? renderer = null, Action<int, int, int>? onWarp = null, int engine = 0)
    {
        InitializeComponent();
        if (engine == 0)
        {
            var gi = CoreInterop.GameInfo.Create();
            if (CoreInterop.rpg_detect_game(gameDir, ref gi) == 0) engine = gi.Engine;
        }
        _assets = new GameAssetSource(gameDir, engine);
        MapScrollViewer.SizeChanged += OnViewerSizeChanged;
        _gameDir = gameDir;
        _currentMapId = currentMapId;
        _playerX = playerX;
        _playerY = playerY;
        _renderer = renderer;
        _onWarp = onWarp;

        if (_renderer != null)
        {
            _renderer.GameStateUpdated += OnGameStateUpdated;
        }

        _mapListView = CollectionViewSource.GetDefaultView(_mapList);
        _mapListView.Filter = FilterMap;
        MapListBox.ItemsSource = _mapListView;

        LoadMapInfos();

        // 현재 맵 선택
        var currentItem = _mapList.FirstOrDefault(m => m.Id == _currentMapId) ?? _mapList.FirstOrDefault();
        if (currentItem != null)
        {
            MapListBox.SelectedItem = currentItem;
        }
    }

    public void UpdatePlayerPosition(int mapId, int x, int y)
    {
        _currentMapId = mapId;
        _playerX = x;
        _playerY = y;

        foreach (var m in _mapList)
        {
            m.IsCurrentPlayerMap = (m.Id == _currentMapId);
        }

        // 텔레메트리마다 전체 오버레이를 다시 만들지 않고 플레이어 표시만 옮깁니다.
        UpdatePlayerMarker();
    }

    private void OnGameStateUpdated(GameState state)
    {
        if (!Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
        {
            try
            {
                Dispatcher.Invoke(() =>
                {
                    UpdatePlayerPosition(state.MapId, state.PlayerX, state.PlayerY);
                });
            }
            catch { }
        }
    }

    private string FindDataFolder()
    {
        string p1 = System.IO.Path.Combine(_gameDir, "data");
        if (Directory.Exists(p1)) return p1;
        string p2 = System.IO.Path.Combine(_gameDir, "www", "data");
        if (Directory.Exists(p2)) return p2;
        string p3 = System.IO.Path.Combine(_gameDir, "Copy of Data");
        if (Directory.Exists(p3)) return p3;
        return p1;
    }

    private byte[]? ReadDataFile(string relativePath)
    {
        string p1 = System.IO.Path.Combine(_gameDir, relativePath);
        if (File.Exists(p1))
        {
            try { return File.ReadAllBytes(p1); } catch { }
        }

        string p2 = System.IO.Path.Combine(FindDataFolder(), System.IO.Path.GetFileName(relativePath));
        if (File.Exists(p2))
        {
            try { return File.ReadAllBytes(p2); } catch { }
        }

        // RGSS 아카이브 (열어 둔 것을 재사용: 매번 수십 MB 아카이브를 다시 색인하지 않음)
        return _assets.ReadData(relativePath) ?? _assets.ReadData($"Data\\{System.IO.Path.GetFileName(relativePath)}");
    }

    private void LoadMapInfos()
    {
        _mapList.Clear();
        string dataFolder = FindDataFolder();
        string mapInfosPath = System.IO.Path.Combine(dataFolder, "MapInfos.json");

        if (File.Exists(mapInfosPath))
        {
            try
            {
                string json = File.ReadAllText(mapInfosPath);
                using var doc = JsonDocument.Parse(json);
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    if (el.ValueKind != JsonValueKind.Object) continue;
                    if (!el.TryGetProperty("id", out var idProp)) continue;
                    int id = idProp.GetInt32();
                    string name = el.TryGetProperty("name", out var np) ? np.GetString() ?? "" : "";
                    int parentId = el.TryGetProperty("parentId", out var pp) ? pp.GetInt32() : 0;
                    int order = el.TryGetProperty("order", out var op) ? op.GetInt32() : 0;

                    _mapList.Add(new MapListItem
                    {
                        Id = id,
                        Name = name,
                        ParentId = parentId,
                        Order = order,
                        IsCurrentPlayerMap = (id == _currentMapId)
                    });
                }
                return;
            }
            catch (Exception ex)
            {
                UiLog.Write($"MapViewer: LoadMapInfos JSON error {ex.Message}");
            }
        }

        // Ruby 계열 맵 정보 로드 (rvdata2, rvdata, rxdata)
        byte[]? rubyInfos = ReadDataFile("Data\\MapInfos.rvdata2") ??
                            ReadDataFile("Data\\MapInfos.rvdata") ??
                            ReadDataFile("Data\\MapInfos.rxdata");

        if (rubyInfos != null)
        {
            var list = RubyMarshalReader.ParseMapInfos(rubyInfos);
            foreach (var item in list)
            {
                item.IsCurrentPlayerMap = (item.Id == _currentMapId);
                _mapList.Add(item);
            }
            if (_mapList.Count > 0) return;
        }

        // 2000/2003 LCF 맵 트리 로드 (RPG_RT.lmt)
        byte[]? lmtData = ReadDataFile("RPG_RT.lmt") ?? ReadDataFile("Data\\RPG_RT.lmt");
        if (lmtData != null)
        {
            var list = LcfReader.ParseMapTree(lmtData);
            foreach (var item in list)
            {
                item.IsCurrentPlayerMap = (item.Id == _currentMapId);
                _mapList.Add(item);
            }
        }
    }

    private bool FilterMap(object obj)
    {
        if (obj is not MapListItem item) return false;
        string q = SearchMapBox.Text.Trim();
        if (string.IsNullOrEmpty(q)) return true;

        if (item.Id.ToString() == q) return true;
        return item.Name.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    private void SearchMapBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _mapListView?.Refresh();
    }

    private void MapListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        try
        {
            if (MapListBox.SelectedItem is MapListItem item)
            {
                if (_pendingTarget == null)
                {
                    _selectedTile = null;
                    TransferPanel.Children.Clear();
                    TxtTileInfo.Text = "맵의 타일을 클릭하면 정보가 나옵니다. 휠: 확대/축소 · 오른쪽 버튼 끌기: 이동 · 더블 클릭: 워프";
                }
                _selectedMapId = item.Id;
                LoadAndRenderMap(item.Id, item.Name);
            }
        }
        catch (Exception ex)
        {
            UiLog.Write($"MapListBox_SelectionChanged error: {ex.Message}");
        }
    }

    private List<MapEventData> _currentEvents = new();

    private void LoadAndRenderMap(int mapId, string mapName)
    {
        try
        {
            string dataFolder = FindDataFolder();
            string mapFile = System.IO.Path.Combine(dataFolder, $"Map{mapId:D3}.json");

            if (File.Exists(mapFile))
            {
                try
                {
                    string json = File.ReadAllText(mapFile);
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;

                    _mapWidth = root.TryGetProperty("width", out var wp) ? wp.GetInt32() : 0;
                    _mapHeight = root.TryGetProperty("height", out var hp) ? hp.GetInt32() : 0;
                    int tilesetId = root.TryGetProperty("tilesetId", out var tp) ? tp.GetInt32() : 0;

                    // 이벤트 추출
                    _currentEvents.Clear();
                    if (root.TryGetProperty("events", out var evProp) && evProp.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var evEl in evProp.EnumerateArray())
                        {
                            if (evEl.ValueKind != JsonValueKind.Object) continue;
                            var ev = JsonSerializer.Deserialize<MapEventData>(evEl.GetRawText());
                            if (ev != null) _currentEvents.Add(ev);
                        }
                    }

                    // 타일 데이터 추출
                    int[]? tileData = null;
                    if (root.TryGetProperty("data", out var dataProp) && dataProp.ValueKind == JsonValueKind.Array)
                    {
                        tileData = JsonSerializer.Deserialize<int[]>(dataProp.GetRawText());
                    }

                    MapHeaderInfo.Text = $"[{mapId:D3}] {mapName} (크기: {_mapWidth}x{_mapHeight}) | 타일셋: {tilesetId} | 이벤트: {_currentEvents.Count}개";

                    // 타일 렌더링
                    RenderMap(tileData, tilesetId, 0);

                    // 기본 대상 좌표
                    TxtWarpX.Text = (_selectedMapId == _currentMapId) ? _playerX.ToString() : (_mapWidth / 2).ToString();
                    TxtWarpY.Text = (_selectedMapId == _currentMapId) ? _playerY.ToString() : (_mapHeight / 2).ToString();
                    return;
                }
                catch (Exception ex)
                {
                    UiLog.Write($"MapViewer: LoadMap json error {ex.Message}");
                }
            }

            // Ruby 계열 맵 로드 (rvdata2, rvdata, rxdata)
            try
            {
                byte[]? rubyMap = ReadDataFile($"Data\\Map{mapId:D3}.rvdata2") ??
                                  ReadDataFile($"Data\\Map{mapId:D3}.rvdata") ??
                                  ReadDataFile($"Data\\Map{mapId:D3}.rxdata");

                if (rubyMap != null)
                {
                    var (w, h, tilesetId, tileData, evs) = RubyMarshalReader.ParseMap(rubyMap);
                    _mapWidth = w;
                    _mapHeight = h;
                    _currentEvents = evs ?? new();

                    MapHeaderInfo.Text = $"[{mapId:D3}] {mapName} (크기: {_mapWidth}x{_mapHeight}) | 타일셋: {tilesetId} | 이벤트: {_currentEvents.Count}개";

                    RenderMap(tileData, tilesetId, 0);

                    TxtWarpX.Text = (_selectedMapId == _currentMapId) ? _playerX.ToString() : (_mapWidth / 2).ToString();
                    TxtWarpY.Text = (_selectedMapId == _currentMapId) ? _playerY.ToString() : (_mapHeight / 2).ToString();
                    return;
                }
            }
            catch (Exception ex)
            {
                UiLog.Write($"MapViewer: LoadMap ruby error {ex.Message}");
            }

            // 2000/2003 LCF 맵 로드 (MapXXXX.lmu / MapXXX.lmu)
            try
            {
                byte[]? lcfMap = ReadDataFile($"Map{mapId:D4}.lmu") ??
                                 ReadDataFile($"Map{mapId:D3}.lmu") ??
                                 ReadDataFile($"Data\\Map{mapId:D4}.lmu") ??
                                 ReadDataFile($"Data\\Map{mapId:D3}.lmu");

                if (lcfMap != null)
                {
                    var mapData = LcfReader.ParseMapUnit(lcfMap);
                    _mapWidth = mapData.Width;
                    _mapHeight = mapData.Height;
                    _currentEvents = mapData.Events ?? new();

                    MapHeaderInfo.Text = $"[{mapId:D3}] {mapName} (크기: {_mapWidth}x{_mapHeight}) | 칩셋: {mapData.ChipsetId} | 이벤트: {_currentEvents.Count}개";

                    RenderMap(mapData.TileData, 0, mapData.ChipsetId);

                    TxtWarpX.Text = (_selectedMapId == _currentMapId) ? _playerX.ToString() : (_mapWidth / 2).ToString();
                    TxtWarpY.Text = (_selectedMapId == _currentMapId) ? _playerY.ToString() : (_mapHeight / 2).ToString();
                    return;
                }
            }
            catch (Exception ex)
            {
                UiLog.Write($"MapViewer: LoadMap lcf error {ex.Message}");
            }

            MapHeaderInfo.Text = $"[{mapId:D3}] {mapName} - 맵 파일이 존재하지 않습니다.";
            MapTileImage.Source = null;
            MapOverlayCanvas.Children.Clear();
        }
        catch (Exception ex)
        {
            UiLog.Write($"MapViewer: LoadAndRenderMap error {ex.Message}");
        }
    }

    /// <summary>
    /// 게임의 실제 타일셋으로 맵을 그립니다(백그라운드). 타일셋 이미지를 찾지 못하면 색상 맵으로 대체합니다.
    /// </summary>
    private void RenderMap(int[]? tileData, int tilesetId, int chipsetId)
    {
        int gen = ++_renderGeneration;
        int w = _mapWidth, h = _mapHeight;
        MapTileImage.Source = null;
        MapOverlayCanvas.Children.Clear();
        _playerMarker = null;
        if (tileData == null || w <= 0 || h <= 0)
        {
            RenderTileMap(tileData);
            return;
        }

        int tsKey = tilesetId * 100000 + chipsetId;
        MapLoading.Visibility = Visibility.Visible;
        System.Threading.Tasks.Task.Run<(BitmapSource bmp, int TileSize)?>(() =>
        {
            MapTileRenderer.Tileset? ts;
            lock (_tilesetCache)
            {
                if (!_tilesetCache.TryGetValue(tsKey, out ts))
                {
                    ts = MapTileRenderer.LoadTileset(_assets, tilesetId, chipsetId);
                    _tilesetCache[tsKey] = ts;
                }
            }
            var result = ts != null ? MapTileRenderer.Render(ts, w, h, tileData) : null;
            return result == null ? null : (bmp: MapTileRenderer.ToBitmap(result), result.TileSize);
        }).ContinueWith(t =>
        {
            if (gen != _renderGeneration) return; // 그 사이 다른 맵을 선택함
            MapLoading.Visibility = Visibility.Collapsed;
            if (t.IsCompletedSuccessfully && t.Result is { } r)
            {
                _currentTileSize = r.TileSize;
                MapTileImage.Source = r.bmp;
                ApplyZoom();
            }
            else
            {
                if (t.Exception != null) UiLog.Write($"MapViewer: tile render failed {t.Exception.GetBaseException().Message}");
                RenderTileMap(tileData); // 타일셋 이미지를 못 찾은 경우 색상 맵
            }
        }, System.Threading.Tasks.TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void RenderTileMap(int[]? tileData)
    {
        if (_mapWidth <= 0 || _mapHeight <= 0 || MapTileImage == null) return;

        try
        {
            _currentTileSize = BaseTileSize;
            if (_mapWidth > 150 || _mapHeight > 150)
            {
                _currentTileSize = Math.Max(4, Math.Min(BaseTileSize, 2048 / Math.Max(_mapWidth, _mapHeight)));
            }

            int pixelW = _mapWidth * _currentTileSize;
            int pixelH = _mapHeight * _currentTileSize;
            if (pixelW <= 0 || pixelH <= 0) return;

            var bmp = new WriteableBitmap(pixelW, pixelH, 96, 96, PixelFormats.Bgra32, null);
            int stride = pixelW * 4;
            byte[] pixels = new byte[stride * pixelH];

            // 타일 색상 생성
            for (int y = 0; y < _mapHeight; y++)
            {
                for (int x = 0; x < _mapWidth; x++)
                {
                    int tileId = 0;
                    if (tileData != null)
                    {
                        // RPG Maker 타일 데이터 레이어 스캔 (MV/MZ는 6개 레이어, XP/VX/Ace는 3~4개 레이어, 2000/2003은 2개 레이어)
                        // 상층부터 하층(0)으로 스캔하여 최상위 유효 타일 ID를 취득 (10000은 2000/2003 상층 투명 타일이므로 스킵)
                        int totalLayers = Math.Max(1, tileData.Length / (_mapWidth * _mapHeight));
                        for (int z = Math.Min(totalLayers - 1, 3); z >= 0; z--)
                        {
                            int tIndex = (z * _mapHeight + y) * _mapWidth + x;
                            if (tIndex < tileData.Length && tileData[tIndex] > 0 && tileData[tIndex] != 10000)
                            {
                                tileId = tileData[tIndex];
                                break;
                            }
                        }
                    }

                    // 타일 색상 산출
                    (byte r, byte g, byte b) = GetTileColor(tileId, x, y);

                    // 타일 내 픽셀 채우기 + 테두리 그리드
                    for (int py = 0; py < _currentTileSize; py++)
                    {
                        int gy = y * _currentTileSize + py;
                        for (int px = 0; px < _currentTileSize; px++)
                        {
                            int gx = x * _currentTileSize + px;
                            int idx = gy * stride + gx * 4;

                            bool isBorder = (_currentTileSize >= 8) && (px == 0 || py == 0);
                            if (isBorder)
                            {
                                pixels[idx + 0] = (byte)(b * 0.7);
                                pixels[idx + 1] = (byte)(g * 0.7);
                                pixels[idx + 2] = (byte)(r * 0.7);
                                pixels[idx + 3] = 255;
                            }
                            else
                            {
                                pixels[idx + 0] = b;
                                pixels[idx + 1] = g;
                                pixels[idx + 2] = r;
                                pixels[idx + 3] = 255;
                            }
                        }
                    }
                }
            }

            bmp.WritePixels(new Int32Rect(0, 0, pixelW, pixelH), pixels, stride, 0);
            MapTileImage.Source = bmp;
            ApplyZoom();
        }
        catch (Exception ex)
        {
            UiLog.Write($"MapViewer: RenderTileMap error {ex.Message}");
        }
    }

    private static (byte r, byte g, byte b) GetTileColor(int tileId, int x, int y)
    {
        // 바둑판 체크 기본 패턴
        bool checker = ((x + y) % 2 == 0);
        byte baseVal = (byte)(checker ? 48 : 42);

        if (tileId == 0 || tileId == 10000)
        {
            return (baseVal, baseVal, baseVal);
        }

        // 2000/2003 Upper Layer tiles (10000+)
        if (tileId > 10000)
        {
            int subId = tileId - 10000;
            byte hashR = (byte)((subId * 37) % 150 + 60);
            byte hashG = (byte)((subId * 59) % 150 + 60);
            byte hashB = (byte)((subId * 83) % 150 + 60);
            return (hashR, hashG, hashB);
        }

        // RPG Maker 타일 ID 대역별 색상 매핑
        if (tileId < 4000)
        {
            // A1: 물/바다/용암/폭포 (2000/2003 animated autotiles & XP/VX/Ace/MV/MZ A1)
            return (40, 90, (byte)(160 + (tileId % 30)));
        }
        else if (tileId >= 4000 && tileId <= 4999)
        {
            // A2 & RM2000/2003 ground autotiles: 바닥 / 풀밭 / 카펫 / 눈
            return ((byte)(70 + (tileId % 40)), (byte)(110 + (tileId % 50)), (byte)(60 + (tileId % 30)));
        }
        else if (tileId >= 5000 && tileId <= 5887)
        {
            // A3 & RM2000/2003 lower chipset tiles: 건물 외벽 / 절벽
            return ((byte)(130 + (tileId % 40)), (byte)(110 + (tileId % 40)), (byte)(100 + (tileId % 30)));
        }
        else if (tileId >= 5888 && tileId <= 8191)
        {
            // A4 & A5: 던전/성벽 및 돌바닥
            return ((byte)(115 + (tileId % 35)), (byte)(115 + (tileId % 35)), (byte)(125 + (tileId % 30)));
        }
        else
        {
            // B~E: 장식, 가구, 소품, 상층 타일
            byte hashR = (byte)((tileId * 37) % 150 + 60);
            byte hashG = (byte)((tileId * 59) % 150 + 60);
            byte hashB = (byte)((tileId * 83) % 150 + 60);
            return (hashR, hashG, hashB);
        }
    }

    static readonly Brush[] EventStroke = MakeBrushes(255);
    static readonly Brush[] EventFill = MakeBrushes(70);
    static readonly Brush TransferStroke = FrozenBrush(Color.FromRgb(255, 60, 160));
    static readonly Brush TransferFill = FrozenBrush(Color.FromArgb(90, 255, 60, 160));
    static readonly Brush SelectStroke = FrozenBrush(Color.FromRgb(255, 230, 0));
    static readonly Brush TargetStroke = FrozenBrush(Color.FromRgb(0, 255, 120));

    static Brush FrozenBrush(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    static Brush[] MakeBrushes(byte alpha)
    {
        Color[] c = [Color.FromRgb(0, 153, 255), Color.FromRgb(0, 204, 68), Color.FromRgb(255, 136, 0), Color.FromRgb(187, 51, 255), Color.FromRgb(0, 255, 255)];
        return c.Select(x => { var b = new SolidColorBrush(Color.FromArgb(alpha, x.R, x.G, x.B)); b.Freeze(); return (Brush)b; }).ToArray();
    }

    static int TriggerClass(int trigger) => trigger switch { 0 => 0, 1 or 2 => 1, 3 => 2, 4 => 3, _ => 4 };

    private FrameworkElement? _playerMarker;
    private FrameworkElement? _selectMarker;
    private FrameworkElement? _targetMarker;
    private (int x, int y)? _selectedTile;
    private (int mapId, int x, int y)? _pendingTarget;   // 장소 이동 목적지로 맵을 열었을 때 표시할 타일

    // 이벤트 표시는 맵이 바뀔 때만 한 번 만듭니다. 확대/축소는 LayoutTransform이라 다시 만들 필요가 없습니다.
    private void DrawOverlays()
    {
        if (MapOverlayCanvas == null) return;
        MapOverlayCanvas.Children.Clear();
        _playerMarker = _selectMarker = _targetMarker = null;
        if (_mapWidth <= 0 || _mapHeight <= 0) return;
        double ts = _currentTileSize;

        foreach (var ev in _currentEvents)
        {
            if (ev == null || ev.X < 0 || ev.X >= _mapWidth || ev.Y < 0 || ev.Y >= _mapHeight) continue;

            int trigger = (ev.Pages != null && ev.Pages.Count > 0 && ev.Pages[0] != null) ? ev.Pages[0].Trigger : 0;
            int cls = TriggerClass(trigger);
            bool transfer = ev.AllTransfers().Count > 0;

            var border = new Border
            {
                Width = Math.Max(2, ts - 2),
                Height = Math.Max(2, ts - 2),
                BorderBrush = transfer ? TransferStroke : EventStroke[cls],
                BorderThickness = new Thickness(Math.Max(1, Math.Min(2, ts / 8))),
                Background = transfer ? TransferFill : EventFill[cls],
                CornerRadius = new CornerRadius(Math.Max(1, Math.Min(3, ts / 8)))
            };
            border.Child = new TextBlock
            {
                Text = transfer ? "⇲" : ev.Id.ToString(),
                FontSize = Math.Max(6, ts * 0.4),
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            Canvas.SetLeft(border, ev.X * ts + 1);
            Canvas.SetTop(border, ev.Y * ts + 1);
            MapOverlayCanvas.Children.Add(border);
        }

        if (_pendingTarget is { } pt && pt.mapId == _selectedMapId)
        {
            _targetMarker = TileFrame(pt.x, pt.y, TargetStroke);
            SelectTile(pt.x, pt.y, scrollIntoView: true);
            _pendingTarget = null;
        }
        else if (_selectedTile is { } st)
        {
            _selectMarker = TileFrame(st.x, st.y, SelectStroke);
        }

        UpdatePlayerMarker();

        // 처음 열었을 때 현재 맵이면 주인공이 보이게 스크롤
        if (_scrollToPlayer && _selectedMapId == _currentMapId)
        {
            _scrollToPlayer = false;
            ScrollToMapPoint((_playerX + 0.5) * _currentTileSize, (_playerY + 0.5) * _currentTileSize);
        }
    }

    private bool _scrollToPlayer = true;

    private FrameworkElement TileFrame(int x, int y, Brush stroke)
    {
        double ts = _currentTileSize;
        var r = new Rectangle
        {
            Width = ts,
            Height = ts,
            Stroke = stroke,
            StrokeThickness = Math.Max(1.5, ts / 8),
            IsHitTestVisible = false
        };
        Canvas.SetLeft(r, x * ts);
        Canvas.SetTop(r, y * ts);
        MapOverlayCanvas.Children.Add(r);
        return r;
    }

    // 플레이어 위치만 바뀔 때는 기존 표시를 옮기기만 합니다.
    private void UpdatePlayerMarker()
    {
        if (MapOverlayCanvas == null || _mapWidth <= 0) return;
        bool show = _selectedMapId == _currentMapId;
        if (!show)
        {
            if (_playerMarker != null) _playerMarker.Visibility = Visibility.Collapsed;
            return;
        }
        double ts = _currentTileSize;
        if (_playerMarker == null)
        {
            var playerMarker = new Grid
            {
                Width = ts,
                Height = ts,
                IsHitTestVisible = false
            };

            var ellipse = new Ellipse
            {
                Fill = new SolidColorBrush(Color.FromArgb(180, 255, 50, 0)),
                Stroke = Brushes.Yellow,
                StrokeThickness = Math.Max(1, Math.Min(2, ts / 8))
            };
            var star = new TextBlock
            {
                Text = "★",
                FontSize = Math.Max(6, ts * 0.6),
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            playerMarker.Children.Add(ellipse);
            playerMarker.Children.Add(star);
            MapOverlayCanvas.Children.Add(playerMarker);
            _playerMarker = playerMarker;
        }
        _playerMarker.Visibility = Visibility.Visible;
        Canvas.SetLeft(_playerMarker, _playerX * ts);
        Canvas.SetTop(_playerMarker, _playerY * ts);
    }

    private static string GetTriggerName(int trigger) => trigger switch
    {
        0 => "결정키",
        1 => "플레이어 접촉",
        2 => "이벤트 접촉",
        3 => "자동실행",
        4 => "병렬처리",
        _ => "기타"
    };

    // ── 확대/축소와 이동 ──

    private const double MinZoom = 0.25, MaxZoom = 8.0;

    /// <summary>맵이 바뀌었을 때: 원래 크기로 표면을 맞추고 이벤트 표시를 새로 만듭니다.</summary>
    private void ApplyZoom()
    {
        if (MapRenderContainer == null || MapTileImage == null || MapOverlayCanvas == null) return;

        double ts = _currentTileSize;
        double totalW = _mapWidth * ts;
        double totalH = _mapHeight * ts;

        MapRenderContainer.Width = totalW;
        MapRenderContainer.Height = totalH;
        MapTileImage.Width = totalW;
        MapTileImage.Height = totalH;
        MapOverlayCanvas.Width = totalW;
        MapOverlayCanvas.Height = totalH;

        UpdatePadding();
        SetZoom(_zoomScale, null);
        ScrollToMapPoint(totalW / 2, totalH / 2);   // 새 맵은 가운데에 (주인공/도착 타일이 있으면 DrawOverlays가 그쪽으로)
        DrawOverlays();
    }

    // 맵 둘레에 보기 영역 절반만큼 빈 공간을 두어, 맵 가장자리도 화면 가운데까지 끌어올 수 있게 합니다 (답답하지 않게).
    private double PadX => MapRenderContainer.Margin.Left;
    private double PadY => MapRenderContainer.Margin.Top;

    // 창 크기가 바뀌면 여백을 다시 맞추되, 보고 있던 곳은 그대로 가운데에 둡니다.
    private void OnViewerSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_mapWidth <= 0 || e.PreviousSize.Width <= 0) { UpdatePadding(); return; }
        double cx = (MapScrollViewer.HorizontalOffset + e.PreviousSize.Width / 2 - PadX) / _zoomScale;
        double cy = (MapScrollViewer.VerticalOffset + e.PreviousSize.Height / 2 - PadY) / _zoomScale;
        UpdatePadding();
        ScrollToMapPoint(cx, cy);
    }

    private void UpdatePadding()
    {
        double vw = MapScrollViewer.ViewportWidth > 0 ? MapScrollViewer.ViewportWidth : MapScrollViewer.ActualWidth;
        double vh = MapScrollViewer.ViewportHeight > 0 ? MapScrollViewer.ViewportHeight : MapScrollViewer.ActualHeight;
        MapRenderContainer.Margin = new Thickness(vw / 2, vh / 2, vw / 2, vh / 2);
    }

    /// <summary>맵 좌표(확대 전 픽셀)의 한 점을 보기 영역 가운데로 스크롤합니다.</summary>
    private void ScrollToMapPoint(double mapX, double mapY)
    {
        MapScrollViewer.UpdateLayout();
        MapScrollViewer.ScrollToHorizontalOffset(PadX + mapX * _zoomScale - MapScrollViewer.ViewportWidth / 2);
        MapScrollViewer.ScrollToVerticalOffset(PadY + mapY * _zoomScale - MapScrollViewer.ViewportHeight / 2);
    }

    /// <summary>배율을 바꿉니다. anchor(보기 영역 안의 점)가 있으면 그 아래의 맵 위치가 그대로 남도록 스크롤합니다.</summary>
    private void SetZoom(double zoom, Point? anchor)
    {
        zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        Point a = anchor ?? new Point(MapScrollViewer.ViewportWidth / 2, MapScrollViewer.ViewportHeight / 2);
        double mapX = (MapScrollViewer.HorizontalOffset + a.X - PadX) / _zoomScale;
        double mapY = (MapScrollViewer.VerticalOffset + a.Y - PadY) / _zoomScale;

        _zoomScale = zoom;
        MapScale.ScaleX = MapScale.ScaleY = zoom;
        TxtZoom.Text = $"배율: {zoom * 100:0}%";
        MapScrollViewer.UpdateLayout();
        double h = PadX + mapX * zoom - a.X, v = PadY + mapY * zoom - a.Y;
        MapScrollViewer.ScrollToHorizontalOffset(h);
        MapScrollViewer.ScrollToVerticalOffset(v);
        // 끌어서 이동하는 도중에 확대하면 기준점을 새 위치로 (예전 기준으로 계산해 화면이 튀었음)
        if (_panStart != null && anchor is { } an)
        {
            _panStart = an;
            _panH = h;
            _panV = v;
        }
    }

    private void MapScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_mapWidth <= 0) return;
        double factor = Math.Pow(1.15, e.Delta / 120.0);
        SetZoom(_zoomScale * factor, e.GetPosition(MapScrollViewer));
        e.Handled = true;
    }

    private void BtnZoomIn_Click(object sender, RoutedEventArgs e) => SetZoom(_zoomScale * 1.25, null);
    private void BtnZoomOut_Click(object sender, RoutedEventArgs e) => SetZoom(_zoomScale / 1.25, null);
    private void BtnZoomReset_Click(object sender, RoutedEventArgs e) => SetZoom(1.0, null);

    private void BtnZoomFit_Click(object sender, RoutedEventArgs e)
    {
        if (_mapWidth <= 0 || _mapHeight <= 0) return;
        double w = _mapWidth * _currentTileSize, h = _mapHeight * _currentTileSize;
        SetZoom(0.95 * Math.Min(MapScrollViewer.ViewportWidth / w, MapScrollViewer.ViewportHeight / h), null);
        ScrollToMapPoint(w / 2, h / 2);
    }

    private Point? _panStart;
    private double _panH, _panV;

    private void MapScrollViewer_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        _panStart = e.GetPosition(MapScrollViewer);
        _panH = MapScrollViewer.HorizontalOffset;
        _panV = MapScrollViewer.VerticalOffset;
        MapScrollViewer.CaptureMouse();
        MapScrollViewer.Cursor = Cursors.SizeAll;
        MapScrollViewer.LostMouseCapture -= OnPanCaptureLost;
        MapScrollViewer.LostMouseCapture += OnPanCaptureLost;
        e.Handled = true;
    }

    private void OnPanCaptureLost(object sender, MouseEventArgs e)
    {
        _panStart = null;
        MapScrollViewer.Cursor = null;
    }

    private void MapScrollViewer_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_panStart is not { } s) return;
        var p = e.GetPosition(MapScrollViewer);
        MapScrollViewer.ScrollToHorizontalOffset(_panH - (p.X - s.X));
        MapScrollViewer.ScrollToVerticalOffset(_panV - (p.Y - s.Y));
    }

    private void MapScrollViewer_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_panStart == null) return;
        _panStart = null;
        MapScrollViewer.ReleaseMouseCapture();
        MapScrollViewer.Cursor = null;
        e.Handled = true;   // 오른쪽 버튼 메뉴가 뜨지 않게
    }

    // ── 타일 선택과 정보 ──

    private bool TileAt(MouseEventArgs e, out int tx, out int ty)
    {
        tx = ty = 0;
        if (_mapWidth <= 0 || _mapHeight <= 0) return false;
        Point p = e.GetPosition(MapRenderContainer);   // LayoutTransform 적용 전(원래 크기) 좌표
        double ts = _currentTileSize;
        tx = Math.Clamp((int)(p.X / ts), 0, _mapWidth - 1);
        ty = Math.Clamp((int)(p.Y / ts), 0, _mapHeight - 1);
        return true;
    }

    private void MapRenderContainer_MouseMove(object sender, MouseEventArgs e)
    {
        if (TileAt(e, out int tx, out int ty)) TxtHoverStatus.Text = $"좌표: ({tx}, {ty})";
    }

    private void MapRenderContainer_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!TileAt(e, out int tx, out int ty)) return;
        if (e.ClickCount == 2)
        {
            ExecuteWarp(tx, ty);
            return;
        }
        SelectTile(tx, ty, scrollIntoView: false);
    }

    private void SelectTile(int tx, int ty, bool scrollIntoView)
    {
        _selectedTile = (tx, ty);
        TxtWarpX.Text = tx.ToString();
        TxtWarpY.Text = ty.ToString();

        if (_selectMarker != null) MapOverlayCanvas.Children.Remove(_selectMarker);
        _selectMarker = TileFrame(tx, ty, SelectStroke);

        var evs = _currentEvents.Where(ev => ev != null && ev.X == tx && ev.Y == ty).ToList();
        var lines = new List<string> { $"타일 ({tx}, {ty})" + (_selectedMapId == _currentMapId && tx == _playerX && ty == _playerY ? " · 주인공 위치" : "") };
        TransferPanel.Children.Clear();
        if (evs.Count == 0) lines.Add("이벤트 없음");
        foreach (var ev in evs)
        {
            int trigger = ev.Pages is { Count: > 0 } && ev.Pages[0] != null ? ev.Pages[0].Trigger : 0;
            string name = string.IsNullOrWhiteSpace(ev.Name) ? $"EV{ev.Id:D3}" : ev.Name;
            lines.Add($"이벤트 {ev.Id}: {name} · {GetTriggerName(trigger)} · 페이지 {ev.Pages?.Count ?? 0}개");
            foreach (var t in ev.AllTransfers())
                AddTransferButtons(t);
        }
        TxtTileInfo.Text = string.Join("\n", lines);

        if (scrollIntoView)
        {
            ScrollToMapPoint((tx + 0.5) * _currentTileSize, (ty + 0.5) * _currentTileSize);
        }
    }

    private void AddTransferButtons(MapTransfer t)
    {
        string mapName = _mapList.FirstOrDefault(m => m.Id == t.MapId)?.Name ?? "";
        var label = new TextBlock
        {
            Text = $"장소 이동 → [{t.MapId:D3}] {mapName} ({t.X}, {t.Y})",
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = TransferStroke,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 6, 0)
        };
        var open = new Button { Content = "그 맵 보기", Padding = new Thickness(8, 1, 8, 1), Margin = new Thickness(0, 0, 4, 0) };
        open.Click += (_, _) => OpenMapAt(t.MapId, t.X, t.Y);
        var warp = new Button { Content = "바로 워프", Padding = new Thickness(8, 1, 8, 1), Margin = new Thickness(0, 0, 12, 0) };
        warp.Click += (_, _) => WarpTo(t.MapId, t.X, t.Y);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
        row.Children.Add(label);
        row.Children.Add(open);
        row.Children.Add(warp);
        TransferPanel.Children.Add(row);
    }

    /// <summary>장소 이동 목적지 맵을 열고 도착 타일을 표시합니다.</summary>
    private void OpenMapAt(int mapId, int x, int y)
    {
        var item = _mapList.FirstOrDefault(m => m.Id == mapId);
        if (item == null)
        {
            MessageBox.Show(this, $"맵 {mapId}을(를) 목록에서 찾을 수 없습니다.", "안내", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        _pendingTarget = (mapId, x, y);
        _selectedTile = null;
        SearchMapBox.Text = "";
        if (ReferenceEquals(MapListBox.SelectedItem, item)) LoadAndRenderMap(item.Id, item.Name);
        else MapListBox.SelectedItem = item;
        MapListBox.ScrollIntoView(item);
    }

    private void BtnWarp_Click(object sender, RoutedEventArgs e)
    {
        if (int.TryParse(TxtWarpX.Text.Trim(), out int tx) && int.TryParse(TxtWarpY.Text.Trim(), out int ty))
        {
            ExecuteWarp(tx, ty);
        }
        else
        {
            MessageBox.Show(this, "유효한 정수 좌표 (X, Y)를 입력해 주세요.", "입력 오류", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ExecuteWarp(int tx, int ty)
    {
        if (_selectedMapId <= 0)
        {
            MessageBox.Show(this, "워프할 맵을 먼저 선택해 주세요.", "안내", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        WarpTo(_selectedMapId, tx, ty);
    }

    private void WarpTo(int mapId, int tx, int ty)
    {
        _renderer?.Warp(mapId, tx, ty);
        _onWarp?.Invoke(mapId, tx, ty);
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_renderer != null)
        {
            _renderer.GameStateUpdated -= OnGameStateUpdated;
        }
        _renderGeneration++;
        _assets.Dispose();
        base.OnClosed(e);
    }
}
