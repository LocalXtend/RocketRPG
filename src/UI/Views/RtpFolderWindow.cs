#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using RocketRPG.Models;

namespace RocketRPG.Views;

/// <summary>
/// 설정 &gt; RTP 폴더 지정: 엔진마다 RTP 폴더를 직접 고릅니다 (설치 기록이 없거나 다른 곳에 둔 경우).
/// 비워 두면 설치 기록(레지스트리)과 기본 설치 폴더에서 자동으로 찾습니다.
/// </summary>
internal sealed class RtpFolderWindow : Window
{
    static readonly (string Key, string Name)[] Engines =
        [("2000", "RPG Maker 2000"), ("2003", "RPG Maker 2003"), ("xp", "RPG Maker XP"), ("vx", "RPG Maker VX"), ("vxace", "RPG Maker VX Ace")];

    readonly Dictionary<string, TextBox> _boxes = new();
    readonly Dictionary<string, TextBlock> _status = new();
    public Dictionary<string, string> Result { get; } = new(StringComparer.OrdinalIgnoreCase);

    public RtpFolderWindow(Window owner, IReadOnlyDictionary<string, string> current)
    {
        Owner = owner;
        Title = "RTP 폴더 지정";
        Width = 620;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = SystemColors.ControlBrush;
        FontFamily = owner.FontFamily;

        var root = new StackPanel { Margin = new Thickness(12) };
        root.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10),
            Text = "RTP(기본 그림·소리 모음)가 있는 폴더를 고릅니다. 비워 두면 설치된 RTP를 자동으로 찾습니다.\n" +
                   "RTP는 게임마다 다르게 고를 필요 없이 엔진마다 한 번만 지정하면 됩니다.",
        });
        foreach (var (key, name) in Engines)
        {
            var box = new TextBox { IsReadOnly = true, Text = current.TryGetValue(key, out var p) ? p : "", Padding = new Thickness(2, 1, 2, 1) };
            var browse = new Button { Content = "찾아보기...", Padding = new Thickness(8, 1, 8, 1), Margin = new Thickness(6, 0, 0, 0) };
            var clear = new Button { Content = "지우기", Padding = new Thickness(8, 1, 8, 1), Margin = new Thickness(6, 0, 0, 0) };
            var status = new TextBlock { Foreground = SystemColors.GrayTextBrush, FontSize = 11, Margin = new Thickness(0, 2, 0, 0), TextWrapping = TextWrapping.Wrap };
            string k = key;
            browse.Click += (_, _) =>
            {
                var dlg = new OpenFolderDialog { Title = $"{name} RTP 폴더 고르기" };
                if (dlg.ShowDialog(this) != true) return;
                box.Text = dlg.FolderName;
                Refresh(k);
            };
            clear.Click += (_, _) => { box.Text = ""; Refresh(k); };
            var row = new DockPanel();
            DockPanel.SetDock(clear, Dock.Right);
            DockPanel.SetDock(browse, Dock.Right);
            row.Children.Add(clear);
            row.Children.Add(browse);
            row.Children.Add(box);
            var group = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
            group.Children.Add(new TextBlock { Text = name, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 2) });
            group.Children.Add(row);
            group.Children.Add(status);
            root.Children.Add(group);
            _boxes[key] = box;
            _status[key] = status;
            Refresh(key);
        }
        var ok = new Button { Content = "확인", Width = 75, IsDefault = true, Margin = new Thickness(0, 0, 6, 0) };
        var cancel = new Button { Content = "취소", Width = 75, IsCancel = true };
        ok.Click += (_, _) =>
        {
            foreach (var (key, box) in _boxes) if (box.Text.Trim().Length > 0) Result[key] = box.Text.Trim();
            DialogResult = true;
        };
        root.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 6, 0, 0), Children = { ok, cancel } });
        Content = root;
    }

    /// <summary>고른 폴더가 RTP처럼 보이는지, 비었으면 자동으로 찾은 곳을 보여 줌</summary>
    void Refresh(string key)
    {
        string path = _boxes[key].Text.Trim();
        bool is2k = key is "2000" or "2003";
        if (path.Length > 0)
        {
            bool ok = is2k ? RtpResolver.LooksLike2kRtp(path) : RtpResolver.LooksLikeRgssRtp(path);
            _status[key].Text = ok ? "RTP 폴더가 맞습니다." : is2k ? "이 폴더에 CharSet·ChipSet 같은 폴더가 없습니다. RTP 폴더가 맞는지 확인해 주세요."
                                                               : "이 폴더에 Graphics·Audio 폴더가 없습니다. RTP 폴더가 맞는지 확인해 주세요.";
            return;
        }
        string? auto;
        var saved = RtpResolver.UserPaths;
        try
        {
            RtpResolver.UserPaths = new(StringComparer.OrdinalIgnoreCase);
            auto = is2k ? RtpResolver.Resolve2k(key == "2003") : AutoRgss(key);
        }
        finally { RtpResolver.UserPaths = saved; }
        _status[key].Text = auto != null ? $"자동으로 찾음: {auto}" : "설치된 RTP를 찾지 못했습니다.";
    }

    /// <summary>XP/VX/Ace는 게임마다 RTP 이름이 있지만, 기본 RTP를 기준으로 자동 탐색 결과를 보여 줌</summary>
    static string? AutoRgss(string key)
    {
        string tmp = Path.Combine(Path.GetTempPath(), "rocketrpg_rtp_probe_" + key);
        try
        {
            Directory.CreateDirectory(tmp);
            string ini = key switch { "xp" => "[Game]\r\nRTP1=Standard\r\n", "vx" => "[Game]\r\nRTP=RPGVX\r\n", _ => "[Game]\r\nRTP=RPGVXAce\r\n" };
            File.WriteAllText(Path.Combine(tmp, "Game.ini"), ini);
            int engine = key switch { "xp" => CoreInterop.EngineXp, "vx" => CoreInterop.EngineVx, _ => CoreInterop.EngineAce };
            return RtpResolver.ResolveRgss(tmp, engine).FirstOrDefault();
        }
        catch { return null; }
        finally { try { Directory.Delete(tmp, true); } catch { } }
    }
}
