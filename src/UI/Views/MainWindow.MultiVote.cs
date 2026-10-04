#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using RocketRPG.Models;

namespace RocketRPG.Views;

// ── 멀티 선택지 투표 ──
// 방장 게임에 선택지가 뜨면 방장이 투표를 엽니다. 참가자 화면 오른쪽에 누를 수 있는 투표 상자가 뜨고, 한 사람 한 표(바꿀 수 있음).
// 방장 화면에는 줄마다 득표 수와 투표한 사람의 색이 보이고, 실제로 고르는 것은 방장입니다 (자동으로 고르지 않음).
// 방장이 고르거나 선택지가 사라지면 바로 닫히고, 지난 선택지의 표는 다음 선택지에 쓰지 않습니다(선택지 번호 Gen).
// 투표는 메시지 바·도구와 달리 스트리머 모드·조종 권한과 상관없이 모두 할 수 있고, 마우스로만 하므로 게임 키로 가지 않습니다.
// 투표가 열려 있는 동안에는 조종 권한이 있어도 참가자의 키·메시지 바·도구 요청을 받지 않습니다 (방장이 고름).
public partial class MainWindow
{
    readonly ChoiceVote _vote = new();          // 방장: 지금 투표
    JsonObject? _guestVote;                     // 참가자: 방장이 보낸 투표 상태
    int _guestVotePick = -1;                    // 참가자: 내가 고른 줄 (보낸 것)
    Border? _votePanel;

    /// <summary>방장: 게임 선택지가 열림/닫힘 (게임 브리지에서)</summary>
    void OnGameChoice(IGameBridge source, ChoiceState c)
    {
        if (!ReferenceEquals(source, _currentBridge)) return;
        UiLog.Write($"choice: gen {c.Gen} {(c.Open ? $"open {c.Items.Count}" : $"closed picked {c.Picked}")}");
        if (c.Open)
        {
            _vote.Start(c.Gen, c.Items);
            if (_multi is { InRoom: true, IsHost: true }) ReleaseRemoteKeys();   // 참가자가 누르던 키로 선택지가 움직이지 않게
            BroadcastVote();
        }
        else if (_vote.Open && (c.Gen == _vote.Gen || c.Gen == 0))
        {
            string picked = c.Picked >= 0 && c.Picked < _vote.Items.Count ? _vote.Items[c.Picked].Text : "";
            _vote.Close();
            if (_multi is { InRoom: true, IsHost: true })
                NotesSend("*", new JsonObject { ["op"] = "vote", ["gen"] = _vote.Gen, ["open"] = false, ["picked"] = picked });
            RenderVotePanel();
        }
    }

    /// <summary>방장: 투표 상태를 참가자 모두(또는 한 명)에게 + 내 화면 갱신</summary>
    void BroadcastVote(string to = "*")
    {
        if (_multi is not { InRoom: true, IsHost: true }) { RenderVotePanel(); return; }
        if (_vote.Open || to != "*")
        {
            var counts = _vote.Counts();
            var voters = _vote.Voters();
            NotesSend(to, new JsonObject
            {
                ["op"] = "vote", ["gen"] = _vote.Gen, ["open"] = _vote.Open,
                ["items"] = new JsonArray(_vote.Items.Select(i => (JsonNode)new JsonObject { ["t"] = i.Text, ["e"] = i.Enabled }).ToArray()),
                ["counts"] = new JsonArray(counts.Select(n => (JsonNode)n).ToArray()),
                ["colors"] = new JsonArray(voters.Select(v => (JsonNode)new JsonArray(v.Select(id => (JsonNode)MemberColor(id)).ToArray())).ToArray()),
            });
        }
        if (to == "*") RenderVotePanel();
    }

    int MemberColor(string id) => _multi?.Members.FirstOrDefault(m => m.Id == id)?.Color ?? 0;

    /// <summary>방장: 방 인원이 바뀜 → 나간 사람의 표를 지움</summary>
    void SyncVoteMembers()
    {
        if (_multi is not { InRoom: true, IsHost: true })
        {
            if (_vote.Open) { _vote.Close(); }
            if (_multi?.InRoom != true) _guestVote = null;
            RenderVotePanel();
            return;
        }
        if (_vote.Open && _vote.Keep(_multi.Members.Where(m => !m.Host).Select(m => m.Id))) BroadcastVote();
    }

    /// <summary>노트 채널의 투표 메시지 (처리했으면 true)</summary>
    bool OnMultiVoteMessage(string from, JsonObject m)
    {
        string op = m["op"]?.GetValue<string>() ?? "";
        if (op is not ("vote" or "vote_pick")) return false;
        try
        {
            if (op == "vote_pick" && _multi is { InRoom: true, IsHost: true })
            {
                if (!_multi.Members.Any(x => x.Id == from && !x.Host)) return true;
                int gen = m["gen"]?.GetValue<int>() ?? -1, pick = m["pick"]?.GetValue<int>() ?? -1;
                if (_vote.Vote(from, gen, pick)) BroadcastVote();
            }
            else if (op == "vote" && IsMultiGuest && from == "host")
            {
                bool open = m["open"]?.GetValue<bool>() == true;
                int gen = m["gen"]?.GetValue<int>() ?? 0;
                if (!open)
                {
                    string picked = m["picked"]?.GetValue<string>() ?? "";
                    if (_guestVote != null && picked.Length > 0) ShowHudMessage($"방장이 '{picked}'을(를) 골랐습니다.", 3000);
                    _guestVote = null;
                }
                else
                {
                    if (_guestVote?["gen"]?.GetValue<int>() != gen) _guestVotePick = -1;   // 새 선택지
                    _guestVote = m;
                }
                RenderVotePanel();
                SyncMultiControl("guest");   // 투표 중에는 조종 멈춤, 끝나면 다시
                UpdateGuestTools();
            }
        }
        catch (Exception ex) { UiLog.Write($"multi: invalid vote message: {ex.Message}"); }
        return true;
    }

    void GuestVote(int pick)
    {
        if (_guestVote == null || !IsMultiGuest) return;
        _guestVotePick = pick;
        NotesSend("host", new JsonObject { ["op"] = "vote_pick", ["gen"] = _guestVote["gen"]?.GetValue<int>() ?? 0, ["pick"] = pick });
        RenderVotePanel();
    }

    // ── 투표 상자 (게임 화면 오른쪽, 오버레이 창) ──

    void RenderVotePanel()
    {
        bool host = _multi is { InRoom: true, IsHost: true } && _vote.Open && _multi.Members.Any(m => !m.Host);
        bool guest = IsMultiGuest && _guestVote != null;
        if (!host && !guest)
        {
            if (_votePanel != null) _votePanel.Visibility = Visibility.Collapsed;
            return;
        }
        if (_votePanel == null)
        {
            _votePanel = new Border
            {
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0),
                Padding = new Thickness(10, 8, 10, 8), Background = new SolidColorBrush(Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x70, 0x70, 0x70)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(2),
                MaxWidth = 300,
            };
            _overlay.Root.Children.Add(_votePanel);
        }
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = host ? "선택지 투표 (고르는 것은 방장)" : "선택지 투표 · 눌러서 투표 (바꿀 수 있음)",
            FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 6), Foreground = Brushes.Black,
        });
        List<(string text, bool enabled)> items;
        int[] counts;
        List<int>[] colors;
        if (host)
        {
            items = _vote.Items.Select(i => (i.Text, i.Enabled)).ToList();
            counts = _vote.Counts();
            colors = _vote.Voters().Select(v => v.Select(MemberColor).ToList()).ToArray();
        }
        else
        {
            items = (_guestVote!["items"] as JsonArray ?? new JsonArray())
                .Select(n => (n?["t"]?.GetValue<string>() ?? "", n?["e"]?.GetValue<bool>() != false)).ToList();
            counts = (_guestVote["counts"] as JsonArray ?? new JsonArray()).Select(n => n?.GetValue<int>() ?? 0).ToArray();
            colors = (_guestVote["colors"] as JsonArray ?? new JsonArray())
                .Select(a => (a as JsonArray ?? new JsonArray()).Select(n => n?.GetValue<int>() ?? 0).ToList()).ToArray();
        }
        for (int i = 0; i < items.Count; i++)
        {
            int index = i;
            var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
            var dots = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
            foreach (int c in i < colors.Length ? colors[i] : new List<int>())
            {
                uint pc = MultiChatStyle.PingColors[Math.Clamp(c, 0, MultiChatStyle.PingColors.Length - 1)];
                dots.Children.Add(new Ellipse
                {
                    Width = 9, Height = 9, Margin = new Thickness(1, 0, 1, 0), Stroke = Brushes.DimGray, StrokeThickness = 1,
                    Fill = new SolidColorBrush(Color.FromRgb((byte)(pc >> 16), (byte)(pc >> 8), (byte)pc)),
                });
            }
            var count = new TextBlock { Text = $"{(i < counts.Length ? counts[i] : 0)}표", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0), Foreground = Brushes.Black };
            DockPanel.SetDock(count, Dock.Right);
            DockPanel.SetDock(dots, Dock.Right);
            row.Children.Add(count);
            row.Children.Add(dots);
            string label = $"{i + 1}. {items[i].text}";
            if (guest)
            {
                var b = new Button
                {
                    Content = new TextBlock { Text = label, TextTrimming = TextTrimming.CharacterEllipsis }, HorizontalContentAlignment = HorizontalAlignment.Left,
                    Padding = new Thickness(6, 2, 6, 2), IsEnabled = items[i].enabled, Focusable = false,
                    FontWeight = _guestVotePick == i ? FontWeights.Bold : FontWeights.Normal,
                    Background = _guestVotePick == i ? new SolidColorBrush(Color.FromRgb(0xCC, 0xE4, 0xF7)) : SystemColors.ControlBrush,
                };
                // 누르는 순간 투표: 오버레이 창은 활성화되지 않아 Click(누름+뗌)이 첫 번째에 빠지는 경우가 있었음
                b.PreviewMouseLeftButtonDown += (_, e) => { e.Handled = true; if (b.IsEnabled) GuestVote(index); };
                row.Children.Add(b);
            }
            else row.Children.Add(new TextBlock { Text = label, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = items[i].enabled ? Brushes.Black : Brushes.Gray, VerticalAlignment = VerticalAlignment.Center });
            stack.Children.Add(row);
        }
        _votePanel.Child = stack;
        _votePanel.Visibility = Visibility.Visible;
    }
}
