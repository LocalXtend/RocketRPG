#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using RocketRPG.Models;

namespace RocketRPG.Views;

public class ExplorerEntry
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public ImageSource? Icon { get; set; }
    public string Description { get; set; } = "";
    public bool IsFolder { get; set; }
    public ScannedGame? Game { get; set; }
    /// <summary>폴더 안에서 맨 앞에 보이는 "상위 폴더로" 항목</summary>
    public bool IsBack { get; set; }
}

public partial class GameLibraryWindow : Window
{
    private readonly MainController _ctl;
    private readonly DataTemplate _iconTemplate;
    private readonly DataTemplate _listTemplate;
    private readonly ItemsPanelTemplate _wrapPanel;
    private readonly ItemsPanelTemplate _stackPanel;

    private bool _isIconView = true;
    private ExplorerEntry? _currentFolder = null; // null = Root (컴퓨터)
    private readonly List<ExplorerEntry> _rootFolders = new();
    private readonly Dictionary<string, List<ScannedGame>> _folderGamesCache = new(StringComparer.OrdinalIgnoreCase);
    private List<ExplorerEntry> _displayedEntries = new();
    private CancellationTokenSource? _scanCts;

    public event Action<string>? GameSelected;

    public GameLibraryWindow(MainController ctl)
    {
        InitializeComponent();
        _ctl = ctl;

        _iconTemplate = (DataTemplate)Resources["IconViewTemplate"];
        _listTemplate = (DataTemplate)Resources["ListViewTemplate"];
        _wrapPanel = (ItemsPanelTemplate)Resources["IconWrapPanel"];
        _stackPanel = (ItemsPanelTemplate)Resources["ListStackPanel"];

        ImgBack.Source = GameLibraryScanner.GetUpFolderIcon();

        SetViewMode(true);
        InitRootFolders();
        NavigateToRoot();
    }

    private void SetViewMode(bool iconView)
    {
        _isIconView = iconView;
        if (iconView)
        {
            ExplorerListView.ItemTemplate = _iconTemplate;
            ExplorerListView.ItemsPanel = _wrapPanel;
            BtnViewIcons.FontWeight = FontWeights.Bold;
            BtnViewList.FontWeight = FontWeights.Normal;
        }
        else
        {
            ExplorerListView.ItemTemplate = _listTemplate;
            ExplorerListView.ItemsPanel = _stackPanel;
            BtnViewIcons.FontWeight = FontWeights.Normal;
            BtnViewList.FontWeight = FontWeights.Bold;
        }
    }

    private void InitRootFolders()
    {
        _rootFolders.Clear();
        var folderIcon = GameLibraryScanner.GetFolderIcon();

        // 1. Steam folders
        var steamDirs = GameLibraryScanner.DetectSteamLibraryFolders();
        var steamIcon = GameLibraryScanner.GetSteamIcon() ?? folderIcon;
        foreach (var s in steamDirs)
        {
            if (Directory.Exists(s))
            {
                _rootFolders.Add(new ExplorerEntry
                {
                    Name = $"Steam 라이브러리 ({Path.GetFileName(Path.GetDirectoryName(s) ?? s)})",
                    Path = s,
                    Icon = steamIcon,
                    Description = s,
                    IsFolder = true
                });
            }
        }

        // 2. Documents
        try
        {
            string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (Directory.Exists(docs))
            {
                _rootFolders.Add(new ExplorerEntry
                {
                    Name = "내 문서",
                    Path = docs,
                    Icon = GameLibraryScanner.GetDocumentsIcon() ?? folderIcon,
                    Description = docs,
                    IsFolder = true
                });
            }
        }
        catch { }

        // 3. Downloads
        try
        {
            string downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            if (Directory.Exists(downloads))
            {
                _rootFolders.Add(new ExplorerEntry
                {
                    Name = "다운로드",
                    Path = downloads,
                    Icon = GameLibraryScanner.GetDownloadsIcon() ?? folderIcon,
                    Description = downloads,
                    IsFolder = true
                });
            }
        }
        catch { }

        // 4. GameSample (Dev sample folder)
        try
        {
            string sampleDir = Path.Combine(SettingsService.Root(), "GameSample");
            if (Directory.Exists(sampleDir))
            {
                _rootFolders.Add(new ExplorerEntry
                {
                    Name = "GameSample (샘플 모음)",
                    Path = sampleDir,
                    Icon = folderIcon,
                    Description = sampleDir,
                    IsFolder = true
                });
            }
        }
        catch { }

        // 5. Custom scan folders
        if (_ctl.Settings.CustomScanFolders != null)
        {
            foreach (var cf in _ctl.Settings.CustomScanFolders)
            {
                if (!string.IsNullOrWhiteSpace(cf) && Directory.Exists(cf))
                {
                    _rootFolders.Add(new ExplorerEntry
                    {
                        Name = Path.GetFileName(cf.TrimEnd('\\', '/')),
                        Path = cf,
                        Icon = folderIcon,
                        Description = cf,
                        IsFolder = true
                    });
                }
            }
        }
    }

    private void NavigateToRoot()
    {
        // 폴더 탐색 도중 빠르게 루트로 돌아오면, 늦게 끝난 탐색 결과가 루트 목록을 덮어써
        // "루트보다 위"처럼 빈 목록이 보였습니다. 진행 중인 탐색을 취소합니다.
        _scanCts?.Cancel();
        ScanProgress.Visibility = Visibility.Collapsed;
        _currentFolder = null;
        AddressBox.Text = "컴퓨터";
        BtnBack.IsEnabled = false;
        BtnRemoveFolder.IsEnabled = false;

        _displayedEntries = new List<ExplorerEntry>(_rootFolders);
        ApplyFilter();
        StatusText.Text = "루트 폴더 목록";
        CountText.Text = $"{_displayedEntries.Count}개 폴더";
    }

    private async Task NavigateToFolderAsync(ExplorerEntry folder)
    {
        _currentFolder = folder;
        AddressBox.Text = $"컴퓨터\\{folder.Name}";
        BtnBack.IsEnabled = true;

        bool isCustom = _ctl.Settings.CustomScanFolders.Any(f => f.Equals(folder.Path, StringComparison.OrdinalIgnoreCase));
        BtnRemoveFolder.IsEnabled = isCustom;

        // Check cache
        if (_folderGamesCache.TryGetValue(folder.Path, out var cachedGames))
        {
            _scanCts?.Cancel();
            ScanProgress.Visibility = Visibility.Collapsed;
            ShowGames(cachedGames);
            return;
        }

        // Scan folder
        _scanCts?.Cancel();
        _scanCts?.Dispose();
        _scanCts = new CancellationTokenSource();
        var token = _scanCts.Token;

        StatusText.Text = $"{folder.Name} 탐색 중...";
        ScanProgress.Visibility = Visibility.Visible;
        _displayedEntries = new List<ExplorerEntry>();
        ApplyFilter();   // 탐색 중에도 "상위 폴더로" 항목은 보입니다

        try
        {
            var games = await Task.Run(() =>
            {
                return GameLibraryScanner.ScanDirectories(new[] { folder.Path }, msg =>
                {
                    Dispatcher.BeginInvoke(() =>
                    {
                        if (!token.IsCancellationRequested)
                            StatusText.Text = msg;
                    });
                }, token);
            }, token);

            if (!token.IsCancellationRequested)
            {
                _folderGamesCache[folder.Path] = games;
                if (ReferenceEquals(_currentFolder, folder)) ShowGames(games);   // 그사이 다른 곳으로 갔으면 보여 주지 않음
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (ReferenceEquals(_currentFolder, folder)) StatusText.Text = $"탐색 오류: {ex.Message}";
        }
        finally
        {
            if (ReferenceEquals(_currentFolder, folder) || _currentFolder == null) ScanProgress.Visibility = Visibility.Collapsed;
        }
    }

    private void ShowGames(List<ScannedGame> games)
    {
        _displayedEntries = games.Select(g => new ExplorerEntry
        {
            Name = g.DisplayTitle,
            Path = g.DirectoryPath,
            Icon = g.IconSource,
            Description = $"{g.EngineName} • {g.DirectoryPath}",
            IsFolder = false,
            Game = g
        }).ToList();

        ApplyFilter();
        StatusText.Text = $"{_currentFolder?.Name ?? ""} 탐색 완료";
        CountText.Text = $"{_displayedEntries.Count}개 게임";
    }

    private void ApplyFilter()
    {
        string query = SearchBox.Text.Trim();
        IEnumerable<ExplorerEntry> filtered = _displayedEntries;

        if (!string.IsNullOrEmpty(query))
        {
            filtered = _displayedEntries.Where(e =>
                e.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                e.Path.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                e.Description.Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        var list = filtered.ToList();
        if (_currentFolder != null) list.Insert(0, BackEntry());
        ExplorerListView.ItemsSource = list;
    }

    ExplorerEntry? _backEntry;
    ExplorerEntry BackEntry() => _backEntry ??= new ExplorerEntry
    {
        Name = "상위 폴더로",
        Description = "이전 폴더(루트 목록)로 돌아갑니다",
        Icon = GameLibraryScanner.GetUpFolderIcon(),
        IsFolder = true,
        IsBack = true
    };

    private void ExplorerListView_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        ActivateSelectedItem();
    }

    private void ExplorerListView_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            ActivateSelectedItem();
            e.Handled = true;
        }
        else if (e.Key == Key.Back && _currentFolder != null)
        {
            NavigateToRoot();
            e.Handled = true;
        }
    }

    private void ActivateSelectedItem()
    {
        if (ExplorerListView.SelectedItem is ExplorerEntry item)
        {
            if (item.IsBack)
            {
                NavigateToRoot();
            }
            else if (item.IsFolder)
            {
                _ = NavigateToFolderAsync(item);
            }
            else if (item.Game != null)
            {
                GameSelected?.Invoke(item.Game.DirectoryPath);
                Close();
            }
        }
    }

    private void BtnBack_Click(object sender, RoutedEventArgs e)
    {
        NavigateToRoot();
    }

    private void BtnAddFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog
        {
            Title = "탐색할 게임 폴더 선택"
        };
        if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.FolderName))
        {
            string folder = dlg.FolderName;
            if (!_ctl.Settings.CustomScanFolders.Contains(folder, StringComparer.OrdinalIgnoreCase))
            {
                _ctl.Settings.CustomScanFolders.Add(folder);
                _ctl.SaveSettings();
                InitRootFolders();
                NavigateToRoot();
            }
        }
    }

    private void BtnRemoveFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_currentFolder != null && _ctl.Settings.CustomScanFolders.Remove(_currentFolder.Path))
        {
            _ctl.SaveSettings();
            _folderGamesCache.Remove(_currentFolder.Path);
            InitRootFolders();
            NavigateToRoot();
            MessageBox.Show(this, "선택한 폴더가 목록에서 제거되었습니다.", "폴더 제거", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void BtnRefresh_Click(object sender, RoutedEventArgs e)
    {
        if (_currentFolder != null)
        {
            _folderGamesCache.Remove(_currentFolder.Path);
            _ = NavigateToFolderAsync(_currentFolder);
        }
        else
        {
            _folderGamesCache.Clear();
            InitRootFolders();
            NavigateToRoot();
        }
    }

    private void BtnLaunchSelected_Click(object sender, RoutedEventArgs e)
    {
        ActivateSelectedItem();
    }

    private void BtnViewIcons_Click(object sender, RoutedEventArgs e)
    {
        SetViewMode(true);
    }

    private void BtnViewList_Click(object sender, RoutedEventArgs e)
    {
        SetViewMode(false);
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        ApplyFilter();
    }

    private void BtnClearSearch_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Text = "";
    }

    private void ExplorerListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ExplorerListView.SelectedItem is ExplorerEntry item && !item.IsFolder && item.Game != null)
        {
            BtnLaunchSelected.IsEnabled = true;
        }
        else
        {
            BtnLaunchSelected.IsEnabled = ExplorerListView.SelectedItem is ExplorerEntry;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _scanCts?.Cancel();
        _scanCts?.Dispose();
        base.OnClosed(e);
    }
}
