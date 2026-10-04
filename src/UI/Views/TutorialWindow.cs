#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using RocketRPG.Models;

namespace RocketRPG.Views;

/// <summary>
/// 정보 &gt; 사용법: 왼쪽 목차, 오른쪽 그림과 따라 하기.
/// 그림은 지금 RocketRPG의 실제 메뉴 줄과 실제 창을 그대로 그려(누를 수는 없음) 빨간 테두리와 번호를 붙입니다.
/// 게임 화면 자리는 직접 그린 예시입니다. 메뉴별 기능 쪽은 실제 메뉴에서 만들어 메뉴가 바뀌어도 빠지는 항목이 없습니다.
/// </summary>
internal sealed class TutorialWindow : Window
{
    static readonly Brush Red = Freeze(new SolidColorBrush(Color.FromRgb(0xE0, 0x1F, 0x1F)));
    static readonly Brush Highlight = Freeze(new SolidColorBrush(Color.FromRgb(0xCC, 0xE8, 0xFF)));
    static readonly Brush MenuBorder = Freeze(new SolidColorBrush(Color.FromRgb(0xA0, 0xA0, 0xA0)));

    readonly MainWindow _main;
    readonly Menu _menu;
    readonly TreeView _tree = new() { Width = 230, Margin = new Thickness(0, 0, 8, 0) };
    readonly ScrollViewer _scroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };

    public TutorialWindow(MainWindow owner, Menu mainMenu, string? startPage = null)
    {
        _main = owner;
        _menu = mainMenu;
        Owner = owner;
        Title = "RocketRPG 사용법";
        Width = 980;
        Height = 700;
        MinWidth = 700;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = SystemColors.ControlBrush;
        FontFamily = owner.FontFamily;

        TreeViewItem? select = null;
        foreach (var section in TutorialContent.Sections)
        {
            var node = new TreeViewItem { Header = section.Title, IsExpanded = true, FontWeight = FontWeights.SemiBold };
            foreach (var page in section.Pages)
            {
                var item = new TreeViewItem { Header = page.Title, Tag = page, FontWeight = FontWeights.Normal };
                node.Items.Add(item);
                if (page.Id == startPage || select == null) select ??= item;
                if (page.Id == startPage) select = item;
            }
            _tree.Items.Add(node);
        }
        // 메뉴별 기능: 실제 메뉴 줄의 탭 이름 아래로 (예: 도구 > 벽 통과)
        var menus = new TreeViewItem { Header = "메뉴별 기능", IsExpanded = false, FontWeight = FontWeights.SemiBold };
        foreach (var top in _menu.Items.OfType<MenuItem>())
            menus.Items.Add(new TreeViewItem { Header = Clean(top.Header), Tag = top, FontWeight = FontWeights.Normal });
        _tree.Items.Add(menus);
        _tree.SelectedItemChanged += (_, _) => Show((_tree.SelectedItem as TreeViewItem)?.Tag);

        var body = new DockPanel { Margin = new Thickness(8) };
        DockPanel.SetDock(_tree, Dock.Left);
        body.Children.Add(_tree);
        body.Children.Add(new Border { Background = Brushes.White, BorderBrush = SystemColors.ActiveBorderBrush, BorderThickness = new Thickness(1), Child = _scroll });
        Content = body;
        Loaded += (_, _) => { if (select != null) select.IsSelected = true; };
    }

    // ── 쪽 ──

    void Show(object? tag)
    {
        var page = new StackPanel { Margin = new Thickness(20, 16, 20, 24), MaxWidth = 720, HorizontalAlignment = HorizontalAlignment.Left };
        if (tag is TutorialPage p) BuildPage(page, p);
        else if (tag is MenuItem top) BuildMenuPage(page, top);
        else page.Children.Add(new TextBlock { Text = "왼쪽에서 보고 싶은 항목을 고르세요.", Foreground = SystemColors.GrayTextBrush });
        _scroll.Content = page;
        _scroll.ScrollToTop();
    }

    void BuildPage(StackPanel page, TutorialPage p)
    {
        page.Children.Add(new TextBlock { Text = p.Title, FontSize = 20, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 6) });
        page.Children.Add(Para(TutorialContent.ApplyKeys(p.Summary), 14));
        if (p.Picture != null) page.Children.Add(Figure(p.Picture));
        foreach (var more in p.More ?? []) page.Children.Add(Figure(more));
        if (p.Steps.Count > 0)
        {
            page.Children.Add(new TextBlock { Text = "따라 하기", FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 14, 0, 6) });
            for (int i = 0; i < p.Steps.Count; i++)
            {
                var row = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
                // 빨간 동그라미 번호는 그림의 빨간 상자에만 씁니다. 따라 하기는 그냥 숫자.
                var num = new TextBlock { Text = $"{i + 1}.", FontSize = 13, FontWeight = FontWeights.SemiBold, MinWidth = 20, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Top };
                DockPanel.SetDock(num, Dock.Left);
                row.Children.Add(num);
                row.Children.Add(Para(TutorialContent.ApplyKeys(p.Steps[i]), 13));
                page.Children.Add(row);
            }
        }
        if (p.Result != null)
        {
            page.Children.Add(new TextBlock { Text = "이렇게 됩니다", FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 14, 0, 6) });
            page.Children.Add(Figure(p.Result));
        }
        if (p.Tip != null)
            page.Children.Add(new Border
            {
                Margin = new Thickness(0, 14, 0, 0), Padding = new Thickness(10, 6, 10, 6), Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xF8, 0xDC)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xE0, 0xC8, 0x70)), BorderThickness = new Thickness(1),
                Child = Para("알아 두면 좋아요: " + TutorialContent.ApplyKeys(p.Tip), 13),
            });
    }

    void BuildMenuPage(StackPanel page, MenuItem top)
    {
        page.Children.Add(new TextBlock { Text = $"{Clean(top.Header)} 메뉴", FontSize = 20, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 6) });
        page.Children.Add(Para("메뉴 줄에서 이 탭을 누르면 나오는 기능입니다. 오른쪽 글자는 지금 정해진 단축키입니다.", 13));
        page.Children.Add(Figure(new TutorialPicture("Menu", Clean(top.Header))));
        void Add(MenuItem mi, int depth)
        {
            string name = Clean(mi.Header);
            if (name.Length == 0) return;
            string? help = Handler(mi) is { } h && TutorialContent.MenuHelp.TryGetValue(h, out var d) ? TutorialContent.ApplyKeys(d) : mi.ToolTip as string;
            bool group = mi.Items.OfType<MenuItem>().Any();
            if (help != null || group)
            {
                var row = new DockPanel { Margin = new Thickness(depth * 18, 6, 0, 0) };
                var gesture = new TextBlock { Text = mi.InputGestureText, Foreground = SystemColors.GrayTextBrush, Margin = new Thickness(12, 0, 0, 0) };
                DockPanel.SetDock(gesture, Dock.Right);
                row.Children.Add(gesture);
                var text = new TextBlock { TextWrapping = TextWrapping.Wrap };
                text.Inlines.Add(new System.Windows.Documents.Run(name) { FontWeight = FontWeights.SemiBold });
                if (help != null) text.Inlines.Add(new System.Windows.Documents.Run(" — " + help));
                row.Children.Add(text);
                page.Children.Add(row);
            }
            // 같은 종류의 고르기(비율, 밝기, 배속 값 등)는 한 줄로 이미 설명했으니 묶음 안만 펼침
            if (group && mi.Items.OfType<MenuItem>().Select(Handler).Distinct().Count() > 1)
                foreach (var child in mi.Items.OfType<MenuItem>()) Add(child, depth + 1);
            else if (group && help == null && mi.Items.OfType<MenuItem>().FirstOrDefault() is { } first && Handler(first) is { } fh && TutorialContent.MenuHelp.TryGetValue(fh, out var gd))
                page.Children.Add(Para(TutorialContent.ApplyKeys(gd), 13, new Thickness(depth * 18 + 18, 2, 0, 0)));
        }
        foreach (var mi in top.Items.OfType<MenuItem>()) Add(mi, 0);
    }

    /// <summary>메뉴 항목의 Click 처리 함수 이름 (사용법 설명을 찾는 열쇠)</summary>
    public static string? Handler(MenuItem mi)
    {
        try
        {
            var store = typeof(UIElement).GetProperty("EventHandlersStore", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(mi);
            if (store == null) return null;
            var get = store.GetType().GetMethod("GetRoutedEventHandlers", BindingFlags.Instance | BindingFlags.Public);
            if (get?.Invoke(store, [MenuItem.ClickEvent]) is RoutedEventHandlerInfo[] handlers && handlers.Length > 0)
                return handlers[0].Handler.Method.Name;
        }
        catch { }
        return null;
    }

    // ── 그림 ──

    FrameworkElement Figure(TutorialPicture pic)
    {
        FrameworkElement content;
        try
        {
            content = pic.Kind switch
            {
                "Menu" => MenuPicture(pic.Args, pic.State),
                "Window" => WindowPicture(pic.Args[0], pic.Args.Skip(1).ToArray()),
                "Bar" => BarPicture(),
                "Vote" => VotePicture(),
                "Chat" => ChatPicture(),
                "Ping" => PingPicture(),
                "Library" => LibraryPicture(),
                "Notes" => NotesPicture(),
                _ => new TextBlock { Text = "(그림 없음)" },
            };
        }
        catch (Exception ex)
        {
            UiLog.Write($"tutorial: picture {pic.Kind} failed {ex.Message}");
            content = new TextBlock { Text = "(그림을 그리지 못했습니다)", Foreground = SystemColors.GrayTextBrush };
        }
        content.IsHitTestVisible = false;   // 그림일 뿐 누를 수 없음
        var frame = new Border
        {
            Margin = new Thickness(0, pic.Caption == null ? 10 : 2, 0, 0), Padding = new Thickness(12), Background = new SolidColorBrush(Color.FromRgb(0xF4, 0xF4, 0xF4)),
            BorderBrush = SystemColors.ActiveBorderBrush, BorderThickness = new Thickness(1), HorizontalAlignment = HorizontalAlignment.Left,
            // 쪽보다 넓은 그림(하위 메뉴가 여럿 펼쳐진 메뉴 등)은 잘리지 않게 비율대로 줄임
            Child = new Viewbox { Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, Child = content, HorizontalAlignment = HorizontalAlignment.Left },
        };
        if (pic.Caption == null) return frame;
        return new StackPanel
        {
            Margin = new Thickness(0, 10, 0, 0),
            Children = { new TextBlock { Text = pic.Caption, FontWeight = FontWeights.SemiBold, Foreground = SystemColors.GrayTextBrush }, frame },
        };
    }

    /// <summary>그림에 그릴 메뉴 한 줄 (실제 메뉴 항목에서 만들되, State가 있으면 그 상태의 멀티 메뉴로)</summary>
    sealed record MenuRow(string Header, string Gesture, bool Checked, bool Enabled, ItemsControl? Sub, bool Separator = false);

    static List<MenuRow> MenuRows(ItemsControl level, string? state)
    {
        var rows = new List<MenuRow>();
        foreach (var obj in level.Items)
        {
            var fe = obj as FrameworkElement;
            bool? rule = state == null ? null : MultiMenuRules.Visible(fe?.Name, state);
            if (rule == false || (rule == null && fe?.Visibility == Visibility.Collapsed)) continue;
            if (obj is Separator) { rows.Add(new MenuRow("", "", false, true, null, Separator: true)); continue; }
            if (obj is not MenuItem mi) continue;
            string header = Clean(mi.Header);
            bool isChecked = mi.IsChecked, enabled = mi.IsEnabled;
            ItemsControl? sub = mi.Items.OfType<MenuItem>().Any() ? mi : null;
            if (state != null)
            {
                // 방에 있을 때 내용이 바뀌는 항목은 예시로
                switch (mi.Name)
                {
                    case "MultiRoomHeader": header = $"{MultiMenuRules.SampleRoomTitle} / 방 인원 2명"; break;
                    case "MultiCodeItem": header = $"방 코드 {MultiMenuRules.SampleRoomCode} 복사"; break;
                    case "MultiMembersMenu": header = "참가자 (2/4)"; sub = SampleMembers(state == MultiMenuRules.Host); break;
                    case "MultiControlItem": isChecked = false; break;
                }
                if (rule == true && mi.Name != "MultiRoomHeader") enabled = true;
            }
            rows.Add(new MenuRow(header, sub != null ? "▶" : mi.InputGestureText, isChecked, enabled, sub));
        }
        return rows;
    }

    /// <summary>예시 참가자 목록: 방장 메뉴면 '친구'에게 관리 메뉴가 달림</summary>
    static MenuItem SampleMembers(bool host)
    {
        var list = new MenuItem();
        list.Items.Add(new MenuItem { Header = host ? "나 (방장) (나)" : "방장 (방장)", IsEnabled = false });
        var friend = new MenuItem { Header = host ? MultiMenuRules.SampleFriend : MultiMenuRules.SampleFriend + " (나)", IsEnabled = host };
        if (host)
        {
            friend.Items.Add(new MenuItem { Header = "채팅 금지" });
            friend.Items.Add(new MenuItem { Header = "방장 넘기기..." });
            friend.Items.Add(new Separator());
            friend.Items.Add(new MenuItem { Header = "내보내기..." });
        }
        list.Items.Add(friend);
        return list;
    }

    /// <summary>메뉴 줄에서 경로를 차례로 연 모습: [탭] → [항목] → [하위 항목], 고르는 곳에 번호</summary>
    FrameworkElement MenuPicture(string[] path, string? state)
    {
        var tops = _menu.Items.OfType<MenuItem>().ToList();
        var top = tops.FirstOrDefault(m => Clean(m.Header).StartsWith(path[0], StringComparison.Ordinal));
        var root = new Grid();
        var col = new StackPanel();
        // 메뉴 줄
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Background = SystemColors.MenuBarBrush };
        int n = 1;
        FrameworkElement? topCell = null;
        foreach (var t in tops)
        {
            bool hit = ReferenceEquals(t, top);
            var cell = new Border { Padding = new Thickness(8, 3, 8, 3), Background = hit ? Highlight : Brushes.Transparent, Child = new TextBlock { Text = Clean(t.Header) } };
            var el = hit ? Numbered(cell, n++) : cell;
            if (hit) topCell = el;
            bar.Children.Add(el);
        }
        col.Children.Add(new Border { BorderBrush = MenuBorder, BorderThickness = new Thickness(1), Child = bar, HorizontalAlignment = HorizontalAlignment.Left });
        // 펼친 메뉴들 (나란히)
        var drops = new StackPanel { Orientation = Orientation.Horizontal };
        var boxes = new List<(Border box, FrameworkElement? hitRow)>();
        ItemsControl? level = top;
        for (int depth = 1; level != null && depth <= Math.Max(1, path.Length - 1); depth++)
        {
            string? want = depth < path.Length ? path[depth] : null;
            var panel = new StackPanel { MinWidth = depth == 1 ? 210 : 140 };
            ItemsControl? next = null;
            FrameworkElement? hitRow = null;
            bool found = false;
            foreach (var mr in MenuRows(level, state))
            {
                if (mr.Separator) { panel.Children.Add(new Rectangle { Height = 1, Fill = MenuBorder, Margin = new Thickness(26, 3, 4, 3) }); continue; }
                bool hit = want != null && !found && mr.Header.StartsWith(want, StringComparison.Ordinal);
                if (hit) { found = true; next = mr.Sub; }
                var row = new DockPanel { Background = hit ? Highlight : Brushes.Transparent };
                var g = new TextBlock { Text = mr.Gesture, Foreground = SystemColors.GrayTextBrush, Margin = new Thickness(24, 0, 6, 0), FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
                DockPanel.SetDock(g, Dock.Right);
                row.Children.Add(g);
                row.Children.Add(new TextBlock { Text = (mr.Checked ? "✓ " : "   ") + mr.Header, Padding = new Thickness(6, 3, 0, 3), Foreground = mr.Enabled ? Brushes.Black : Brushes.Gray });
                var el = hit ? Numbered(row, n++) : row;
                if (hit) hitRow = el;
                panel.Children.Add(el);
            }
            if (want != null && !found) UiLog.Write($"tutorial: menu path not found '{string.Join(" > ", path)}' at '{want}' (state {state ?? "now"})");
            var box = new Border
            {
                Background = Brushes.White, BorderBrush = MenuBorder, BorderThickness = new Thickness(1), Child = panel, VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(depth == 1 ? 0 : -2, 0, 0, 0),
                Effect = new DropShadowEffect { BlurRadius = 6, ShadowDepth = 2, Opacity = 0.25 },
            };
            // 앞쪽 메뉴를 위에: 고른 줄의 번호(오른쪽 위로 삐져나옴)가 옆 하위 메뉴에 가려지지 않게
            Panel.SetZIndex(box, 100 - depth);
            drops.Children.Add(box);
            boxes.Add((box, hitRow));
            level = next;
        }
        col.Children.Add(drops);
        root.Children.Add(col);
        // 실제 메뉴처럼: 첫 메뉴는 고른 탭 아래, 하위 메뉴는 고른 줄 옆에 (그려진 뒤 위치를 재서 맞춤)
        root.Loaded += (_, _) => root.Dispatcher.BeginInvoke(() =>
        {
            try
            {
                if (topCell != null) drops.Margin = new Thickness(Math.Max(0, topCell.TranslatePoint(new Point(0, 0), bar).X), 0, 0, 0);
                double y = 0;
                for (int i = 1; i < boxes.Count; i++)
                {
                    var prev = boxes[i - 1];
                    if (prev.hitRow != null) y += prev.hitRow.TranslatePoint(new Point(0, 0), prev.box).Y;
                    boxes[i].box.Margin = new Thickness(-2, y, 0, 0);
                }
            }
            catch (InvalidOperationException) { }
        }, System.Windows.Threading.DispatcherPriority.Loaded);
        return root;
    }

    /// <summary>실제 창의 내용을 그대로 (제목 줄은 그려 넣음), 지정한 글자가 있는 칸·단추에 번호</summary>
    FrameworkElement WindowPicture(string kind, string[] marks)
    {
        Window? w = kind switch
        {
            "rtp" => new RtpFolderWindow(_main, RtpResolver.UserPaths),
            "create-room" => new MultiCreateWindow(_main, "내 이름의 방"),
            "join-room" => new MultiJoinWindow(_main),
            "chat" => new ChatInputWindow(_main, () => 0),
            "hotkeys" => new HotkeySettingsWindow(),
            _ => null,
        };
        string title;
        UIElement inner;
        double width;
        if (w == null && kind == "name")
        {
            title = "이름 설정하기";
            width = 320;
            var box = new TextBox { Text = "멋진이름", Margin = new Thickness(0, 4, 0, 10), Padding = new Thickness(3, 2, 3, 2) };
            var ok = new Button { Content = "확인", Width = 75, Margin = new Thickness(0, 0, 6, 0) };
            inner = new StackPanel
            {
                Margin = new Thickness(12),
                Children = { new TextBlock { Text = "방에서 보일 내 이름 (다른 사람과 겹쳐도 됩니다):" }, box,
                    new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { ok, new Button { Content = "취소", Width = 75 } } } },
            };
        }
        else if (w != null)
        {
            title = w.Title;
            width = double.IsNaN(w.Width) ? 520 : Math.Min(w.Width, 680);
            inner = (UIElement)w.Content;
            w.Content = null;
        }
        else return new TextBlock { Text = "(그림 없음)" };

        var titleBar = new DockPanel { Background = Brushes.White, Height = 28 };
        var close = new TextBlock { Text = "✕", Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.DimGray };
        DockPanel.SetDock(close, Dock.Right);
        titleBar.Children.Add(close);
        titleBar.Children.Add(new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) });
        var frame = new DockPanel { Width = width, Background = SystemColors.ControlBrush };
        DockPanel.SetDock(titleBar, Dock.Top);
        frame.Children.Add(titleBar);
        frame.Children.Add(inner);
        var shell = new Border { BorderBrush = MenuBorder, BorderThickness = new Thickness(1), Child = frame, Effect = new DropShadowEffect { BlurRadius = 8, ShadowDepth = 2, Opacity = 0.25 } };
        return WithMarks(shell, frame, marks);
    }

    /// <summary>그림 위에 표시할 칸을 찾아 빨간 테두리와 번호를 겹쳐 그림 (그림이 화면에 놓인 뒤 위치를 계산)</summary>
    static FrameworkElement WithMarks(FrameworkElement picture, FrameworkElement searchRoot, string[] marks)
    {
        var grid = new Grid { HorizontalAlignment = HorizontalAlignment.Left };
        var layer = new Canvas { IsHitTestVisible = false };
        grid.Children.Add(picture);
        grid.Children.Add(layer);
        void Place()
        {
            layer.Children.Clear();
            int n = 1;
            foreach (var mark in marks)
            {
                var target = FindByText(searchRoot, mark);
                if (target == null || !target.IsVisible) continue;
                Rect r;
                try { r = target.TransformToAncestor(grid).TransformBounds(new Rect(0, 0, target.ActualWidth, target.ActualHeight)); }
                catch (InvalidOperationException) { continue; }
                var box = new Rectangle { Width = r.Width + 6, Height = r.Height + 6, Stroke = Red, StrokeThickness = 2, RadiusX = 3, RadiusY = 3 };
                Canvas.SetLeft(box, r.X - 3);
                Canvas.SetTop(box, r.Y - 3);
                var badge = Badge(n++);
                Canvas.SetLeft(badge, r.X - 12);
                Canvas.SetTop(badge, r.Y - 12);
                layer.Children.Add(box);
                layer.Children.Add(badge);
            }
        }
        grid.Loaded += (_, _) => grid.Dispatcher.BeginInvoke(Place, System.Windows.Threading.DispatcherPriority.Loaded);
        return grid;
    }

    /// <summary>글자로 칸·단추 찾기 (단추 글자, 글 줄, 체크 상자, 탭 등)</summary>
    static FrameworkElement? FindByText(DependencyObject root, string text)
    {
        foreach (var d in Descendants(root))
        {
            string? t = d switch
            {
                TextBlock tb => tb.Text,
                ContentControl { Content: string s } => s,
                HeaderedContentControl { Header: string h } => h,
                _ => null,
            };
            if (t == null || !t.Contains(text, StringComparison.Ordinal) || d is not FrameworkElement fe) continue;
            // 단추 안의 글자를 찾았으면 단추 전체를 표시
            for (DependencyObject? p = fe; p != null && !ReferenceEquals(p, root); p = VisualTreeHelper.GetParent(p))
                if (p is ButtonBase or TextBoxBase or ComboBox or PasswordBox) return (FrameworkElement)p;
            // 칸 이름(라벨)이면 그 옆 입력 칸까지는 찾지 않고 라벨만
            return fe;
        }
        return null;
    }

    static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        var stack = new Stack<DependencyObject>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var d = stack.Pop();
            yield return d;
            int n = d is Visual || d is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetChildrenCount(d) : 0;
            for (int i = n - 1; i >= 0; i--) stack.Push(VisualTreeHelper.GetChild(d, i));
        }
    }

    /// <summary>예시 게임 화면 (직접 그린 것: 하늘·땅·대화창)</summary>
    static Grid GameMock(double w, double h, bool message = true)
    {
        var g = new Grid { Width = w, Height = h, ClipToBounds = true };
        g.Children.Add(new Rectangle { Fill = new LinearGradientBrush(Color.FromRgb(0x3B, 0x6E, 0xA8), Color.FromRgb(0x9C, 0xC9, 0xE8), 90) });
        g.Children.Add(new Rectangle { Fill = new SolidColorBrush(Color.FromRgb(0x5E, 0x9E, 0x4A)), Height = h * 0.38, VerticalAlignment = VerticalAlignment.Bottom });
        g.Children.Add(new Ellipse { Width = 26, Height = 34, Fill = new SolidColorBrush(Color.FromRgb(0xF2, 0xC2, 0x8C)), Stroke = Brushes.SaddleBrown, StrokeThickness = 2,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, h * 0.30) });
        if (message)
            g.Children.Add(new Border
            {
                VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(10, 0, 10, 10), Height = h * 0.26, Background = new SolidColorBrush(Color.FromArgb(0xD0, 0x10, 0x18, 0x40)),
                BorderBrush = Brushes.White, BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(4), Padding = new Thickness(10, 6, 10, 6),
                Child = new TextBlock { Text = "예시 게임: 안녕하세요! 모험을 떠나 볼까요?", Foreground = Brushes.White, FontSize = 13 },
            });
        g.Children.Add(new TextBlock { Text = "예시 화면", Foreground = new SolidColorBrush(Color.FromArgb(0xA0, 0xFF, 0xFF, 0xFF)), FontSize = 10, Margin = new Thickness(6, 4, 0, 0) });
        return g;
    }

    FrameworkElement BarPicture()
    {
        var root = new StackPanel { Background = Brushes.Black };
        root.Children.Add(GameMock(560, 300));
        var bar = new RenpyMessageBar { Height = 30 };
        bar.SetQuickSaveVisible(true);
        root.Children.Add(bar);
        return WithMarks(root, bar, ["기록", "스킵", "자동", "속도"]);
    }

    static FrameworkElement VotePicture()
    {
        var g = GameMock(560, 300, message: false);
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock { Text = "선택지 투표 · 눌러서 투표 (바꿀 수 있음)", FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 6) });
        string[] items = ["1. 동굴로 들어간다", "2. 마을로 돌아간다", "3. 잠시 쉰다"];
        int[] counts = [2, 1, 0];
        uint[][] colors = [[0xFFD34D, 0x65BBFF], [0xFF7373], []];
        for (int i = 0; i < items.Length; i++)
        {
            var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
            var count = new TextBlock { Text = $"{counts[i]}표", Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            var dots = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            foreach (uint c in colors[i]) dots.Children.Add(new Ellipse { Width = 9, Height = 9, Margin = new Thickness(1, 0, 1, 0), Fill = new SolidColorBrush(Color.FromRgb((byte)(c >> 16), (byte)(c >> 8), (byte)c)), Stroke = Brushes.DimGray });
            DockPanel.SetDock(count, Dock.Right);
            DockPanel.SetDock(dots, Dock.Right);
            row.Children.Add(count);
            row.Children.Add(dots);
            row.Children.Add(new Button { Content = items[i], HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(6, 2, 6, 2), FontWeight = i == 0 ? FontWeights.Bold : FontWeights.Normal,
                Background = i == 0 ? new SolidColorBrush(Color.FromRgb(0xCC, 0xE4, 0xF7)) : SystemColors.ControlBrush });
            stack.Children.Add(row);
        }
        g.Children.Add(new Border
        {
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0), Padding = new Thickness(10, 8, 10, 8),
            Background = new SolidColorBrush(Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF)), BorderBrush = new SolidColorBrush(Color.FromRgb(0x70, 0x70, 0x70)), BorderThickness = new Thickness(1), Width = 270, Child = stack,
        });
        return WithMarks(g, stack, ["1. 동굴로", "2표"]);
    }

    static FrameworkElement ChatPicture()
    {
        var g = GameMock(560, 300);
        (string text, uint color, double top, double left)[] lines =
            [("여기 숨은 길 있어요!", 0xFF3838, 70, 260), ("ㅋㅋㅋ 귀엽다", 0xFFFFFF, 100, 120), ("저 상자 열어 봐요", 0xFFF2A6, 130, 330)];
        foreach (var (text, color, top, left) in lines)
            g.Children.Add(Outlined(text, color, new Thickness(left, top, 0, 0)));
        var fixedLine = Outlined("[고정] 보스 전에 저장하기!", 0xB8E5FF, new Thickness(0, 30, 0, 0));
        fixedLine.HorizontalAlignment = HorizontalAlignment.Center;
        g.Children.Add(fixedLine);
        return g;
    }

    static FrameworkElement PingPicture()
    {
        var g = GameMock(560, 300, message: false);
        (double x, double y, uint c, string who)[] pings = [(180, 150, 0xFFD34D, "나"), (380, 120, 0x65BBFF, "친구")];
        foreach (var (x, y, c, who) in pings)
        {
            var brush = new SolidColorBrush(Color.FromRgb((byte)(c >> 16), (byte)(c >> 8), (byte)c));
            var ring = new Ellipse { Width = 34, Height = 34, Stroke = brush, StrokeThickness = 3, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(x - 17, y - 17, 0, 0) };
            var dot = new Ellipse { Width = 12, Height = 12, Fill = brush, Stroke = Brushes.Black, StrokeThickness = 1, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(x - 6, y - 6, 0, 0) };
            g.Children.Add(ring);
            g.Children.Add(dot);
            g.Children.Add(Outlined(who, c, new Thickness(x + 18, y - 26, 0, 0), 12));
        }
        g.Children.Add(new TextBlock { Text = "🖱 가운데 단추(휠)", FontSize = 13, Foreground = Brushes.White, Background = new SolidColorBrush(Color.FromArgb(0x90, 0, 0, 0)), Padding = new Thickness(6, 2, 6, 2),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(8, 0, 0, 8) });
        return g;
    }

    static FrameworkElement LibraryPicture()
    {
        var root = new StackPanel { Width = 560, Background = SystemColors.ControlBrush };
        var top = new DockPanel { Margin = new Thickness(8) };
        var add = new Button { Content = "폴더 추가", Padding = new Thickness(8, 1, 8, 1) };
        DockPanel.SetDock(add, Dock.Right);
        top.Children.Add(add);
        top.Children.Add(new TextBox { Text = "찾기", Foreground = Brushes.Gray, Margin = new Thickness(0, 0, 8, 0) });
        root.Children.Add(top);
        var tiles = new WrapPanel { Margin = new Thickness(8, 0, 8, 8) };
        string[] names = ["내 게임 1 (2000)", "내 게임 2 (XP)", "내 게임 3 (MV)", "내 게임 4 (VX Ace)"];
        foreach (var n in names)
            tiles.Children.Add(new Border
            {
                Width = 125, Height = 95, Margin = new Thickness(0, 0, 8, 8), Background = Brushes.White, BorderBrush = MenuBorder, BorderThickness = new Thickness(1),
                Child = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = {
                    new Rectangle { Width = 60, Height = 40, Fill = new LinearGradientBrush(Color.FromRgb(0x3B, 0x6E, 0xA8), Color.FromRgb(0x5E, 0x9E, 0x4A), 90) },
                    new TextBlock { Text = n, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0), FontSize = 11 } } },
            });
        root.Children.Add(tiles);
        return WithMarks(root, root, ["폴더 추가", "내 게임 1"]);
    }

    static FrameworkElement NotesPicture()
    {
        var root = new DockPanel { Width = 380, Height = 300, Background = Brushes.White };
        var tools = new StackPanel { Orientation = Orientation.Horizontal, Background = SystemColors.ControlBrush };
        foreach (var t in new[] { "메모", "글상자", "이미지", "스크린샷", "전체 보기" })
            tools.Children.Add(new Button { Content = t, Padding = new Thickness(6, 1, 6, 1), Margin = new Thickness(2) });
        DockPanel.SetDock(tools, Dock.Top);
        root.Children.Add(tools);
        var canvas = new Canvas { Background = new SolidColorBrush(Color.FromRgb(0xFA, 0xFA, 0xF6)) };
        var memo = new Border { Width = 150, Height = 80, Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xF2, 0xA6)), BorderBrush = new SolidColorBrush(Color.FromRgb(0xD8, 0xC0, 0x50)), BorderThickness = new Thickness(1), Padding = new Thickness(6),
            Child = new TextBlock { Text = "보물 상자: 2층 왼쪽\n비밀번호 = 1234", TextWrapping = TextWrapping.Wrap } };
        Canvas.SetLeft(memo, 30); Canvas.SetTop(memo, 30);
        var shot = new Border { Width = 140, Height = 90, BorderBrush = MenuBorder, BorderThickness = new Thickness(1), Child = GameMock(140, 90, message: false) };
        Canvas.SetLeft(shot, 200); Canvas.SetTop(shot, 120);
        canvas.Children.Add(memo);
        canvas.Children.Add(shot);
        root.Children.Add(canvas);
        return WithMarks(root, root, ["메모", "스크린샷"]);
    }

    // ── 작은 도우미 ──

    static TextBlock Outlined(string text, uint color, Thickness margin, double size = 15) => new()
    {
        Text = text, FontSize = size, FontWeight = FontWeights.Bold, Margin = margin, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
        Foreground = new SolidColorBrush(Color.FromRgb((byte)(color >> 16), (byte)(color >> 8), (byte)color)),
        Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 3, ShadowDepth = 0, Opacity = 1 },
    };

    static FrameworkElement Numbered(FrameworkElement e, int n)
    {
        var g = new Grid();
        g.Children.Add(new Border { BorderBrush = Red, BorderThickness = new Thickness(2), Child = e });
        var b = Badge(n);
        b.HorizontalAlignment = HorizontalAlignment.Right;
        b.VerticalAlignment = VerticalAlignment.Top;
        b.Margin = new Thickness(0, -9, -9, 0);
        g.Children.Add(b);
        return g;
    }

    static Border Badge(int n) => new()
    {
        Width = 20, Height = 20, CornerRadius = new CornerRadius(10), Background = Red,
        Child = new TextBlock { Text = n.ToString(), Foreground = Brushes.White, FontWeight = FontWeights.Bold, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
    };

    static TextBlock Para(string text, double size, Thickness? margin = null) =>
        new() { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, Margin = margin ?? new Thickness(0, 0, 0, 4) };

    /// <summary>메뉴 글자에서 단축 글자 표시를 뺌 ("도구(_T)" → "도구", "노트(_O)" → "노트")</summary>
    public static string Clean(object? header)
    {
        string s = header as string ?? "";
        s = Regex.Replace(s, @"\(_[A-Za-z]\)", "");
        s = s.Replace("__", "\u0001").Replace("_", "").Replace("\u0001", "_");
        return s.Replace("...", "").Trim();
    }

    static Brush Freeze(Brush b) { b.Freeze(); return b; }
}
