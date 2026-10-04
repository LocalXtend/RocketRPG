#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using RocketRPG.Models;

namespace RocketRPG.Views;

/// <summary>멀티 창들의 공통 모양 (윈도우 기본 스타일)</summary>
internal abstract class MultiDialog : Window
{
    protected MultiDialog(Window owner, string title, double width)
    {
        Owner = owner;
        Title = title;
        Width = width;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = SystemColors.ControlBrush;
        FontFamily = owner.FontFamily;
    }

    protected static Grid FormGrid(out Action<string, UIElement> addRow)
    {
        var grid = new Grid { Margin = new Thickness(12, 12, 12, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        int row = 0;
        addRow = (label, editor) =>
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var l = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4, 12, 4) };
            Grid.SetRow(l, row);
            Grid.SetRow(editor, row);
            Grid.SetColumn(editor, 1);
            if (editor is FrameworkElement fe) fe.Margin = new Thickness(0, 4, 0, 4);
            grid.Children.Add(l);
            grid.Children.Add(editor);
            row++;
        };
        return grid;
    }

    protected static StackPanel OkCancel(string okText, Action onOk, out Button ok)
    {
        ok = new Button { Content = okText, MinWidth = 75, Padding = new Thickness(8, 1, 8, 1), IsDefault = true, Margin = new Thickness(0, 0, 6, 0) };
        var cancel = new Button { Content = "취소", Width = 75, IsCancel = true };
        ok.Click += (_, _) => onOk();
        return new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(12, 4, 12, 12), Children = { ok, cancel } };
    }

    protected static void ShowError(Window owner, string message) =>
        MessageBox.Show(owner, message, "멀티", MessageBoxButton.OK, MessageBoxImage.Warning);
}

/// <summary>멀티 > 방 생성하기: 방 이름, 비밀번호(선택), 최대 인원, 목록에 표시</summary>
internal sealed class MultiCreateWindow : MultiDialog
{
    readonly TextBox _title = new() { Padding = new Thickness(3, 2, 3, 2), MaxLength = 40 };
    readonly PasswordBox _password = new() { Padding = new Thickness(3, 2, 3, 2), MaxLength = 32 };
    readonly ComboBox _max = new() { Width = 80, HorizontalAlignment = HorizontalAlignment.Left };
    readonly CheckBox _listed = new() { Content = "방 목록에 표시 (끄면 방 코드로만 들어올 수 있음)", IsChecked = true };

    public (string title, string password, int max, bool listed) Result { get; private set; }

    public MultiCreateWindow(Window owner, string defaultTitle) : base(owner, "방 생성하기", 400)
    {
        _title.Text = defaultTitle;
        for (int i = 2; i <= 8; i++) _max.Items.Add($"{i}명");
        _max.SelectedIndex = 2;   // 4명
        var grid = FormGrid(out var add);
        add("방 이름", _title);
        add("비밀번호", _password);
        add("최대 인원", _max);
        add("", _listed);
        var hint = new TextBlock
        {
            Text = "비밀번호는 비워 두면 누구나 들어올 수 있습니다. 인원이 많을수록 방장 인터넷 업로드가 더 필요합니다 (참가자 1명당 약 1~8Mbps, 방송 화질에 따라).",
            TextWrapping = TextWrapping.Wrap, Foreground = SystemColors.GrayTextBrush, Margin = new Thickness(12, 0, 12, 4)
        };
        Content = new StackPanel { Children = { grid, hint, OkCancel("만들기", Accept, out _) } };
        Loaded += (_, _) => { _title.Focus(); _title.SelectAll(); };
    }

    void Accept()
    {
        string title = _title.Text.Trim();
        if (title.Length == 0) { ShowError(this, "방 이름을 입력해 주세요."); return; }
        Result = (title, _password.Password, _max.SelectedIndex + 2, _listed.IsChecked == true);
        DialogResult = true;
    }
}

/// <summary>멀티 > 방 입장하기: 공개 방 목록 + 방 코드로 입장</summary>
internal sealed class MultiJoinWindow : MultiDialog
{
    readonly ListView _list = new() { Height = 260 };
    readonly TextBox _code = new() { Width = 110, Padding = new Thickness(3, 2, 3, 2), MaxLength = 6, CharacterCasing = CharacterCasing.Upper };
    readonly TextBlock _status = new() { Foreground = SystemColors.GrayTextBrush, Margin = new Thickness(12, 0, 12, 4) };

    /// <summary>들어갈 방 (id, 비밀번호가 필요한지)</summary>
    public MultiRoomSummary? Selected { get; private set; }

    public MultiJoinWindow(Window owner) : base(owner, "방 입장하기", 560)
    {
        var view = new GridView();
        view.Columns.Add(new GridViewColumn { Header = "", Width = 26, DisplayMemberBinding = new System.Windows.Data.Binding(nameof(Row.Lock)) });
        view.Columns.Add(new GridViewColumn { Header = "방 이름", Width = 210, DisplayMemberBinding = new System.Windows.Data.Binding(nameof(Row.Title)) });
        view.Columns.Add(new GridViewColumn { Header = "게임", Width = 180, DisplayMemberBinding = new System.Windows.Data.Binding(nameof(Row.Game)) });
        view.Columns.Add(new GridViewColumn { Header = "인원", Width = 70, DisplayMemberBinding = new System.Windows.Data.Binding(nameof(Row.People)) });
        _list.View = view;
        _list.MouseDoubleClick += (_, _) => { if (_list.SelectedItem is Row r) Pick(r.Room); };

        var refresh = new Button { Content = "새로 고침", Padding = new Thickness(8, 1, 8, 1) };
        refresh.Click += async (_, _) => await LoadAsync();
        var byCode = new Button { Content = "코드로 입장", Padding = new Thickness(8, 1, 8, 1), Margin = new Thickness(6, 0, 0, 0) };
        byCode.Click += async (_, _) => await JoinByCodeAsync();
        var codeRow = new DockPanel { Margin = new Thickness(12, 0, 12, 8) };
        var codePanel = new StackPanel { Orientation = Orientation.Horizontal, Children = { new TextBlock { Text = "방 코드", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) }, _code, byCode } };
        DockPanel.SetDock(refresh, Dock.Right);
        codeRow.Children.Add(refresh);
        codeRow.Children.Add(codePanel);
        _code.KeyDown += async (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; await JoinByCodeAsync(); } };

        var listBox = new Border { Margin = new Thickness(12, 12, 12, 8), Child = _list };
        Content = new StackPanel
        {
            Children =
            {
                listBox, _status, codeRow,
                OkCancel("입장", () => { if (_list.SelectedItem is Row r) Pick(r.Room); else ShowError(this, "들어갈 방을 고르거나 방 코드를 입력해 주세요."); }, out _)
            }
        };
        Loaded += async (_, _) => await LoadAsync();
    }

    sealed record Row(MultiRoomSummary Room)
    {
        public string Lock => Room.Locked ? "🔒" : "";
        public string Title => Room.Title;
        public string Game => Room.HostAway ? "(방장 연결 끊김)" : string.IsNullOrEmpty(Room.Game) ? "(대기 중)" : Room.Game;
        public string People => $"{Room.Count}/{Room.Max}";
    }

    async Task LoadAsync()
    {
        _status.Text = "방 목록을 불러오는 중...";
        try
        {
            var rooms = await MultiClient.ListRoomsAsync();
            _list.ItemsSource = rooms.Select(r => new Row(r)).ToList();
            _status.Text = rooms.Count == 0 ? "지금 열린 공개 방이 없습니다. 방 코드를 받았다면 아래에 입력하세요." : $"공개 방 {rooms.Count}개";
        }
        catch (Exception ex)
        {
            UiLog.Write($"multi: list failed {ex.Message}");
            _status.Text = "서버에 연결하지 못했습니다. 인터넷 연결을 확인해 주세요.";
        }
    }

    async Task JoinByCodeAsync()
    {
        string code = _code.Text.Trim();
        if (code.Length < 4) { ShowError(this, "방 코드를 입력해 주세요."); return; }
        try
        {
            var room = await MultiClient.FindByCodeAsync(code);
            if (room == null) { ShowError(this, "그 코드의 방을 찾지 못했습니다."); return; }
            Pick(room);
        }
        catch (Exception ex)
        {
            UiLog.Write($"multi: code lookup failed {ex.Message}");
            ShowError(this, "서버에 연결하지 못했습니다.");
        }
    }

    void Pick(MultiRoomSummary room)
    {
        if (room.Count >= room.Max) { ShowError(this, "방이 가득 찼습니다."); return; }
        Selected = room;
        DialogResult = true;
    }
}

/// <summary>비밀번호 입력 (가려진 칸)</summary>
internal static class PasswordDialog
{
    public static string? Ask(Window owner, string title, string label)
    {
        var box = new PasswordBox { Margin = new Thickness(0, 4, 0, 10), Padding = new Thickness(3, 2, 3, 2) };
        var ok = new Button { Content = "확인", Width = 75, IsDefault = true, Margin = new Thickness(0, 0, 6, 0) };
        var cancel = new Button { Content = "취소", Width = 75, IsCancel = true };
        var panel = new StackPanel
        {
            Margin = new Thickness(12),
            Children = { new TextBlock { Text = label }, box, new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { ok, cancel } } }
        };
        var win = new Window
        {
            Title = title, Content = panel, Owner = owner, Width = 320, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false, Background = SystemColors.ControlBrush, FontFamily = owner.FontFamily
        };
        ok.Click += (_, _) => win.DialogResult = true;
        win.Loaded += (_, _) => box.Focus();
        return win.ShowDialog() == true ? box.Password : null;
    }
}

/// <summary>
/// 채팅 글자색(5가지)과 고정 채팅 고르기. 고른 값은 다음에 열어도 그대로입니다.
/// 흰색이 아닌 색으로 바꾸면 30초 동안은 다른 색(흰색 말고)으로 바꿀 수 없습니다 (색을 계속 바꾸지 못하게). 흰색으로는 언제든 바꿉니다.
/// </summary>
internal sealed class ChatStylePicker : StackPanel
{
    static int _lastColor;
    static bool _lastFixed;
    readonly ComboBox _color = new() { Width = 110 };
    readonly TextBlock _colorWait = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0), Foreground = SystemColors.GrayTextBrush };
    readonly CheckBox _fixed = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0),
        ToolTip = "글이 흘러가지 않고 화면 위쪽 한 줄에 6초 동안 머뭅니다. 90초에 한 번 보낼 수 있습니다." };
    readonly Func<int> _waitSeconds;
    readonly System.Windows.Threading.DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    bool _syncing;
    public int Color => _color.SelectedIndex;
    public bool Fixed => _fixed.IsChecked == true && _waitSeconds() <= 0;

    public ChatStylePicker(Func<int> waitSeconds)
    {
        _waitSeconds = waitSeconds;
        Orientation = Orientation.Horizontal;
        Margin = new Thickness(8, 0, 8, 6);
        for (int i = 0; i < MultiChatStyle.Names.Length; i++)
        {
            uint c = MultiChatStyle.Colors[i];
            var swatch = new Border
            {
                Width = 14, Height = 14, Margin = new Thickness(0, 0, 6, 0), BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1),
                Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb((byte)(c >> 16), (byte)(c >> 8), (byte)c)),
            };
            _color.Items.Add(new ComboBoxItem { Content = new StackPanel { Orientation = Orientation.Horizontal, Children = { swatch, new TextBlock { Text = MultiChatStyle.Names[i] } } } });
        }
        _lastColor = Math.Clamp(_lastColor, 0, MultiChatStyle.Names.Length - 1);
        _color.SelectedIndex = _lastColor;
        _fixed.IsChecked = _lastFixed;
        _color.SelectionChanged += (_, _) =>
        {
            if (_syncing || _color.SelectedIndex < 0) return;
            if (MultiChatStyle.ColorCooldown.TryChange(_lastColor, _color.SelectedIndex, Environment.TickCount64)) _lastColor = _color.SelectedIndex;
            RefreshWait();   // 못 바꿨으면 원래 색으로 돌아감
        };
        _fixed.Click += (_, _) => { _lastFixed = _fixed.IsChecked == true; RefreshWait(); };
        Children.Add(_color);
        Children.Add(_colorWait);
        Children.Add(_fixed);
        _timer.Tick += (_, _) => RefreshWait();
        Loaded += (_, _) => { RefreshWait(); _timer.Start(); };
        Unloaded += (_, _) => _timer.Stop();
    }

    /// <summary>색 바꾸기·고정 채팅 남은 대기 시간을 보여 줌 (기다리는 동안은 고를 수 없음)</summary>
    void RefreshWait()
    {
        // 다른 채팅 창에서 바꾼 색을 따라감
        if (_color.SelectedIndex != _lastColor) { _syncing = true; _color.SelectedIndex = _lastColor; _syncing = false; }
        int colorWait = MultiChatStyle.ColorCooldown.WaitSeconds(Environment.TickCount64);
        for (int i = 1; i < _color.Items.Count; i++)
            if (_color.Items[i] is ComboBoxItem item) item.IsEnabled = colorWait <= 0 || i == _lastColor;
        _colorWait.Text = colorWait > 0 ? $"색 변경 {colorWait}초 뒤" : "";
        _colorWait.Visibility = colorWait > 0 ? Visibility.Visible : Visibility.Collapsed;
        _color.ToolTip = "흰색이 아닌 색은 30초에 한 번 바꿀 수 있습니다. 흰색으로는 언제든 바꿀 수 있습니다.";

        int wait = _waitSeconds();
        // 기다리는 동안은 켤 수 없지만, 켜 둔 것을 끄는 것은 됨 (보낸 뒤 체크된 채로 막혀 있지 않게)
        _fixed.IsEnabled = wait <= 0 || _fixed.IsChecked == true;
        _fixed.Content = wait > 0 ? $"고정 ({wait}초 뒤 가능)" : "고정 (6초 · 90초마다)";
    }
}

internal sealed class ChatInputWindow : Window
{
    readonly TextBox _box = new() { MaxLength = 120, Padding = new Thickness(3, 2, 3, 2), VerticalContentAlignment = VerticalAlignment.Center };
    readonly ChatStylePicker _style;
    bool _closing;

    public event Action<string, int, bool>? Sent;

    public ChatInputWindow(Window owner, Func<int> fixedWaitSeconds)
    {
        _style = new ChatStylePicker(fixedWaitSeconds);
        Owner = owner;
        Title = "채팅";
        WindowStyle = WindowStyle.ToolWindow;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        SizeToContent = SizeToContent.Height;
        Width = 420;
        Background = SystemColors.ControlBrush;
        FontFamily = owner.FontFamily;
        var send = new Button { Content = "보내기", MinWidth = 64, Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(8, 1, 8, 1) };
        send.Click += (_, _) => Send();
        var row = new DockPanel { Margin = new Thickness(8, 8, 8, 4) };
        DockPanel.SetDock(send, Dock.Right);
        row.Children.Add(send);
        row.Children.Add(_box);
        var hint = new TextBlock { Text = "Enter 보내기 · Esc 닫기 · 게임 화면 위로 이름 없이 흘러갑니다", Foreground = SystemColors.GrayTextBrush, Margin = new Thickness(8, 0, 8, 6), FontSize = 11 };
        Content = new StackPanel { Children = { row, _style, hint } };
        _box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { e.Handled = true; Send(); }
            else if (e.Key == Key.Escape) { e.Handled = true; Close(); }
        };
        Loaded += (_, _) => { Activate(); _box.Focus(); };
        Deactivated += (_, _) => { if (!_closing) Close(); };   // 다른 곳을 누르면 닫힘
        Closing += (_, _) => _closing = true;
    }

    void Send()
    {
        string text = _box.Text.Trim();
        if (text.Length > 0) Sent?.Invoke(text, _style.Color, _style.Fixed);
        Close();
    }
}

/// <summary>멀티 채팅 기록: 누가 무엇을 말했는지 (화면에 흐르는 글에는 이름이 없음). 아래 칸에서 바로 보낼 수도 있습니다.</summary>
internal sealed class ChatLogWindow : Window
{
    readonly ChatStylePicker _style;
    readonly ListBox _list = new() { BorderThickness = new Thickness(1), HorizontalContentAlignment = HorizontalAlignment.Stretch };
    readonly TextBox _input = new() { MaxLength = 120, Padding = new Thickness(3, 2, 3, 2), VerticalContentAlignment = VerticalAlignment.Center };

    public event Action<string, int, bool>? Sent;

    public ChatLogWindow(Window owner, Func<int> fixedWaitSeconds)
    {
        _style = new ChatStylePicker(fixedWaitSeconds);
        Owner = owner;
        Title = "채팅 기록";
        Width = 420;
        Height = 480;
        MinWidth = 260;
        MinHeight = 200;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = SystemColors.ControlBrush;
        FontFamily = owner.FontFamily;
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
        var copy = new Button { Content = "모두 복사", Padding = new Thickness(8, 1, 8, 1), Margin = new Thickness(6, 0, 0, 0) };
        var send = new Button { Content = "보내기", MinWidth = 64, Padding = new Thickness(8, 1, 8, 1), Margin = new Thickness(6, 0, 0, 0) };
        send.Click += (_, _) => SendInput();
        _input.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; SendInput(); } };
        copy.Click += (_, _) =>
        {
            var lines = _list.Items.OfType<TextBlock>().Select(t => t.Text);
            try { Clipboard.SetText(string.Join(Environment.NewLine, lines)); } catch { }
        };
        var bottom = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
        DockPanel.SetDock(copy, Dock.Right);
        DockPanel.SetDock(send, Dock.Right);
        bottom.Children.Add(copy);
        bottom.Children.Add(send);
        bottom.Children.Add(_input);
        var root = new DockPanel { Margin = new Thickness(8) };
        DockPanel.SetDock(bottom, Dock.Bottom);
        DockPanel.SetDock(_style, Dock.Bottom);
        root.Children.Add(_style);
        root.Children.Add(bottom);
        root.Children.Add(_list);
        Content = root;
    }

    void SendInput()
    {
        string text = _input.Text.Trim();
        if (text.Length == 0) return;
        Sent?.Invoke(text, _style.Color, _style.Fixed);
        _input.Clear();
    }

    public void SetLines(IReadOnlyList<MultiChatLine> lines)
    {
        bool atEnd = _list.Items.Count == 0 || _list.SelectedIndex < 0;
        _list.Items.Clear();
        foreach (var l in lines)
        {
            var tb = new TextBlock { TextWrapping = TextWrapping.Wrap, Text = $"[{l.Time:HH:mm}] {l.Name}: {(l.Fixed ? "[고정] " : "")}{l.Text}" };
            if (l.Mine) tb.Foreground = SystemColors.HotTrackBrush;
            else if (l.Color == 1) tb.Foreground = Brushes.Firebrick;   // 빨간 글은 기록에서도 눈에 띄게 (연한 색은 흰 바탕에서 안 보여 기본색)
            _list.Items.Add(tb);
        }
        if (atEnd && _list.Items.Count > 0) _list.ScrollIntoView(_list.Items[^1]);
    }
}
