#nullable enable
using System.Collections.Generic;
using System.Linq;

namespace RocketRPG.Models;

/// <summary>
/// 멀티 엑스트라 모드를 받는 게임 (참가자마다 캐릭터). MV/MZ는 rocket_extra.js, XP/VX/Ace는 루비 에이전트(rocket_mkxp_agent.rb),
/// 2000/2003은 EasyRPG Player 수정본이 같은 규칙으로 캐릭터를 둡니다.
/// </summary>
public interface IExtraModeTarget
{
    /// <summary>이 게임에서 엑스트라 모드를 쓸 수 있는지 (게임 안 쪽이 준비됨)</summary>
    bool ExtraSupported { get; }
    void SetExtraMode(bool on);
    /// <summary>캐릭터를 둘 참가자 (id, 이름, 이름표 색 #rrggbb)</summary>
    void SetExtraGuests(IEnumerable<(string id, string name, string color)> guests);
    void ExtraKey(string id, int vk, bool down);
    void ExtraHeld(string id, IEnumerable<int> keys);
    /// <summary>방장: 참가자 캐릭터를 모두 방장 자리로</summary>
    void ExtraSummon();
}

/// <summary>루비 에이전트·EasyRPG 에이전트 파이프 명령 (xmode / xguests / xkey / xheld)</summary>
public static class ExtraAgentCommands
{
    public static void Mode(MkxpAgentChannel? ch, bool on) => ch?.Send("xmode", on);

    /// <summary>참가자 목록: 한 사람은 id \x01 이름 \x01 #색, 사람 사이는 \x02 (이름 안의 구분 문자는 지움)</summary>
    public static string GuestList(IEnumerable<(string id, string name, string color)> guests) =>
        string.Join("\u0002", guests.Select(g => $"{Clean(g.id)}\u0001{Clean(g.name)}\u0001{Clean(g.color)}"));

    static string Clean(string s) => new string(s.Where(c => c >= ' ').ToArray());

    public static void Guests(MkxpAgentChannel? ch, IEnumerable<(string id, string name, string color)> guests) => ch?.Send("xguests", GuestList(guests));
    public static void Key(MkxpAgentChannel? ch, string id, int vk, bool down) => ch?.Send("xkey", id, vk, down);
    public static void Held(MkxpAgentChannel? ch, string id, IEnumerable<int> keys) => ch?.Send("xheld", id, string.Join(",", keys));
    public static void Summon(MkxpAgentChannel? ch) => ch?.Send("xsummon");
}
