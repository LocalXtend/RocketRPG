#nullable enable

namespace RocketRPG.Models;

/// <summary>
/// 멀티 메뉴 항목이 언제 보이는지 (항목 x:Name 기준). 메인 창 메뉴와 사용법 그림이 같은 규칙을 씁니다
/// (사용법 그림은 지금 상태와 상관없이 '방 밖 / 방장 / 참가자' 메뉴를 그대로 그려야 하므로).
/// </summary>
public static class MultiMenuRules
{
    /// <summary>사용법 그림의 상태: 방 밖, 방장, 참가자</summary>
    public const string Outside = "out", Host = "host", Guest = "guest";

    /// <summary>규칙이 있는 항목이면 보이는지, 없으면 null (항상 그대로)</summary>
    public static bool? Visible(string? name, bool inRoom, bool host) => name switch
    {
        "MultiRoomHeader" or "MultiCodeItem" or "MultiMembersMenu" or "MultiColorMenu" or "MultiRoomSeparator" or "MultiChatItem" or "MultiChatLogItem" => inRoom,
        "MultiCreateItem" or "MultiJoinItem" => !inRoom,
        "MultiControlItem" or "MultiExtraItem" or "MultiQualityMenu" or "MultiNotesEditItem" or "MultiDissolveItem" => inRoom && host,
        "MultiLeaveItem" => inRoom && !host,
        _ => null,
    };

    public static bool? Visible(string? name, string state) =>
        Visible(name, state is Host or Guest, state == Host);

    /// <summary>사용법 그림에 쓰는 예시 참가자 (방장 메뉴에서는 '친구'에게 관리 메뉴가 달림)</summary>
    public const string SampleFriend = "친구";
    public const string SampleRoomTitle = "친구들과 모험";
    public const string SampleRoomCode = "AB12CD";
}
