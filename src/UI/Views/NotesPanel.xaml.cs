#nullable enable
using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using RocketRPG.Models;

namespace RocketRPG.Views;

/// <summary>
/// 게임 노트 / 일반 메모 패널 (MainWindow 오른쪽에 도킹하거나 따로 창으로). 게임마다 RocketRPG\config\notes\ 에 따로,
/// 일반 메모는 notes\_general\ 에 저장됩니다. 게임이 없으면 일반 메모를 보여 줍니다.
/// 패널이 닫혀 있는 동안에는 캔버스가 아무것도 그리지 않습니다(GIF 타이머 정지).
/// </summary>
public partial class NotesPanel : UserControl
{
    NotesStore? _store;          // 지금 보이는 노트
    NotesStore? _gameStore;      // 실행 중인 게임의 노트
    NotesStore? _generalStore;   // 일반 메모
    NotesStore? _roomStore;      // 멀티 참가자: 방장의 게임 노트 (사본)
    bool _roomReadOnly;          // 방장이 노트 고치기를 잠갔음
    bool _switching;

    public event Action? CloseRequested;
    public event Action? ScreenshotRequested;
    public event Action? GalleryRequested;     // 일반 메모에서 '보관함에서...'
    public event Action? DetachRequested;

    public NotesPanel()
    {
        InitializeComponent();
        Canvas.Changed += () => _store?.MarkDirty();
    }

    public NotesStore? Store => _store;

    /// <summary>실행 중인 게임의 노트 (멀티 방장이 공유하는 노트)</summary>
    public NotesStore? GameStore => _gameStore;

    /// <summary>멀티 참가자: 방 노트(방장의 게임 노트)를 보여 줍니다. 게임 노트 자리에 들어갑니다.</summary>
    public void OpenRoom(NotesStore room)
    {
        _roomStore = room;
        ShowStore(room);
        UpdateModeBar();
    }

    /// <summary>방을 나가면 방 노트를 닫고 게임 노트나 일반 메모로 돌아갑니다.</summary>
    public void CloseRoom()
    {
        if (_roomStore == null) return;
        bool showing = ReferenceEquals(_store, _roomStore);
        _roomStore = null;
        _roomReadOnly = false;
        if (showing)
        {
            _store = null;
            if (_gameStore != null) ShowStore(_gameStore); else ShowGeneral();
        }
        UpdateModeBar();
    }

    /// <summary>방 노트를 읽기만 할지 (방장이 잠금)</summary>
    public void SetRoomReadOnly(bool readOnly)
    {
        _roomReadOnly = readOnly;
        ApplyEditMode();
    }

    void ApplyEditMode()
    {
        bool ro = _store?.IsRoom == true && _roomReadOnly;
        Canvas.ReadOnly = ro;
        BtnText.IsEnabled = BtnLabel.IsEnabled = BtnImage.IsEnabled = BtnShot.IsEnabled = !ro;
    }

    /// <summary>
    /// 멀티 동기화로 노트가 바뀜: 페이지가 바뀌었으면 탭을 다시, 항목은 바뀐 것만 다시 그립니다.
    /// </summary>
    public void RefreshFromSync(NotesStore store, System.Collections.Generic.IEnumerable<string> itemIds, bool pagesChanged, bool force = false)
    {
        if (!ReferenceEquals(_store, store)) return;
        if (store.IsRoom) TitleText.Text = $"방 노트 — {store.Doc.GameTitle}";
        if (pagesChanged)
        {
            store.Doc.ActivePage = Math.Clamp(store.Doc.ActivePage, 0, store.Doc.Pages.Count - 1);
            BuildTabs();
        }
        if (Canvas.Page == null || !store.Doc.Pages.Contains(Canvas.Page)) Canvas.Show(store, store.ActivePage);
        else Canvas.RefreshItems(itemIds, force);
    }

    /// <summary>멀티: 이 항목을 사용자가 지금 다루는 중인지 (보이는 노트일 때만)</summary>
    public bool IsItemBusy(NotesStore store, string id) => ReferenceEquals(_store, store) && Canvas.IsBusy(id);

    /// <summary>분리 창에 있으면 버튼이 '붙이기'가 됩니다.</summary>
    public bool Detached
    {
        set
        {
            BtnDetach.Content = value ? "붙이기" : "분리";
            BtnDetach.ToolTip = value ? "노트를 RocketRPG 창 오른쪽에 다시 붙이기" : "노트를 따로 창으로 띄우기";
        }
    }

    /// <summary>게임을 시작하면 그 게임의 노트로 바꿉니다 (같은 게임이면 그대로).</summary>
    public void OpenGame(string gameDir, string title)
    {
        if (string.IsNullOrEmpty(gameDir)) return;
        if (_gameStore == null || !SettingsService.SameGameDir(_gameStore.GameDir, gameDir))
        {
            _gameStore?.Flush();
            _gameStore = new NotesStore(gameDir, title);
            ShowStore(_gameStore);
        }
        else if (_store == null) ShowStore(_gameStore);
        UpdateModeBar();
    }

    /// <summary>게임이 끝나면 일반 메모로 돌아갑니다.</summary>
    public void CloseGame()
    {
        _gameStore?.Flush();
        _gameStore = null;
        ShowGeneral();
    }

    /// <summary>보이는 노트가 없으면 일반 메모를 엽니다.</summary>
    public void EnsureOpen()
    {
        if (_store == null) ShowGeneral();
    }

    /// <summary>멀티 참가자: 방 노트를 보여 줌 (스크린샷을 방 노트에 넣을 때)</summary>
    public void ShowRoomNotes()
    {
        if (_roomStore != null && !ReferenceEquals(_store, _roomStore)) { ShowStore(_roomStore); UpdateModeBar(); }
    }

    void ShowGeneral()
    {
        _generalStore ??= new NotesStore("", "일반 메모");
        ShowStore(_generalStore);
        UpdateModeBar();
    }

    void ShowStore(NotesStore store)
    {
        if (ReferenceEquals(_store, store)) return;
        _store?.Flush();
        _store = store;
        TitleText.Text = store.IsRoom ? $"방 노트 — {store.Doc.GameTitle}" : store.IsGeneral ? "일반 메모" : $"게임 노트 — {store.Doc.GameTitle}";
        TitleText.ToolTip = store.Folder;
        // 일반 메모에서는 게임 화면 대신 스크린샷 보관함에서 고릅니다
        BtnShot.Content = store.IsGeneral ? "보관함에서..." : "스크린샷";
        BtnShot.ToolTip = store.IsGeneral ? "스크린샷 보관함에서 골라 넣기" : "지금 게임 화면을 찍어 노트에 넣기 (Ctrl+Shift+S)";
        BuildTabs();
        Canvas.Show(store, store.ActivePage);
        ApplyEditMode();
    }

    void UpdateModeBar()
    {
        _switching = true;
        var first = _roomStore ?? _gameStore;
        ModeBar.Visibility = first != null ? Visibility.Visible : Visibility.Collapsed;
        ModeGame.Content = _roomStore != null ? "방 노트" : "게임 노트";
        ModeGame.IsChecked = _store != null && ReferenceEquals(_store, first);
        ModeGeneral.IsChecked = _store != null && _store.IsGeneral;
        _switching = false;
    }

    void ModeGame_Checked(object sender, RoutedEventArgs e)
    {
        if (_switching || (_roomStore ?? _gameStore) == null) return;
        ShowStore((_roomStore ?? _gameStore)!);
    }

    void ModeGeneral_Checked(object sender, RoutedEventArgs e)
    {
        if (_switching) return;
        ShowGeneral();
    }

    public void Flush()
    {
        _gameStore?.Flush();
        _generalStore?.Flush();
    }

    /// <summary>스크린샷(PNG)을 지금 페이지의 화면 가운데에 넣습니다.</summary>
    public void AddScreenshot(byte[] png)
    {
        if (_store == null) return;
        Canvas.AddImage(_store.SavePng(png, "shot"));
        _store.MarkDirty();
    }

    /// <summary>이미지 파일(보관함의 스크린샷 등)을 복사해 넣습니다. 노트는 자기 사본을 가져 원본을 지워도 남습니다.</summary>
    public void AddImageFile(string path)
    {
        EnsureOpen();
        if (_store == null) return;
        Canvas.AddImage(_store.ImportImageFile(path));
        _store.MarkDirty();
    }
    // ── 페이지 탭 ──

    void BuildTabs()
    {
        PageTabs.Children.Clear();
        if (_store == null) return;
        var doc = _store.Doc;
        bool manage = !_store.IsRoom;   // 방 노트의 페이지 관리는 방장만
        for (int i = 0; i < doc.Pages.Count; i++)
        {
            int index = i;
            var page = doc.Pages[i];
            var tab = new ToggleButton
            {
                Content = page.Title,
                IsChecked = i == doc.ActivePage,
                Padding = new Thickness(8, 2, 8, 2),
                Margin = new Thickness(0, 0, 3, 4),
                ToolTip = manage ? "두 번 누르면 이름 바꾸기 · 오른쪽 클릭: 메뉴" : "방 노트의 페이지는 방장이 관리합니다"
            };
            tab.Click += (_, _) => SwitchPage(index);
            PageTabs.Children.Add(tab);
            if (!manage) continue;
            tab.MouseDoubleClick += (_, e) => { RenamePage(index); e.Handled = true; };
            var menu = new ContextMenu();
            var rename = new MenuItem { Header = "이름 바꾸기" };
            rename.Click += (_, _) => RenamePage(index);
            var del = new MenuItem { Header = "페이지 삭제", IsEnabled = doc.Pages.Count > 1 };
            del.Click += (_, _) => DeletePage(index);
            menu.Items.Add(rename);
            menu.Items.Add(del);
            tab.ContextMenu = menu;
        }
        if (!manage) return;
        var add = new Button { Content = "+", Width = 24, Margin = new Thickness(0, 0, 0, 4), ToolTip = "새 페이지" };
        add.Click += (_, _) => AddPage();
        PageTabs.Children.Add(add);
    }

    void SwitchPage(int index)
    {
        if (_store == null) return;
        _store.Doc.ActivePage = index;
        _store.MarkDirty();
        // 탭을 다시 만들지 않고 눌린 상태만 바꿉니다. 다시 만들면 두 번 누르기(이름 바꾸기)가 사라진 탭에 가서 동작하지 않았음.
        int i = 0;
        foreach (var tb in PageTabs.Children.OfType<ToggleButton>()) tb.IsChecked = i++ == index;
        Canvas.Show(_store, _store.ActivePage);
    }

    void AddPage()
    {
        if (_store == null) return;
        _store.Doc.Pages.Add(new NotePage { Title = $"노트 {_store.Doc.Pages.Count + 1}" });
        BuildTabs();
        SwitchPage(_store.Doc.Pages.Count - 1);
    }

    void RenamePage(int index)
    {
        if (_store == null) return;
        var page = _store.Doc.Pages[index];
        string? name = InputDialog.Ask(Window.GetWindow(this), "페이지 이름", "새 이름:", page.Title);
        if (string.IsNullOrWhiteSpace(name)) return;
        page.Title = name.Trim();
        _store.MarkDirty();
        BuildTabs();
    }

    void DeletePage(int index)
    {
        if (_store == null || _store.Doc.Pages.Count <= 1) return;
        var page = _store.Doc.Pages[index];
        if (page.Items.Count > 0 &&
            MessageBox.Show(Window.GetWindow(this), $"'{page.Title}' 페이지와 안의 메모·이미지 {page.Items.Count}개를 지울까요?",
                "게임 노트", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;
        _store.Doc.Pages.RemoveAt(index);
        _store.Doc.ActivePage = Math.Clamp(_store.Doc.ActivePage >= index ? _store.Doc.ActivePage - 1 : _store.Doc.ActivePage, 0, _store.Doc.Pages.Count - 1);
        _store.CleanupUnusedImages();
        BuildTabs();
        SwitchPage(_store.Doc.ActivePage);
    }

    // ── 도구 모음 ──

    void BtnText_Click(object sender, RoutedEventArgs e) => Canvas.AddText();
    void BtnLabel_Click(object sender, RoutedEventArgs e) => Canvas.AddLabel();
    void BtnImage_Click(object sender, RoutedEventArgs e) => Canvas.AddImageFromDialog();
    void BtnShot_Click(object sender, RoutedEventArgs e)
    {
        if (_store?.IsGeneral == true) GalleryRequested?.Invoke();
        else ScreenshotRequested?.Invoke();
    }

    void BtnDetach_Click(object sender, RoutedEventArgs e) => DetachRequested?.Invoke();
    void BtnFit_Click(object sender, RoutedEventArgs e) => Canvas.FitAll();
    void BtnZoom100_Click(object sender, RoutedEventArgs e) => Canvas.ResetZoom();
    void BtnClose_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();
}

/// <summary>한 줄 입력 창 (윈도우 기본 모양)</summary>
internal static class InputDialog
{
    public static string? Ask(Window? owner, string title, string label, string initial)
    {
        var box = new TextBox { Text = initial, Margin = new Thickness(0, 4, 0, 10), Padding = new Thickness(3, 2, 3, 2) };
        var ok = new Button { Content = "확인", Width = 75, IsDefault = true, Margin = new Thickness(0, 0, 6, 0) };
        var cancel = new Button { Content = "취소", Width = 75, IsCancel = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        var panel = new StackPanel { Margin = new Thickness(12) };
        panel.Children.Add(new TextBlock { Text = label });
        panel.Children.Add(box);
        panel.Children.Add(buttons);
        var win = new Window
        {
            Title = title,
            Content = panel,
            Owner = owner,
            Width = 320,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            Background = SystemColors.ControlBrush,
            FontFamily = owner?.FontFamily ?? new System.Windows.Media.FontFamily("맑은 고딕")
        };
        ok.Click += (_, _) => win.DialogResult = true;
        win.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        return win.ShowDialog() == true ? box.Text : null;
    }
}
