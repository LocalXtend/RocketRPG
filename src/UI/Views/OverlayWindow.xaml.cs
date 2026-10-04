#nullable enable
using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace RocketRPG.Views;

/// <summary>
/// 게임 표시 영역(target)을 따라다니는 투명 HUD 창.
/// 활성화되지 않으며(WS_EX_NOACTIVATE) 작업 표시줄/Alt+Tab에 나타나지 않습니다.
/// </summary>
public partial class OverlayWindow : Window
{
    readonly Window _owner;
    readonly FrameworkElement _target;
    IntPtr _hwnd;

    const int GWL_EXSTYLE = -20;
    const int WS_EX_NOACTIVATE = 0x08000000;
    const int WS_EX_TOOLWINDOW = 0x00000080;
    const uint SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010, SWP_NOOWNERZORDER = 0x0200;

    [DllImport("user32.dll")] static extern IntPtr GetWindowLongPtrW(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] static extern IntPtr SetWindowLongPtrW(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    public OverlayWindow(Window owner, FrameworkElement target)
    {
        InitializeComponent();
        _owner = owner;
        _target = target;

        _owner.LocationChanged += (_, _) => Sync();
        _owner.SizeChanged += (_, _) => Sync();
        _owner.StateChanged += (_, _) => Sync();
        _owner.IsVisibleChanged += (_, _) => Sync();
        _target.SizeChanged += (_, _) => Sync();
        _target.IsVisibleChanged += (_, _) => Sync();
        _target.LayoutUpdated += OnTargetLayoutUpdated;
    }

    /// <summary>소유 창이 표시된 뒤(Loaded) 호출합니다. 표시 전에는 Owner를 지정할 수 없습니다.</summary>
    public void Attach()
    {
        if (Owner == null) Owner = _owner;
        Sync();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        long ex = GetWindowLongPtrW(_hwnd, GWL_EXSTYLE).ToInt64();
        SetWindowLongPtrW(_hwnd, GWL_EXSTYLE, new IntPtr(ex | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW));
    }

    Rect _lastRect = Rect.Empty;

    // 메뉴바 토글 등으로 대상 위치만 바뀌는 경우를 잡기 위해 레이아웃 갱신 시 위치를 비교합니다(변경 시에만 이동).
    void OnTargetLayoutUpdated(object? sender, EventArgs e)
    {
        if (!IsVisible || _hwnd == IntPtr.Zero) return;
        var r = TargetScreenRect();
        if (r != _lastRect) Sync();
    }

    Rect TargetScreenRect()
    {
        if (PresentationSource.FromVisual(_target) == null) return Rect.Empty;
        var tl = _target.PointToScreen(new Point(0, 0));
        var dpi = VisualTreeHelper.GetDpi(_target);
        return new Rect(Math.Round(tl.X), Math.Round(tl.Y),
                        Math.Round(_target.ActualWidth * dpi.DpiScaleX), Math.Round(_target.ActualHeight * dpi.DpiScaleY));
    }

    public void Sync()
    {
        if (Owner == null) return;
        bool show = _owner.IsVisible && _owner.WindowState != WindowState.Minimized &&
                    _target.IsVisible && _target.ActualWidth > 1 && _target.ActualHeight > 1;
        if (!show)
        {
            if (IsVisible) Hide();
            return;
        }
        if (!IsVisible) Show();
        var r = TargetScreenRect();
        if (r.IsEmpty) return;
        _lastRect = r;
        // AllowsTransparency(계층) 창은 WPF가 자기 Width/Height로 표면을 다시 그리므로, SetWindowPos만 쓰면
        // 창 크기가 초기값(160x28)에 머물러 메시지 바/토스트가 보이지 않았습니다. WPF 속성(DIP)으로 맞춥니다.
        var dpi = VisualTreeHelper.GetDpi(this);
        double w = r.Width / dpi.DpiScaleX, h = r.Height / dpi.DpiScaleY;
        // 다른 프로그램이 창을 숨겼다 되살리는 등으로 실제 창이 초기 크기로 줄어 있으면, WPF 값이 이미 같아 다시 그리지 않으므로 한 번 흔들어 맞춤
        if (GetWindowRect(_hwnd, out var now) && (now.R - now.L != (int)r.Width || now.B - now.T != (int)r.Height) && Math.Abs(Width - w) < 0.5)
            Width = w + 1;
        Left = r.X / dpi.DpiScaleX;
        Top = r.Y / dpi.DpiScaleY;
        Width = w;
        Height = h;
        SetWindowPos(_hwnd, IntPtr.Zero, (int)r.X, (int)r.Y, (int)r.Width, (int)r.Height,
                     SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
    }
}
