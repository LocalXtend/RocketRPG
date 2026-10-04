#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows.Input;

namespace RocketRPG.Models;

public class HotkeyItem
{
    public string Id { get; set; } = "";
    public string Category { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string DefaultGesture { get; set; } = "";
    public string CurrentGesture { get; set; } = "";
}

public static class HotkeyManager
{
    public const string CatGeneral = "일반";
    public const string CatCheat = "치트 & 디버그";
    public const string CatViewer = "뷰어 & ESP";
    public const string CatConvenience = "편의 기능";

    public static readonly List<HotkeyItem> DefaultHotkeys = new()
    {
        new HotkeyItem { Id = "QuickSave", Category = CatGeneral, Name = "퀵 세이브", Description = "현재 진행 상태를 1번 슬롯에 빠른 저장", DefaultGesture = "F5", CurrentGesture = "F5" },
        new HotkeyItem { Id = "QuickLoad", Category = CatGeneral, Name = "퀵 로드", Description = "1번 슬롯의 데이터를 즉시 불러오기", DefaultGesture = "F8", CurrentGesture = "F8" },
        new HotkeyItem { Id = "ForceSaveMenu", Category = CatGeneral, Name = "강제 세이브 메뉴", Description = "세이브 금지 구역에서도 강제로 세이브 화면 열기", DefaultGesture = "Ctrl + S", CurrentGesture = "Ctrl + S" },
        new HotkeyItem { Id = "ForceLoadMenu", Category = CatGeneral, Name = "강제 로드 메뉴", Description = "언제 어디서나 강제로 로드 화면 열기", DefaultGesture = "Ctrl + L", CurrentGesture = "Ctrl + L" },
        new HotkeyItem { Id = "RestartGame", Category = CatGeneral, Name = "게임 재시작", Description = "현재 게임 세션을 처음부터 리셋", DefaultGesture = "Ctrl + R", CurrentGesture = "Ctrl + R" },
        new HotkeyItem { Id = "ExitGame", Category = CatGeneral, Name = "게임 종료", Description = "현재 구동 중인 게임 세션 종료", DefaultGesture = "Ctrl + Q", CurrentGesture = "Ctrl + Q" },

        new HotkeyItem { Id = "ToggleNoclip", Category = CatCheat, Name = "벽 통과 (노클립)", Description = "플레이어 벽/장애물 통과 기능 On/Off", DefaultGesture = "Ctrl + P", CurrentGesture = "Ctrl + P" },
        new HotkeyItem { Id = "SpeedUp", Category = CatCheat, Name = "배속 증가", Description = "게임 속도를 1단계 가속 (최대 8x)", DefaultGesture = "Ctrl + Up", CurrentGesture = "Ctrl + Up" },
        new HotkeyItem { Id = "SpeedDown", Category = CatCheat, Name = "배속 감소", Description = "게임 속도를 1단계 감속 (최소 0.5x)", DefaultGesture = "Ctrl + Down", CurrentGesture = "Ctrl + Down" },
        new HotkeyItem { Id = "SpeedReset", Category = CatCheat, Name = "배속 초기화", Description = "게임 속도를 정속(1.0x)으로 복귀", DefaultGesture = "Ctrl + 0", CurrentGesture = "Ctrl + 0" },
        new HotkeyItem { Id = "TogglePause", Category = CatCheat, Name = "일시 정지 / 재개", Description = "게임 루프를 즉시 일시 정지하거나 재개", DefaultGesture = "Pause", CurrentGesture = "Pause" },
        new HotkeyItem { Id = "ToggleDataInspector", Category = CatCheat, Name = "스위치/변수 관리자", Description = "실시간 스위치·변수 검색 및 Freeze 관리창 열기", DefaultGesture = "Ctrl + V", CurrentGesture = "Ctrl + V" },

        new HotkeyItem { Id = "ToggleMapViewer", Category = CatViewer, Name = "전체 맵 뷰어", Description = "전체 맵 타일 복원 뷰어 및 클릭 워프 창 열기", DefaultGesture = "Ctrl + M", CurrentGesture = "Ctrl + M" },
        new HotkeyItem { Id = "ToggleEspOverlay", Category = CatViewer, Name = "실시간 ESP 오버레이", Description = "게임 화면 위 이벤트/트리거/히트박스 시각화 토글", DefaultGesture = "F3", CurrentGesture = "F3" },
        new HotkeyItem { Id = "ToggleTileInspector", Category = CatViewer, Name = "가상 마우스 타일 인스펙터", Description = "마우스 커서 위치의 타일/트리거 상세 HUD 토글", DefaultGesture = "F2", CurrentGesture = "F2" },

        new HotkeyItem { Id = "ToggleAutoMessage", Category = CatConvenience, Name = "메시지 자동 넘김", Description = "대화창 자동 넘김 On/Off 토글", DefaultGesture = "F4", CurrentGesture = "F4" },
        new HotkeyItem { Id = "ToggleSkipMessage", Category = CatConvenience, Name = "메시지 고속 스킵", Description = "대화창 고속 스킵 On/Off 토글", DefaultGesture = "Ctrl + K", CurrentGesture = "Ctrl + K" },
        new HotkeyItem { Id = "ToggleMessageBar", Category = CatConvenience, Name = "메시지 제어 바 표시", Description = "화면 위 플로팅 메시지 제어 바 보이기/숨기기", DefaultGesture = "Ctrl + J", CurrentGesture = "Ctrl + J" },
        new HotkeyItem { Id = "ToggleNotes", Category = CatConvenience, Name = "게임 노트", Description = "오른쪽 게임 노트 패널 열기/닫기", DefaultGesture = "Ctrl + N", CurrentGesture = "Ctrl + N" },
        new HotkeyItem { Id = "ScreenshotToNote", Category = CatConvenience, Name = "스크린샷 찍어 노트에 넣기", Description = "지금 게임 화면을 찍어 보관함에 저장하고 노트에도 넣기", DefaultGesture = "Ctrl + Shift + S", CurrentGesture = "Ctrl + Shift + S" },
        new HotkeyItem { Id = "Screenshot", Category = CatConvenience, Name = "스크린샷 찍기", Description = "지금 게임 화면을 찍어 스크린샷 보관함에 저장", DefaultGesture = "Ctrl + Shift + A", CurrentGesture = "Ctrl + Shift + A" },

        new HotkeyItem { Id = "MultiChat", Category = CatGeneral, Name = "채팅하기", Description = "멀티 방에서 채팅 입력 칸 열기 (글은 게임 화면 위로 흘러갑니다)", DefaultGesture = "Ctrl + T", CurrentGesture = "Ctrl + T" },
        new HotkeyItem { Id = "MultiSummon", Category = CatGeneral, Name = "참가자 캐릭터 모두 부르기", Description = "멀티 엑스트라 모드에서 참가자 캐릭터를 모두 내 곁으로 부르기 (방장)", DefaultGesture = "Ctrl + G", CurrentGesture = "Ctrl + G" },
        new HotkeyItem { Id = "ToggleMenuBar", Category = CatGeneral, Name = "메뉴 숨기기/보이기", Description = "창 위쪽 메뉴 줄을 숨기거나 다시 보이기 (숨긴 동안에도 동작)", DefaultGesture = "Alt + H", CurrentGesture = "Alt + H" },
    };

    /// <summary>스트리머 모드·게임 없음·멀티 참가자에서도 늘 되는 단축키</summary>
    public static bool IsAlwaysAvailable(string id) => id is "ToggleNotes" or "MultiChat" or "ToggleMenuBar";

    /// <summary>
    /// 지정하면 윈도우 기능과 겹치는 조합 (Alt+Tab, Alt+F4 등)이면 그 설명, 아니면 null.
    /// 메뉴 줄 열기(Alt+메뉴 글자)와 겹치면 경고만 합니다 (<see cref="MenuAccessConflict"/>).
    /// </summary>
    public static string? SystemConflict(string gesture)
    {
        string g = gesture.Replace(" ", "").ToUpperInvariant();
        if (g.Contains("WIN+")) return "Windows 키 조합은 윈도우가 먼저 씁니다.";
        return g switch
        {
            "ALT+TAB" or "ALT+SHIFT+TAB" or "CTRL+ALT+TAB" => "창 전환(Alt+Tab)과 겹칩니다.",
            "ALT+F4" => "창 닫기(Alt+F4)와 겹칩니다.",
            "ALT+ESCAPE" or "CTRL+ESCAPE" or "CTRL+SHIFT+ESCAPE" => "윈도우 시작 메뉴·작업 관리자와 겹칩니다.",
            "ALT+SPACE" => "창 메뉴(Alt+Space)와 겹칩니다.",
            "CTRL+ALT+DELETE" => "윈도우 보안 화면과 겹칩니다.",
            _ => null,
        };
    }

    /// <summary>메뉴 줄 단축 글자 (파일 F, 화면 V, 소리 S, 설정 P, 도구 T, 멀티 U, 정보 A)와 겹치는 Alt+글자</summary>
    public static bool MenuAccessConflict(string gesture) =>
        gesture.Replace(" ", "").ToUpperInvariant() is "ALT+F" or "ALT+V" or "ALT+S" or "ALT+P" or "ALT+T" or "ALT+U" or "ALT+A";

    /// <summary>메뉴 오른쪽에 보이는 모양 ("Ctrl + Shift + S" → "Ctrl+Shift+S")</summary>
    public static string MenuText(string? gesture) => string.IsNullOrWhiteSpace(gesture) ? "" : gesture.Replace(" ", "");

    public static string GestureOf(string id) => _activeHotkeys.FirstOrDefault(h => h.Id == id)?.CurrentGesture ?? "";

    private static List<HotkeyItem> _activeHotkeys = new();

    static HotkeyManager()
    {
        ResetToDefaults();
    }

    public static IReadOnlyList<HotkeyItem> ActiveHotkeys => _activeHotkeys;

    public static void ResetToDefaults()
    {
        _activeHotkeys = DefaultHotkeys.Select(h => new HotkeyItem
        {
            Id = h.Id,
            Category = h.Category,
            Name = h.Name,
            Description = h.Description,
            DefaultGesture = h.DefaultGesture,
            CurrentGesture = h.DefaultGesture
        }).ToList();
    }

    public static void Load(Dictionary<string, string>? savedBindings)
    {
        ResetToDefaults();
        if (savedBindings == null) return;

        foreach (var item in _activeHotkeys)
        {
            if (savedBindings.TryGetValue(item.Id, out var gesture) && !string.IsNullOrWhiteSpace(gesture))
            {
                item.CurrentGesture = gesture.Trim();
            }
        }
    }

    /// <summary>스트리머 모드: 노트 켜기/끄기 말고는 단축키가 동작하지 않고, 키는 그대로 게임에 갑니다.</summary>
    public static volatile bool StreamerMode;

    public static bool IsAllowed(string id) => !StreamerMode || IsAlwaysAvailable(id);

    public const int CurrentSchema = 1;

    /// <summary>
    /// 예전 기본값을 그대로 쓰던 단축키를 새 기본값으로 옮깁니다. 바꾼 게 있으면 true.
    /// 1: 스크린샷 F12 → Ctrl+Shift+A (F12는 많은 게임에서 리셋 키라 겹쳤음).
    /// </summary>
    public static bool MigrateSaved(Dictionary<string, string>? saved, int fromSchema)
    {
        if (saved == null || fromSchema >= CurrentSchema) return false;
        bool changed = false;
        if (fromSchema < 1 && saved.TryGetValue("Screenshot", out var g) && string.Equals(g?.Trim(), "F12", StringComparison.OrdinalIgnoreCase))
        {
            saved.Remove("Screenshot");
            changed = true;
        }
        return changed;
    }

    public static Dictionary<string, string> SaveToDictionary()
    {
        return _activeHotkeys.ToDictionary(h => h.Id, h => h.CurrentGesture);
    }

    public static void UpdateBindings(IEnumerable<HotkeyItem> updated)
    {
        var dict = updated.ToDictionary(u => u.Id, u => u.CurrentGesture);
        foreach (var item in _activeHotkeys)
        {
            if (dict.TryGetValue(item.Id, out var g))
            {
                item.CurrentGesture = g;
            }
        }
    }

    /// <summary>
    /// KeyEventArgs를 분석하여 일치하는 핫키 Id를 반환합니다 (없으면 null).
    /// </summary>
    public static string? MatchAction(KeyEventArgs e)
    {
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.LeftCtrl || key == Key.RightCtrl ||
            key == Key.LeftAlt || key == Key.RightAlt ||
            key == Key.LeftShift || key == Key.RightShift ||
            key == Key.LWin || key == Key.RWin)
        {
            return null; // 단독 수정자 키는 핫키 발동 방지
        }

        ModifierKeys modifiers = Keyboard.Modifiers;
        string pressedGesture = GestureToString(modifiers, key);

        foreach (var h in _activeHotkeys)
        {
            if (!string.IsNullOrEmpty(h.CurrentGesture) &&
                string.Equals(h.CurrentGesture, pressedGesture, StringComparison.OrdinalIgnoreCase))
            {
                return IsAllowed(h.Id) ? h.Id : null;
            }
        }

        return null;
    }

    public static string GestureToString(ModifierKeys modifiers, Key key)
    {
        var parts = new List<string>();
        if ((modifiers & ModifierKeys.Control) != 0) parts.Add("Ctrl");
        if ((modifiers & ModifierKeys.Alt) != 0) parts.Add("Alt");
        if ((modifiers & ModifierKeys.Shift) != 0) parts.Add("Shift");
        if ((modifiers & ModifierKeys.Windows) != 0) parts.Add("Win");

        string keyName = key switch
        {
            Key.D0 => "0",
            Key.D1 => "1",
            Key.D2 => "2",
            Key.D3 => "3",
            Key.D4 => "4",
            Key.D5 => "5",
            Key.D6 => "6",
            Key.D7 => "7",
            Key.D8 => "8",
            Key.D9 => "9",
            Key.OemOpenBrackets => "[",
            Key.OemCloseBrackets => "]",
            Key.OemBackslash => "\\",
            Key.OemMinus => "-",
            Key.OemPlus => "+",
            _ => key.ToString()
        };

        parts.Add(keyName);
        return string.Join(" + ", parts);
    }

    public static string? MatchWebKey(string key, bool ctrl, bool alt, bool shift)
    {
        var parts = new List<string>();
        if (ctrl) parts.Add("Ctrl");
        if (alt) parts.Add("Alt");
        if (shift) parts.Add("Shift");

        string normalizedKey = key switch
        {
            "ArrowUp" => "Up",
            "ArrowDown" => "Down",
            "ArrowLeft" => "Left",
            "ArrowRight" => "Right",
            " " => "Space",
            _ => key.Length == 1 ? key.ToUpperInvariant() : key
        };

        parts.Add(normalizedKey);
        string gesture = string.Join(" + ", parts);

        foreach (var h in _activeHotkeys)
        {
            if (!string.IsNullOrEmpty(h.CurrentGesture) &&
                string.Equals(h.CurrentGesture, gesture, StringComparison.OrdinalIgnoreCase))
            {
                return IsAllowed(h.Id) ? h.Id : null;
            }
        }
        return null;
    }
}
