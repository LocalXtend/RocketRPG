#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace RocketRPG.Models;

/// <summary>
/// 멀티 방장: 참가자들이 누르고 있는 키 (조종 권한). 같은 키를 여럿이 누르면 마지막 사람이 뗄 때 게임에서 뗍니다.
/// 게임에 실제로 눌러야/떼야 하는 키만 돌려주고, 게임에 넣는 일은 부르는 쪽(MainWindow)이 합니다.
///  - 누름/뗌: <see cref="Set"/>
///  - 참가자가 0.2초마다 보내는 '지금 누르고 있는 키' 목록: <see cref="Reconcile"/> (빠진 누름/뗌 보정)
///  - 키 소식이 <see cref="LeaseMs"/> 넘게 없는 참가자: <see cref="Expired"/> → <see cref="Release"/> (연결이 끊겨 떼기가 안 온 경우)
/// </summary>
public sealed class RemoteKeyState
{
    public const long LeaseMs = 1000;

    readonly Dictionary<int, HashSet<string>> _holders = new();
    readonly Dictionary<string, long> _seen = new();

    /// <summary>게임에 눌린 키 (누군가 누르고 있는 키), 작은 번호부터</summary>
    public IEnumerable<int> Pressed => _holders.Where(kv => kv.Value.Count > 0).Select(kv => kv.Key).OrderBy(k => k);

    public bool Any => _holders.Values.Any(h => h.Count > 0);

    /// <summary>브라우저는 VK_SHIFT, WPF는 좌/우 Shift를 보내므로 같은 키로 묶습니다.</summary>
    public static int Normalize(int vk) => vk is 0xA0 or 0xA1 ? 0x10 : vk;

    /// <summary>참가자 키 소식을 받은 때 (누름·뗌·목록)</summary>
    public void Touch(string from, long now) => _seen[from] = now;

    /// <summary>참가자 한 명의 키 하나. 게임에서 실제로 바뀌면 true (그때만 게임에 넣음).</summary>
    public bool Set(string from, int vk, bool down)
    {
        vk = Normalize(vk);
        if (!_holders.TryGetValue(vk, out var h)) _holders[vk] = h = new();
        return down ? h.Add(from) && h.Count == 1 : h.Remove(from) && h.Count == 0;
    }

    /// <summary>참가자가 지금 누르고 있다는 키 목록으로 맞춤. 게임에서 바뀌는 키 (vk, 누름)를 돌려줌.</summary>
    public List<(int vk, bool down)> Reconcile(string from, IEnumerable<int> held)
    {
        var want = held.Select(Normalize).ToHashSet();
        var changes = new List<(int, bool)>();
        foreach (var (vk, h) in _holders.ToList())
            if (h.Contains(from) && !want.Contains(vk) && Set(from, vk, false)) changes.Add((vk, false));
        foreach (int vk in want.OrderBy(k => k))
            if ((!_holders.TryGetValue(vk, out var h) || !h.Contains(from)) && Set(from, vk, true)) changes.Add((vk, true));
        return changes;
    }

    /// <summary>한 참가자(null이면 모두)의 키를 뗌. 게임에서 떼야 하는 키를 돌려줌.</summary>
    public List<int> Release(string? from = null)
    {
        var up = new List<int>();
        foreach (var (vk, h) in _holders.ToList())
        {
            if (h.Count == 0) continue;
            if (from == null) h.Clear(); else if (!h.Remove(from)) continue;
            if (h.Count == 0) up.Add(vk);
        }
        if (from == null) _seen.Clear(); else _seen.Remove(from);
        return up;
    }

    /// <summary>키를 누르고 있는데 소식이 LeaseMs 넘게 없는 참가자</summary>
    public List<string> Expired(long now) =>
        _holders.Values.SelectMany(h => h).Distinct().Where(id => now - _seen.GetValueOrDefault(id, long.MinValue / 2) > LeaseMs).ToList();

    /// <summary>키를 누르고 있는 참가자</summary>
    public IEnumerable<string> Holders => _holders.Values.SelectMany(h => h).Distinct();
}
