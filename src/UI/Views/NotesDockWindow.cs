#nullable enable
using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace RocketRPG.Views;

/// <summary>
/// 오른쪽에 붙인 노트: RocketRPG 창의 오른쪽 가장자리 바깥에 딱 붙어 함께 움직이는 테두리 없는 창.
/// 다른 프로세스의 게임 창(EasyRPG/mkxp-z)이 들어 있는 RocketRPG 창 위(안)에 그리는 창은 그릴 때마다 게임 화면 출력과
/// 맞물려 한 번에 40ms 넘게 걸려, 노트 이동·확대·입력이 크게 버벅였습니다. 창 밖 옆에 두면 그렇지 않습니다.
/// RocketRPG 창의 보이지 않는 크기 조절 테두리(윈도우 11에서 약 7px)와 겹쳐도 똑같이 느려지므로, 창 사각형의 바로 오른쪽에 둡니다.
/// 오른쪽 끝을 끌어 너비를 바꿉니다.
/// </summary>
internal sealed class NotesDockWindow : Window
{
    public const double MinPanelWidth = 220, MaxPanelWidth = 900;

    readonly Window _owner;
    readonly Grid _root = new();
    IntPtr _ownerHwnd;
    bool _wanted;

    /// <summary>노트 너비 (WPF 단위)</summary>
    public double PanelWidth { get; set; } = 380;

    /// <summary>끌어서 너비를 바꾼 뒤 (저장용)</summary>
    public event Action? PanelWidthCommitted;

    const int WM_NCACTIVATE = 0x0086, DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
    [DllImport("user32.dll")] static extern IntPtr SendMessageW(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT r, int size);

    public NotesDockWindow(Window owner)
    {
        _owner = owner;
        Owner = owner;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;   // 노트를 켜도 게임 입력(포커스)을 빼앗지 않음
        Background = SystemColors.ControlBrush;
        BorderBrush = SystemColors.ActiveBorderBrush;
        BorderThickness = new Thickness(0, 1, 1, 1);
        FontFamily = owner.FontFamily;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Title = "노트";

        _root.ColumnDefinitions.Add(new ColumnDefinition());
        _root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(5) });
        var grip = new Thumb { Cursor = Cursors.SizeWE, Background = new SolidColorBrush(Color.FromRgb(0xD4, 0xD4, 0xD4)) };
        grip.Template = new ControlTemplate(typeof(Thumb)) { VisualTree = MakeGripVisual() };
        Grid.SetColumn(grip, 1);
        grip.DragDelta += (_, e) =>
        {
            PanelWidth = Math.Clamp(PanelWidth + e.HorizontalChange, MinPanelWidth, Math.Min(MaxPanelWidth, RoomRight()));
            Sync();
        };
        grip.DragCompleted += (_, _) => PanelWidthCommitted?.Invoke();
        _root.Children.Add(grip);
        Content = _root;

        _owner.LocationChanged += (_, _) => Sync();
        _owner.SizeChanged += (_, _) => Sync();
        _owner.StateChanged += (_, _) => Sync();

        // 노트에서 글을 쓰는 동안에도 RocketRPG 창 제목 표시줄이 비활성(흐림)으로 바뀌지 않게 합니다.
        Activated += (_, _) => { if (_ownerHwnd != IntPtr.Zero) SendMessageW(_ownerHwnd, WM_NCACTIVATE, (IntPtr)1, IntPtr.Zero); };
        Deactivated += (_, _) =>
        {
            if (_ownerHwnd != IntPtr.Zero && GetForegroundWindow() != _ownerHwnd)
                SendMessageW(_ownerHwnd, WM_NCACTIVATE, IntPtr.Zero, IntPtr.Zero);
        };
    }

    static FrameworkElementFactory MakeGripVisual()
    {
        var f = new FrameworkElementFactory(typeof(Border));
        f.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        return f;
    }

    /// <summary>노트 패널을 담습니다 (null이면 비움).</summary>
    public UIElement? Panel
    {
        get => _root.Children.Count > 1 ? _root.Children[1] : null;
        set
        {
            if (_root.Children.Count > 1) _root.Children.RemoveAt(1);
            if (value != null) { Grid.SetColumn(value, 0); _root.Children.Add(value); }
        }
    }

    /// <summary>보여 줄지 (노트를 붙여서 켠 상태)</summary>
    public bool Wanted
    {
        get => _wanted;
        set { _wanted = value; Sync(); }
    }

    DpiScale Dpi => VisualTreeHelper.GetDpi(_owner);

    /// <summary>RocketRPG 창의 보이는 테두리 (그림자·보이지 않는 크기 조절 테두리 제외, 장치 픽셀)</summary>
    public Rect OwnerFrame()
    {
        if (_ownerHwnd == IntPtr.Zero) _ownerHwnd = new WindowInteropHelper(_owner).Handle;
        if (_ownerHwnd == IntPtr.Zero) return Rect.Empty;
        if (DwmGetWindowAttribute(_ownerHwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out var r, Marshal.SizeOf<RECT>()) != 0 &&
            !GetWindowRect(_ownerHwnd, out r)) return Rect.Empty;
        return new Rect(r.L, r.T, r.R - r.L, r.B - r.T);
    }

    /// <summary>보이는 테두리와 창 크기(Left/Width)의 차이 (왼쪽, 위, 오른쪽, 아래, WPF 단위)</summary>
    public Thickness OwnerInvisibleBorder()
    {
        var f = OwnerFrame();
        if (f.IsEmpty || !GetWindowRect(_ownerHwnd, out var w)) return new Thickness(0);
        var d = Dpi;
        return new Thickness((f.Left - w.L) / d.DpiScaleX, (f.Top - w.T) / d.DpiScaleY, (w.R - f.Right) / d.DpiScaleX, (w.B - f.Bottom) / d.DpiScaleY);
    }

    /// <summary>RocketRPG 창 사각형의 오른쪽 끝 (보이지 않는 테두리 포함, 장치 픽셀). 노트는 여기부터 놓습니다.</summary>
    public double OwnerRight()
    {
        var f = OwnerFrame();
        return !f.IsEmpty && GetWindowRect(_ownerHwnd, out var w) ? w.R : f.Right;
    }

    /// <summary>RocketRPG 창 오른쪽에서 화면(작업 영역) 끝까지 (WPF 단위)</summary>
    double RoomRight()
    {
        var f = OwnerFrame();
        if (f.IsEmpty) return MaxPanelWidth;
        return Math.Max(MinPanelWidth, (WorkAreaOf(f).Right - OwnerRight()) / Dpi.DpiScaleX);
    }

    public static Rect WorkAreaOf(Rect deviceRect)
    {
        var p = new POINT { X = (int)(deviceRect.Left + deviceRect.Width / 2), Y = (int)(deviceRect.Top + deviceRect.Height / 2) };
        IntPtr mon = MonitorFromPoint(p, 2);
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (mon != IntPtr.Zero && GetMonitorInfo(mon, ref mi))
            return new Rect(mi.rcWork.L, mi.rcWork.T, mi.rcWork.R - mi.rcWork.L, mi.rcWork.B - mi.rcWork.T);
        return new Rect(0, 0, SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight);
    }

    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }
    [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
    [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr hMon, ref MONITORINFO mi);

    /// <summary>보여 줄지 정하고 RocketRPG 창 오른쪽에 붙입니다.</summary>
    public void Sync()
    {
        bool show = _wanted && Panel != null && _owner.IsVisible && _owner.WindowState != WindowState.Minimized;
        var f = show ? OwnerFrame() : Rect.Empty;
        if (f.IsEmpty)
        {
            if (IsVisible) Hide();
            return;
        }
        var d = Dpi;
        Left = OwnerRight() / d.DpiScaleX;
        Top = f.Top / d.DpiScaleY;
        Width = PanelWidth;
        Height = f.Height / d.DpiScaleY;
        if (!IsVisible) Show();
    }
}
