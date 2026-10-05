#nullable enable
using System.Collections.Generic;

namespace RocketRPG.Models;

/// <summary>
/// 멀티 규칙 (화면과 상관없는 판단만). 메인 창은 이 결과를 보여 주고 실행만 합니다.
///   방송 화질, 참가자 안내 글, 참가자가 쓸 수 있는 도구·ESP, 참가자가 방장에게 요청할 수 있는 동작.
/// </summary>
public static class MultiPolicy
{
    /// <summary>방 설정의 화질 → (초당 프레임, 최대 높이, 최대 비트레이트)</summary>
    public static (int fps, int maxHeight, int bitrate) Quality(MultiRoomSettings s)
    {
        int fps = s.Fps >= 60 ? 60 : 30;
        return s.Quality switch
        {
            "low" => (fps, 540, fps == 60 ? 1_800_000 : 1_200_000),
            "high" => (fps, 1080, fps == 60 ? 8_000_000 : 5_000_000),
            _ => (fps, 720, fps == 60 ? 4_000_000 : 2_500_000),
        };
    }

    /// <summary>참가자 화면 안내 글 ("" = 게임 중이라 영상을 보여 줌). ownershipStatus = 스팀 게임 확인 안내.</summary>
    public static string GuestStatus(bool reconnecting, bool hostAway, string? game, string ownershipStatus) =>
        reconnecting ? "연결이 끊겨 다시 연결하는 중..."
        : hostAway ? "방장 연결이 끊겼습니다.\n1분 안에 돌아오지 않으면 방이 없어집니다."
        : string.IsNullOrEmpty(game) ? "방장이 게임을 켜기를 기다리는 중..."
        : ownershipStatus;

    /// <summary>참가자가 도구·메시지 바를 방장에게 요청할 수 있음: 조종 권한 + 방장·나 모두 스트리머 모드 아님 + 방장 게임 중</summary>
    public static bool GuestToolsAllowed(bool control, bool hostStreamer, bool myStreamer, bool hostRunning) =>
        control && !hostStreamer && !myStreamer && hostRunning;

    /// <summary>방장이 참가자에게 도구 상태(배속·벽 통과·게임 위치 등)를 보냄: 조종 권한 + 방장 스트리머 아님</summary>
    public static bool HostSharesTools(bool control, bool hostStreamer) => control && !hostStreamer;

    /// <summary>
    /// 방장 ESP를 참가자도 봄: 보기만 하는 것이라 조종 권한과 상관없이 방장이 켜면 함께 (방장 스트리머 모드에서는 숨김).
    /// </summary>
    public static bool HostSharesEsp(bool espOn, bool hostStreamer, bool hasGuests) => espOn && !hostStreamer && hasGuests;

    /// <summary>참가자 화면에 방장 ESP를 그림</summary>
    public static bool GuestEspVisible(bool hostEsp, bool hostStreamer, bool hostRunning) => hostEsp && !hostStreamer && hostRunning;

    /// <summary>도구 권한이 있어야 하는 요청</summary>
    public static readonly IReadOnlySet<string> SharedActions = new HashSet<string> { "QuickSave", "QuickLoad", "ForceSaveMenu", "ForceLoadMenu",
        "ToggleNoclip", "ToggleEspOverlay", "ToggleAutoMessage", "ToggleSkipMessage",
        "SpeedUp", "SpeedDown", "SpeedReset", "TogglePause", "ToggleMessageBar" };

    /// <summary>메시지 바 단추 (단축키로도 요청할 수 있는 것)</summary>
    public static readonly IReadOnlySet<string> BarActions = new HashSet<string> { "ToggleAutoMessage", "ToggleSkipMessage", "AutoSpeed", "QuickSave", "QuickLoad" };

    /// <summary>한 번 실행하는 요청 (켜기/끄기가 아니라 중복 실행을 막아야 함)</summary>
    public static readonly IReadOnlySet<string> OneShotActions = new HashSet<string> { "QuickSave", "QuickLoad", "ForceSaveMenu", "ForceLoadMenu" };
}
