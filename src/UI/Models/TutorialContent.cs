#nullable enable
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace RocketRPG.Models;

/// <summary>
/// 사용법 그림 종류. 그림은 미리 찍어 둔 파일이 아니라 사용법 창이 지금의 실제 메뉴·창으로 그때그때 그립니다
/// (UI가 바뀌어도 그림이 낡지 않게). 게임 화면 자리는 직접 그린 예시만 씁니다 (게임·RTP 그림을 담지 않음).
///   Menu      : 메뉴 줄에서 Args 경로를 차례로 연 모습 (예: "도구", "벽 통과")
///   Window    : 실제 창 하나 (Args[0] = 창 이름), Args[1..] = 번호를 붙일 글자(단추·칸)
///   Bar       : 메시지 바        Vote : 선택지 투표 상자     Chat : 화면 위로 흐르는 채팅
///   Ping      : 가운데 버튼 핑    Library : 쯔꾸르 모음        Notes : 노트
/// 멀티 메뉴는 방 밖·방장·참가자일 때 항목이 다르므로, Menu 그림에는 State(MultiMenuRules.Outside/Host/Guest)를 정해
/// 지금 상태와 상관없이 그 상태의 메뉴를 그립니다. Caption은 그림 위 한 줄 설명입니다.
/// </summary>
public sealed record TutorialPicture(string Kind, params string[] Args)
{
    public string? State { get; init; }
    public string? Caption { get; init; }
}

/// <summary>사용법 한 쪽: 한 줄 설명 → 그림(번호) → 더 보기 그림 → 따라 하기 → 결과 그림</summary>
public sealed record TutorialPage(string Id, string Title, string Summary, TutorialPicture? Picture, IReadOnlyList<string> Steps,
    TutorialPicture? Result = null, string? Tip = null, IReadOnlyList<TutorialPicture>? More = null);

public sealed record TutorialSection(string Title, IReadOnlyList<TutorialPage> Pages);

/// <summary>
/// 처음 쓰는 사람을 위한 사용법 내용 (도움말 &gt; 사용법). 기술 용어 대신 "무엇을 누르면 무엇이 되는지"만 씁니다.
/// 글 안의 {key:동작 id}는 지금 단축키 설정으로 바뀝니다 (예: {key:MultiChat} → Ctrl+T).
/// 메뉴별 기능 설명(MenuHelp)은 메뉴의 Click 처리 함수 이름으로 찾습니다. 메뉴 항목을 새로 만들면 여기에도 적어야
/// 테스트(TestTutorialCoversMenus)가 통과합니다.
/// </summary>
public static class TutorialContent
{
    /// <summary>{key:id} → 현재 단축키 (없으면 "단축키 없음")</summary>
    public static string ApplyKeys(string text) =>
        Regex.Replace(text, @"\{key:([A-Za-z]+)\}", m =>
        {
            string g = HotkeyManager.MenuText(HotkeyManager.GestureOf(m.Groups[1].Value));
            return g.Length > 0 ? g : "단축키 없음";
        });

    /// <summary>메뉴 항목 설명 (Click 처리 함수 이름 → 한두 문장)</summary>
    public static readonly IReadOnlyDictionary<string, string> MenuHelp = new Dictionary<string, string>
    {
        // 파일
        ["OnGameLibrary"] = "PC에 있는 쯔꾸르 게임을 찾아 모아 보여 줍니다. 게임을 두 번 누르면 실행됩니다.",
        ["OnLoadGame"] = "게임 폴더를 직접 골라 실행합니다.",
        ["OnRestart"] = "지금 게임을 처음부터 다시 켭니다 (저장하지 않은 진행은 사라집니다).",
        ["OnGameExit"] = "지금 게임만 끕니다. RocketRPG는 그대로 켜져 있습니다.",
        ["OnExit"] = "RocketRPG를 끕니다.",
        // 화면
        ["OnRatio"] = "게임 화면 비율을 고정합니다. '없음'이면 창에 꽉 채웁니다.",
        ["OnGamma"] = "게임 화면 밝기를 바꿉니다.",
        ["OnFilter"] = "도트 그림을 부드럽게 키우는 화질 필터를 고릅니다.",
        ["OnFrameRate"] = "화면을 그리는 횟수를 정합니다. 게임 속도는 바뀌지 않습니다.",
        ["OnToggleVSync"] = "화면 찢어짐을 줄입니다 (모니터 주사율에 맞춤).",
        ["OnToggleEspOverlay"] = "게임 화면 위에 이벤트 위치를 색 상자로 보여 줍니다.",
        ["OnToggleTileInspector"] = "마우스를 올린 칸이 지나갈 수 있는지, 어떤 이벤트가 있는지 보여 줍니다.",
        ["OnInGameFontSettings"] = "게임 안 글자의 글꼴과 크기를 바꿉니다.",
        // 소리
        ["OnVolumeLevel"] = "게임 전체 소리 크기를 바꿉니다.",
        ["OnOutputDeviceClick"] = "게임 소리가 나올 장치(스피커·헤드셋)를 고릅니다 (Windows 11).",
        // 설정
        ["OnHotkeySettings"] = "단축키를 바꿉니다. 원하는 기능을 고르고 새 키를 누른 뒤 '지정'을 누르세요.",
        ["OnMenuBarMenu"] = "메뉴 줄을 숨깁니다. 같은 단축키({key:ToggleMenuBar})를 다시 누르면 보입니다.",
        ["OnRtpFolders"] = "RTP(기본 그림·소리 모음)가 있는 폴더를 직접 고릅니다. 비워 두면 자동으로 찾습니다.",
        ["OnFontSettings"] = "RocketRPG 메뉴와 창의 글꼴을 바꿉니다.",
        ["OnMemoSettings"] = "노트 메모의 기본 글꼴·크기·색과 노트 바탕색을 바꿉니다.",
        ["OnToggleStreamer"] = "방송할 때 켭니다. 노트 말고 도구·단축키가 꺼지고 방 제목·코드가 숨겨집니다.",
        ["OnToggleDiscord"] = "디스코드 프로필에 지금 하는 게임을 보여 줍니다.",
        ["OnCheckUpdate"] = "새 버전이 있는지 확인합니다.",
        ["OnUpdateChannel"] = "정식 버전만 받을지, 베타 버전도 받을지 고릅니다.",
        // 도구
        ["OnToggleNotes"] = "오른쪽에 게임 노트를 엽니다. 게임마다 따로 저장됩니다.",
        ["OnScreenshotOnly"] = "지금 게임 화면을 찍어 스크린샷 보관함에 저장합니다.",
        ["OnScreenshotToNote"] = "지금 게임 화면을 찍어 보관함과 노트에 함께 넣습니다.",
        ["OnOpenGallery"] = "찍은 스크린샷을 모아 봅니다.",
        ["OnDialogueLogMenu"] = "지나간 대사를 다시 봅니다.",
        ["OnMapViewer"] = "게임 맵 전체를 그림으로 봅니다. 칸을 눌러 그 자리로 이동할 수도 있습니다.",
        ["OnAssetViewer"] = "게임의 그림·소리·글꼴을 둘러봅니다. 이 PC 안에서만 보고 저장할 수는 없습니다.",
        ["OnDataInspector"] = "게임의 스위치·변수를 찾아 보고 바꿉니다.",
        ["OnQuickSaveMenu"] = "1번 칸에 바로 저장합니다.",
        ["OnQuickLoadMenu"] = "1번 칸에서 바로 불러옵니다.",
        ["OnForceSaveMenu"] = "저장이 막힌 곳에서도 저장 화면을 엽니다.",
        ["OnForceLoadMenu"] = "언제든 불러오기 화면을 엽니다.",
        ["OnToggleNoclip"] = "캐릭터가 벽을 지나갈 수 있게 합니다.",
        ["OnPauseMenu"] = "게임을 잠시 멈추거나 다시 움직입니다.",
        ["OnSpeedMenuClick"] = "게임 속도를 바꿉니다 (0.5배~8배).",
        ["OnSpeedStepMenu"] = "게임 속도를 한 단계 올리거나 내리거나 원래대로 돌립니다.",
        ["OnToggleAutoMessage"] = "대사를 자동으로 넘깁니다.",
        ["OnAutoSpeedMenu"] = "자동 넘김 속도를 정합니다.",
        ["OnToggleSkipMessage"] = "대사를 아주 빠르게 넘깁니다.",
        ["OnToggleMessageBar"] = "대사가 나올 때 화면 아래에 기록·스킵·자동 단추 줄을 보여 줍니다.",
        ["OnResetGameSettings"] = "이 게임의 RocketRPG 설정(비율·밝기·소리 등)을 처음 상태로 되돌립니다.",
        // 멀티
        ["OnMultiCopyCode"] = "방 코드를 복사합니다. 친구에게 보내면 그 코드로 들어올 수 있습니다.",
        ["OnMultiChatMenu"] = "채팅을 칩니다. 글은 게임 화면 위로 흘러갑니다.",
        ["OnMultiChatLog"] = "누가 무슨 말을 했는지 채팅 기록을 봅니다.",
        ["OnMultiCreate"] = "방을 만듭니다. 내 게임 화면을 참가자가 함께 봅니다.",
        ["OnMultiJoin"] = "열린 방 목록에서 고르거나 방 코드를 넣어 들어갑니다.",
        ["OnMultiRename"] = "방에서 보일 내 이름을 바꿉니다.",
        ["OnMultiHidePings"] = "다른 사람이 찍은 핑을 내 화면에서 숨깁니다.",
        ["OnMultiHideChat"] = "다른 사람 채팅을 내 화면에서 숨깁니다 (채팅 기록에는 남음).",
        ["OnMultiControl"] = "(방장) 참가자도 키보드로 게임을 조작하고 메시지 바·도구를 쓸 수 있게 합니다.",
        ["OnMultiNotesEditable"] = "(방장) 참가자도 방 노트를 고칠 수 있게 합니다.",
        ["OnMultiQuality"] = "(방장) 참가자에게 보내는 화면 화질을 정합니다.",
        ["OnMultiFps"] = "(방장) 참가자에게 보내는 화면을 초당 몇 장으로 보낼지 정합니다.",
        ["OnMultiLeave"] = "방에서 나옵니다.",
        ["OnMultiDissolve"] = "(방장) 방을 없앱니다. 모든 참가자가 나가게 됩니다.",
        // 정보
        ["OnTutorial"] = "이 사용법 창을 엽니다.",
        ["OnAbout"] = "RocketRPG 버전, 만든 사람, 라이선스와 함께 쓰는 프로그램의 고지를 봅니다.",
    };

    public static IReadOnlyList<TutorialSection> Sections { get; } =
    [
        new("처음 시작", [
            new("start-library", "게임 실행하기 (쯔꾸르 모음)", "PC에 있는 쯔꾸르 게임을 찾아 목록으로 보여 주고, 골라서 바로 실행합니다.",
                new TutorialPicture("Menu", "파일", "쯔꾸르 모음"),
                ["메뉴 줄에서 '파일'을 누릅니다.", "'쯔꾸르 모음...'을 누릅니다.", "처음이면 '폴더 추가'로 게임이 있는 폴더를 고릅니다.", "실행할 게임을 두 번 누릅니다."],
                new TutorialPicture("Library"), "한 번 실행한 게임은 '파일 > 히스토리'에도 남습니다."),
            new("start-open", "게임 실행하기 (폴더로 찾기)", "게임 폴더를 직접 골라 실행합니다.",
                new TutorialPicture("Menu", "파일", "쯔꾸르 불러오기"),
                ["'파일 > 쯔꾸르 불러오기...'를 누릅니다.", "게임 파일(Game.exe, RPG_RT.exe 등)이 있는 폴더를 고릅니다.", "'폴더 선택'을 누르면 바로 실행됩니다."],
                null, "게임 폴더를 RocketRPG 창에 끌어다 놓아도 실행됩니다."),
            new("start-rtp", "그림·소리가 빠져 보일 때 (RTP)", "오래된 쯔꾸르 게임은 'RTP'라는 기본 그림·소리 모음이 따로 필요합니다.",
                new TutorialPicture("Window", "rtp", "찾아보기..."),
                ["화면 위에 'RTP가 필요한데 찾지 못했습니다' 알림이 뜨면 'RTP 받기'로 설치합니다.", "이미 RTP가 있는데도 알림이 뜨면 '설정 > RTP 폴더 지정...'을 엽니다.", "엔진 칸 옆 '찾아보기...'로 RTP 폴더를 고르고 '확인'을 누릅니다.", "게임을 다시 실행합니다."]),
        ]),
        new("내 이름", [
            new("name", "이름 바꾸기", "멀티 방에서 다른 사람에게 보일 내 이름을 정합니다.",
                new TutorialPicture("Menu", "멀티", "방 설정", "이름 설정하기") { State = MultiMenuRules.Outside },
                ["'멀티 > 방 설정 > 이름 설정하기...'를 누릅니다.", "새 이름을 적습니다 (20자까지).", "'확인'을 누르면 바로 바뀝니다. 방에 있는 중이면 다른 사람에게도 바로 보입니다."],
                new TutorialPicture("Window", "name", "확인")),
        ]),
        new("멀티", [
            new("multi-create", "방 만들기", "내 게임 화면을 친구들과 함께 봅니다.",
                new TutorialPicture("Window", "create-room", "방 이름", "비밀번호", "만들기"),
                ["'멀티 > 방 생성하기...'를 누릅니다.", "방 이름과 (필요하면) 비밀번호, 최대 인원을 정합니다.", "'만들기'를 누릅니다.", "게임을 켜면 참가자 화면에 보입니다."],
                null, "'방 목록에 표시'를 끄면 방 코드를 아는 사람만 들어옵니다. 코드는 '멀티 > 방 코드 복사'로 복사합니다."),
            new("multi-join", "방에 들어가기", "친구가 만든 방에 들어가 친구 게임 화면을 봅니다.",
                new TutorialPicture("Window", "join-room", "새로 고침", "코드로 입장"),
                ["'멀티 > 방 입장하기...'를 누릅니다.", "목록에서 방을 두 번 누르거나, 방 코드를 넣고 '코드로 입장'을 누릅니다.", "비밀번호가 있는 방은 비밀번호를 넣습니다.", "디스코드에 뜬 '참가' 단추를 눌러도 바로 들어올 수 있습니다."],
                null, "방에 들어가면 하던 게임은 꺼집니다."),
            new("multi-blocked", "화면이 연결되지 않을 때", "참가자 화면에 '방장과 연결되지 않습니다'가 뜨면 두 사람의 인터넷이 서로 바로 이어지지 못하는 경우입니다.",
                null,
                ["그대로 기다리면 RocketRPG가 계속 다시 연결해 봅니다.", "안내에 '내 네트워크가 막고 있음' 또는 '방장 네트워크가 막고 있음'이 나옵니다.", "ZeroTier·Tailscale·Radmin VPN 같은 가상 LAN을 방장과 같이 켜면 연결됩니다.", "휴대폰 테더링·회사·학교·PC방 네트워크는 막혀 있는 경우가 많습니다."]),
            new("multi-control", "조종 권한", "방장이 켜 주면 참가자도 키보드로 방장 게임을 조작하고 메시지 바·도구를 쓸 수 있습니다.",
                new TutorialPicture("Menu", "멀티", "방 설정", "조종 권한 켜기") { State = MultiMenuRules.Host, Caption = "방장의 멀티 메뉴 (방을 만든 뒤에 보입니다)" },
                ["(방장) '멀티 > 방 설정 > 조종 권한 켜기'를 누릅니다.", "(참가자) 게임 화면을 한 번 누른 뒤 키보드로 조작합니다.", "선택지가 나오면 잠시 조작이 멈추고 오른쪽 투표 상자에서 투표합니다. 고르는 것은 방장입니다."],
                null, "조종 권한은 방장에게만 보입니다. 참가자 메뉴에는 없습니다."),
            new("multi-leave", "방 나가기 / 없애기", "방에서 나오거나(참가자) 방을 없앱니다(방장). 방장과 참가자의 메뉴가 다릅니다.",
                new TutorialPicture("Menu", "멀티", "방 나가기") { State = MultiMenuRules.Guest, Caption = "참가자: 방 나가기" },
                ["(참가자) '멀티 > 방 나가기'를 누릅니다.", "(방장) '멀티 > 방 해체하기'를 누르면 모두 나가게 됩니다.", "방장을 넘기려면 '멀티 > 참가자'에서 사람을 고르고 '방장 넘기기'를 누릅니다."],
                More: [
                    new TutorialPicture("Menu", "멀티", "방 해체하기") { State = MultiMenuRules.Host, Caption = "방장: 방 해체하기" },
                    new TutorialPicture("Menu", "멀티", "참가자", MultiMenuRules.SampleFriend, "방장 넘기기") { State = MultiMenuRules.Host, Caption = "방장: 방장 넘기기" },
                ]),
        ]),
        new("채팅과 핑", [
            new("chat", "채팅 치기", "글을 쓰면 게임 화면 위로 흘러갑니다 (이름 없이). 누가 썼는지는 채팅 기록에서 봅니다.",
                new TutorialPicture("Window", "chat", "보내기", "고정"),
                ["{key:MultiChat}를 누르거나 '멀티 > 채팅하기...'를 누릅니다.", "글을 쓰고 Enter를 누릅니다.", "글자색을 고를 수 있습니다 (빨강 + 연한 색 4가지). 흰색이 아닌 색은 30초에 한 번 바꿀 수 있습니다.", "'고정'을 켜면 글이 흐르지 않고 위쪽 한 줄에 6초 머뭅니다 (90초에 한 번)."],
                new TutorialPicture("Chat")),
            new("ping", "핑 찍기", "가리키고 싶은 곳을 화면에 표시합니다. 사람마다 색이 다릅니다.",
                new TutorialPicture("Ping"),
                ["방에 있을 때 게임 화면에서 마우스 가운데 단추(휠)를 누릅니다.", "누른 자리에 내 색으로 표시가 4초 동안 나타납니다.", "맵에서는 화면이 움직여도 그 자리를 따라갑니다."],
                null, "'멀티 > 방 설정 > 방에 오는 핑 안 보기'로 다른 사람 핑을 숨길 수 있습니다."),
        ]),
        new("메시지와 선택지", [
            new("bar", "메시지 바", "대사가 나오면 화면 아래에 기록·스킵·자동 단추가 나옵니다.",
                new TutorialPicture("Bar"),
                ["'기록'은 지나간 대사를 봅니다.", "'스킵'은 대사를 빠르게 넘깁니다 ({key:ToggleSkipMessage}).", "'자동'은 대사를 자동으로 넘기고, '속도'로 빠르기를 정합니다 ({key:ToggleAutoMessage}).", "'Q.저장/Q.불러오기'는 1번 칸 빠른 저장·불러오기입니다."],
                null, "멀티 참가자는 방장이 조종 권한을 켜야 '기록' 말고 다른 단추를 쓸 수 있습니다."),
            new("vote", "선택지 투표 (멀티)", "방장 게임에 선택지가 나오면 참가자는 원하는 것에 투표합니다.",
                new TutorialPicture("Vote"),
                ["(참가자) 화면 오른쪽 투표 상자에서 원하는 줄을 누릅니다. 다시 누르면 바꿀 수 있습니다.", "(방장) 줄마다 몇 표인지와 투표한 사람 색이 보입니다.", "방장이 게임에서 직접 고르면 투표가 끝납니다."]),
        ]),
        new("노트", [
            new("notes", "게임 노트", "게임마다 메모·글상자·스크린샷을 붙여 두는 노트입니다.",
                new TutorialPicture("Notes"),
                ["{key:ToggleNotes}를 누르거나 '도구 > 노트'를 누릅니다.", "'메모'를 누르고 노트를 눌러 메모를 놓은 뒤, 두 번 눌러 글을 씁니다.", "끌어서 옮기고, 휠로 확대·축소합니다.", "{key:ScreenshotToNote}로 지금 화면을 노트에 바로 넣습니다."],
                null, "노트는 자동으로 저장됩니다. 멀티 방에서는 방장 노트를 함께 봅니다."),
        ]),
        new("설정과 창", [
            new("hotkeys", "단축키 바꾸기", "기능마다 원하는 키를 정합니다. 메뉴 오른쪽에 지금 키가 늘 보입니다.",
                new TutorialPicture("Window", "hotkeys", "새 단축키 입력", "할당", "확인"),
                ["'설정 > 단축키 설정...'을 누릅니다.", "목록에서 바꿀 기능을 고르고, '새 단축키 입력' 칸을 누른 뒤 새 키를 누릅니다.", "'할당'을 누르고 '확인'을 누릅니다.", "Alt+Tab처럼 Windows가 쓰는 키는 지정할 수 없습니다. '기본값으로 복원'을 누르면 처음 키로 돌아갑니다."]),
            new("menu-hide", "메뉴 숨기기", "게임 화면을 넓게 쓰고 싶을 때 메뉴 줄을 숨깁니다.",
                new TutorialPicture("Menu", "설정", "메뉴 숨기기"),
                ["{key:ToggleMenuBar}를 누르거나 '설정 > 메뉴 숨기기'를 누릅니다.", "다시 보려면 {key:ToggleMenuBar}를 한 번 더 누릅니다."]),
            new("assets", "에셋 보기", "게임의 그림·소리·글꼴을 분류해서 둘러봅니다.",
                new TutorialPicture("Menu", "도구", "에셋 보기"),
                ["게임을 켠 채 '도구 > 에셋 보기...'를 누릅니다.", "왼쪽에서 폴더를, 위에서 종류를 고르거나 이름으로 찾습니다.", "그림은 오른쪽에 크게 보이고, 소리·영상은 '재생'을 누릅니다."],
                null, "에셋 보기는 이 PC 안에서만 보는 기능입니다. 저장·내보내기는 할 수 없고 방송·화면 공유에도 찍히지 않습니다."),
        ]),
        new("개인정보와 공개 범위", [
            new("privacy", "무엇이 다른 사람에게 보이나요?", "멀티를 쓸 때 알아 두면 좋은 것들입니다.",
                null,
                ["방에서는 내 이름, 채팅, 핑이 같은 방 사람에게 보입니다.",
                 "방장 게임 화면과 소리, 방 노트는 같은 방 사람에게 바로(서버를 거치지 않고) 전달됩니다.",
                 "바로 연결하기 위해 같은 방 사람에게 내 네트워크 주소(공유기 안 주소, 가상 LAN 주소 포함)가 전달됩니다. 서버에는 저장하지 않습니다.",
                 "맵 뷰어·에셋 보기·스위치/변수 관리자의 내용은 다른 사람에게 보내지 않습니다 (조종 권한이 있으면 변수 관리자만 방장 게임에 요청).",
                 "스트리머 모드를 켜면 방 제목·코드가 화면에서 숨겨지고 도구가 꺼집니다."]),
        ]),
    ];

    /// <summary>모든 쪽 (검색·첫 실행 안내용)</summary>
    public static IEnumerable<TutorialPage> AllPages => Sections.SelectMany(s => s.Pages);
}
