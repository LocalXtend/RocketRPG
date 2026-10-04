#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace RocketRPG.Models;

/// <summary>
/// 멀티 선택지 투표 (방장이 집계). 게임에 선택지가 뜨면 열고, 방장이 고르거나 화면이 바뀌면 닫습니다.
/// 한 사람 한 표이고 바꿀 수 있습니다. 선택지 번호(Gen)가 다른 표(지난 선택지)는 버립니다. 최종 선택은 방장이 합니다.
/// </summary>
public sealed class ChoiceVote
{
    readonly Dictionary<string, int> _votes = new();   // 참가자 id → 고른 줄

    public int Gen { get; private set; }
    public bool Open { get; private set; }
    public IReadOnlyList<(string Text, bool Enabled)> Items { get; private set; } = [];

    /// <summary>새 선택지 (이전 표는 모두 버림)</summary>
    public void Start(int gen, IEnumerable<(string Text, bool Enabled)> items)
    {
        Gen = gen;
        Items = items.Take(16).Select(i => (i.Text.Length > 80 ? i.Text[..80] : i.Text, i.Enabled)).ToList();
        Open = Items.Count > 0;
        _votes.Clear();
    }

    public void Close()
    {
        Open = false;
        _votes.Clear();
    }

    /// <summary>표 하나 (같은 사람은 바꿈). 받아들였으면 true.</summary>
    public bool Vote(string member, int gen, int pick)
    {
        if (!Open || gen != Gen || pick < 0 || pick >= Items.Count || !Items[pick].Enabled || member.Length == 0) return false;
        if (_votes.TryGetValue(member, out int old) && old == pick) return false;
        _votes[member] = pick;
        return true;
    }

    /// <summary>나간 사람의 표를 지움. 바뀌었으면 true.</summary>
    public bool Remove(string member) => _votes.Remove(member);

    /// <summary>방에 남아 있는 사람 말고는 표를 지움. 바뀌었으면 true.</summary>
    public bool Keep(IEnumerable<string> present)
    {
        var keep = present.ToHashSet();
        return _votes.Keys.Where(k => !keep.Contains(k)).ToList().Count(Remove) > 0;
    }

    public int? VoteOf(string member) => _votes.TryGetValue(member, out int p) ? p : null;

    /// <summary>줄마다 득표 수</summary>
    public int[] Counts()
    {
        var c = new int[Items.Count];
        foreach (int p in _votes.Values) if (p >= 0 && p < c.Length) c[p]++;
        return c;
    }

    /// <summary>줄마다 그 줄에 투표한 사람들 (들어온 순서대로)</summary>
    public List<string>[] Voters()
    {
        var v = Enumerable.Range(0, Items.Count).Select(_ => new List<string>()).ToArray();
        foreach (var (m, p) in _votes) if (p >= 0 && p < v.Length) v[p].Add(m);
        return v;
    }
}
