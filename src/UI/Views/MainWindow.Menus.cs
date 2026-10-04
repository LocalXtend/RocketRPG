#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Windows.Interop;
using Microsoft.Win32;
using RocketRPG.Models;

namespace RocketRPG.Views;

public partial class MainWindow
{
    public void ApplyUiFont(string fontName)
    {
        if (string.IsNullOrWhiteSpace(fontName)) return;
        try
        {
            var ff = new FontFamily(fontName);
            this.FontFamily = ff;
            _overlay.FontFamily = ff;
            foreach (Window win in OwnedWindows)
            {
                win.FontFamily = ff;
            }
        }
        catch (Exception ex)
        {
            UiLog.Write($"ApplyUiFont error: {ex.Message}");
        }
    }

    public static void KillProcessTree(int pid)
    {
        if (pid <= 0) return;
        bool exited = false;
        try
        {
            var proc = Process.GetProcessById(pid);
            if (!proc.HasExited)
            {
                proc.Kill(entireProcessTree: true);
                exited = proc.WaitForExit(1000);
            }
            else
            {
                exited = true;
            }
        }
        catch { }

        if (!exited)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "taskkill",
                    Arguments = $"/F /T /PID {pid}",
                    CreateNoWindow = true,
                    UseShellExecute = false
                };
                using var p = Process.Start(psi);
                p?.WaitForExit(1000);
            }
            catch { }
        }
    }

    void ShowResult(string err, string what)
    {
        if (err.Length > 0) MessageBox.Show(this, err, what, MessageBoxButton.OK, MessageBoxImage.Warning);
        else SetStatus($"{what} 실행됨");
    }

    void CheckItem(string tag, object? value)
    {
        if (tag == "filter" && (string.Equals(value?.ToString(), "pixellate", StringComparison.OrdinalIgnoreCase) || string.Equals(value?.ToString(), "cas", StringComparison.OrdinalIgnoreCase)))
        {
            value = "quality";
        }

        MenuItem? target = tag switch
        {
            "ratio" => RatioMenu,
            "gamma" => GammaMenu,
            "filter" => FilterMenu,
            _ => null
        };
        if (target != null)
            MarkChildren(target, tag, value);
    }

    static void MarkChildren(MenuItem parent, string tag, object? value)
    {
        foreach (var i in parent.Items.OfType<MenuItem>())
        {
            if (i.Tag != null)
            {
                if (tag == "gamma" &&
                    double.TryParse(i.Tag.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double tagG) &&
                    double.TryParse(value?.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double curG))
                {
                    i.IsChecked = Math.Abs(tagG - curG) < 0.01;
                }
                else
                {
                    i.IsChecked = string.Equals(i.Tag.ToString(), value?.ToString(), StringComparison.OrdinalIgnoreCase);
                }
            }
            else MarkChildren(i, tag, value);
        }
    }

    void OnFilter(object s, RoutedEventArgs e)
    {
        var mi = (MenuItem)s;
        string filter = mi.Tag?.ToString() ?? "none";
        string before = _ctl.Settings.Filter ?? RocketShaderSystem.FilterNone;
        _ctl.Settings.Filter = filter;
        CheckItem("filter", filter);
        SavePerGame();
        _currentBridge?.SetFilter(filter);
        ShowHudMessage($"필터: {RocketShaderSystem.GetFilterDisplayName(filter)}");
        // XP/VX/Ace는 mkxp-z가 시작할 때 확대 방식을 정하므로 바꾸려면 다시 시작해야 합니다.
        if (_currentBridge == _mkxpRenderer && !string.Equals(before, filter, StringComparison.OrdinalIgnoreCase))
            AskRestartToApply("필터");
    }

    /// <summary>
    /// 실행 중인 엔진에서 실제로 쓸 수 있는 필터만 켭니다. XP/VX/Ace는 mkxp-z 내장 xBRZ, 2000/2003은 CRT 주사선만
    /// 지원하고, MV/MZ는 모든 필터를 씁니다. 저장된 필터를 쓸 수 없으면 원본으로 되돌립니다.
    /// </summary>
    void UpdateFilterAvailability()
    {
        string[]? allowed = _isNativeRunning && _currentBridge == _mkxpRenderer ? [RocketShaderSystem.FilterNone, RocketShaderSystem.FilterXbrzCas]
                          : _isNativeRunning && _currentBridge == _easyRpgRenderer ? [RocketShaderSystem.FilterNone, RocketShaderSystem.FilterCrtRoyale]
                          : null;
        string tip = _currentBridge == _mkxpRenderer ? "XP/VX/Ace(mkxp-z)에서는 쓸 수 없는 필터입니다."
                   : "2000/2003(EasyRPG)에서는 쓸 수 없는 필터입니다.";
        foreach (var mi in FilterMenu.Items.OfType<MenuItem>())
        {
            bool ok = allowed == null || allowed.Contains(mi.Tag?.ToString() ?? "", StringComparer.OrdinalIgnoreCase);
            mi.IsEnabled = ok;
            mi.ToolTip = ok ? null : tip;
            ToolTipService.SetShowOnDisabled(mi, true);
        }
        string cur = _ctl.Settings.Filter ?? RocketShaderSystem.FilterNone;
        if (allowed != null && !allowed.Contains(cur, StringComparer.OrdinalIgnoreCase))
        {
            _ctl.Settings.Filter = RocketShaderSystem.FilterNone;
            CheckItem("filter", RocketShaderSystem.FilterNone);
            _currentBridge?.SetFilter(RocketShaderSystem.FilterNone);
            SavePerGame();
        }
    }

    /// <summary>게임을 다시 시작해야 적용되는 설정: 지금 다시 시작할지 묻습니다.</summary>
    void AskRestartToApply(string what)
    {
        if (string.IsNullOrEmpty(_currentDir)) return;
        var r = MessageBox.Show(this, $"{what}은(는) 게임을 다시 시작하면 적용됩니다.\n지금 게임을 다시 시작할까요? (저장하지 않은 진행은 사라집니다)",
            "RocketRPG", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (r == MessageBoxResult.Yes) RelaunchCurrentGame();
        else ShowHudMessage($"{what}: 다음에 게임을 시작할 때 적용됩니다.");
    }

    /// <summary>현재 게임을 새 설정으로 다시 실행합니다 (프로세스 재시작).</summary>
    void RelaunchCurrentGame()
    {
        string dir = _currentDir;
        if (string.IsNullOrEmpty(dir)) return;
        Dispatcher.BeginInvoke(() => Launch(dir), DispatcherPriority.Background);
    }
}
