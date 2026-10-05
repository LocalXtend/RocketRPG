using System;
using System.Linq;
using RocketRPG.Models;

namespace RocketRPG.Tests;

// 1.0.0 멀티: 참가자 키(박힘 방지), 참가자 창 제목
public partial class Program
{
    private static void TestMultiRemoteKeys()
    {
        Console.WriteLine("--- Testing RemoteKeyState (multi control) ---");
        var k = new RemoteKeyState();
        k.Touch("a", 0);
        Assert("First press reaches the game", k.Set("a", 0x41, true));
        Assert("Repeated press is not sent again", !k.Set("a", 0x41, true));
        k.Touch("b", 0);
        Assert("Second holder does not press again", !k.Set("b", 0x41, true));
        Assert("First release keeps the key while someone else holds it", !k.Set("a", 0x41, false));
        Assert("Last release reaches the game", k.Set("b", 0x41, false));
        Assert("Release of a key nobody holds is ignored", !k.Set("b", 0x41, false));

        // 좌/우 Shift와 VK_SHIFT는 같은 키
        Assert("Left shift presses VK_SHIFT", k.Set("a", 0xA0, true) && k.Pressed.SequenceEqual(new[] { 0x10 }));
        Assert("VK_SHIFT release releases left shift", k.Set("a", 0x10, false) && !k.Any);

        // 목록 보정: 떼기가 빠졌으면 떼고, 누름이 빠졌으면 누름
        k.Set("a", 0x25, true);
        k.Set("a", 0x5A, true);
        var changes = k.Reconcile("a", new[] { 0x5A, 0xA1 });
        Assert("Reconcile releases a key missing from the held list", changes.Contains((0x25, false)));
        Assert("Reconcile presses a held key that never arrived", changes.Contains((0x10, true)));
        Assert("Reconcile leaves held keys alone", !changes.Any(c => c.vk == 0x5A));
        Assert("Reconcile of the same list changes nothing", k.Reconcile("a", new[] { 0x5A, 0x10 }).Count == 0);

        // 다른 사람이 누른 키는 보정·임대 만료에서 건드리지 않음
        k.Touch("b", 0);
        k.Set("b", 0x5A, true);
        Assert("Reconcile does not release a key another participant holds", !k.Reconcile("a", Array.Empty<int>()).Any(c => c.vk == 0x5A));
        Assert("Key stays pressed while the other participant holds it", k.Pressed.Contains(0x5A));

        // 소식이 1초 넘게 없으면 그 사람 키만 뗌
        k.Touch("a", 5000);
        k.Set("a", 0x26, true);
        k.Touch("b", 4500);
        var expired = k.Expired(5600);
        Assert("Participant silent for over a second expires", expired.SequenceEqual(new[] { "b" }), string.Join(",", expired));
        var up = k.Release("b");
        Assert("Releasing an expired participant frees only their keys", up.SequenceEqual(new[] { 0x5A }) && k.Pressed.SequenceEqual(new[] { 0x26 }),
            $"up={string.Join(",", up)} pressed={string.Join(",", k.Pressed)}");
        Assert("Active participant does not expire", k.Expired(5900).Count == 0);
        Assert("Release all frees every key", k.Release().SequenceEqual(new[] { 0x26 }) && !k.Any);
    }

    private static void TestChatColorCooldown()
    {
        Console.WriteLine("--- Testing ChatColorCooldown (30s per non-white color change) ---");
        var c = new ChatColorCooldown();
        Assert("Changing to a color is allowed at first", c.TryChange(0, 1, 1000));
        Assert("Wait is 30 seconds after a color change", c.WaitSeconds(1000) == 30, c.WaitSeconds(1000).ToString());
        Assert("Another color is blocked during the cooldown", !c.TryChange(1, 2, 20_000));
        Assert("White is always allowed", c.TryChange(1, 0, 20_000));
        Assert("Back to a color is still blocked after white", !c.TryChange(0, 3, 25_000));
        Assert("Keeping the same color is not a change", c.TryChange(1, 1, 25_000));
        Assert("Color change works again after 30 seconds", c.TryChange(0, 4, 31_000));
        Assert("A new change restarts the cooldown", c.WaitSeconds(31_000) == 30);
    }

    private static void TestMultiTitle()
    {
        Console.WriteLine("--- Testing MultiTitle (guest window title) ---");
        Assert("Guest title shows the host game", MultiTitle.ForGuest("RocketRPG 1.0.0", "용사 이야기") == "용사 이야기 - RocketRPG 1.0.0 (참가 중)");
        Assert("Guest title without a game", MultiTitle.ForGuest("RocketRPG 1.0.0", "  ") == "RocketRPG 1.0.0 (참가 중)");
        Assert("Control characters and line breaks are removed", MultiTitle.CleanGame("a\u0000b\r\n  c\t") == "a b c", MultiTitle.CleanGame("a\u0000b\r\n  c\t"));
        string longName = new string('가', 80);
        string cut = MultiTitle.CleanGame(longName);
        Assert("Long game names are cut", cut.Length == MultiTitle.MaxGameLength + 1 && cut.EndsWith("…"), cut.Length.ToString());
        Assert("Emoji are not split when cutting", MultiTitle.CleanGame(string.Concat(Enumerable.Repeat("😀", 70))).EndsWith("😀…"));
    }
}

public partial class Program
{
    private static void TestChoiceVote()
    {
        Console.WriteLine("--- Testing ChoiceVote (multi choice voting) ---");
        var v = new ChoiceVote();
        Assert("No vote before a choice opens", !v.Vote("a", 0, 0));
        v.Start(3, new[] { ("예", true), ("아니오", true), ("잠긴 선택지", false) });
        Assert("Vote opens with items", v.Open && v.Items.Count == 3);
        Assert("Valid vote is accepted", v.Vote("a", 3, 0));
        Assert("Same vote twice is not a change", !v.Vote("a", 3, 0));
        Assert("Changing a vote is accepted", v.Vote("a", 3, 1) && v.VoteOf("a") == 1);
        Assert("Vote for a disabled item is refused", !v.Vote("b", 3, 2));
        Assert("Vote with an old choice number is refused", !v.Vote("b", 2, 0));
        Assert("Vote out of range is refused", !v.Vote("b", 3, 5) && !v.Vote("b", 3, -1));
        v.Vote("b", 3, 1);
        v.Vote("c", 3, 0);
        Assert("Counts per line", v.Counts().SequenceEqual(new[] { 1, 2, 0 }), string.Join(",", v.Counts()));
        Assert("Voters per line", v.Voters()[1].OrderBy(x => x).SequenceEqual(new[] { "a", "b" }));
        Assert("Leaving members lose their vote", v.Keep(new[] { "a", "c" }) && v.Counts().SequenceEqual(new[] { 1, 1, 0 }));
        v.Start(4, new[] { ("예", true), ("아니오", true) });
        Assert("A new choice clears previous votes", v.Counts().Sum() == 0 && v.VoteOf("a") == null);
        v.Close();
        Assert("Closed vote refuses votes", !v.Vote("a", 4, 0) && !v.Open);
        var parsed = MkxpAgentChannel.ParseChoice("7\u001F-1\u001F1예\n0아니오");
        Assert("Engine choice line parses", parsed.Open && parsed.Gen == 7 && parsed.Items.Count == 2 && parsed.Items[0] == ("예", true) && parsed.Items[1] == ("아니오", false));
        var closed = MkxpAgentChannel.ParseChoice("7\u001F1\u001F");
        Assert("Engine choice end parses", !closed.Open && closed.Picked == 1);
    }

    // 1.1.5: 멀티 규칙 (화질, 참가자 안내 글, 도구·ESP 권한)
    private static void TestMultiPolicy()
    {
        Console.WriteLine("--- Testing MultiPolicy ---");
        Assert("Normal quality is 720p 30fps", MultiPolicy.Quality(new MultiRoomSettings()) == (30, 720, 2_500_000));
        Assert("High quality at 60fps", MultiPolicy.Quality(new MultiRoomSettings { Quality = "high", Fps = 60 }) == (60, 1080, 8_000_000));
        Assert("Unknown fps falls back to 30", MultiPolicy.Quality(new MultiRoomSettings { Quality = "low", Fps = 45 }).fps == 30);

        Assert("Reconnecting wins over everything", MultiPolicy.GuestStatus(true, true, "", "x").StartsWith("연결이 끊겨"));
        Assert("Host away message", MultiPolicy.GuestStatus(false, true, "game", "").StartsWith("방장 연결이"));
        Assert("No game: waiting for the host", MultiPolicy.GuestStatus(false, false, "", "").StartsWith("방장이 게임을"));
        Assert("In game: ownership status (empty = show video)", MultiPolicy.GuestStatus(false, false, "game", "") == "");

        Assert("Guest tools need control", !MultiPolicy.GuestToolsAllowed(false, false, false, true));
        Assert("Guest tools hidden when the host streams", !MultiPolicy.GuestToolsAllowed(true, true, false, true));
        Assert("Guest tools hidden in my streamer mode", !MultiPolicy.GuestToolsAllowed(true, false, true, true));
        Assert("Guest tools need a running game", !MultiPolicy.GuestToolsAllowed(true, false, false, false));
        Assert("Guest tools allowed", MultiPolicy.GuestToolsAllowed(true, false, false, true));

        // ESP는 조종 권한과 상관없음 (1.1.5)
        Assert("Host shares ESP without control", MultiPolicy.HostSharesEsp(espOn: true, hostStreamer: false, hasGuests: true));
        Assert("Host does not share ESP in streamer mode", !MultiPolicy.HostSharesEsp(true, true, true));
        Assert("No ESP share without guests", !MultiPolicy.HostSharesEsp(true, false, false));
        Assert("Guest sees host ESP", MultiPolicy.GuestEspVisible(hostEsp: true, hostStreamer: false, hostRunning: true));
        Assert("Guest hides ESP when the host streams", !MultiPolicy.GuestEspVisible(true, true, true));

        Assert("Quick save is one-shot and a bar action", MultiPolicy.OneShotActions.Contains("QuickSave") && MultiPolicy.BarActions.Contains("QuickSave"));
        Assert("Every one-shot action needs tool permission", MultiPolicy.OneShotActions.All(MultiPolicy.SharedActions.Contains));
    }
}
