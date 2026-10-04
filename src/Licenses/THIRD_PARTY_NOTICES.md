# RocketRPG 라이선스와 고지 (Licenses and notices)

## RocketRPG 자체
RocketRPG 자체 코드는 **MIT 라이선스**입니다 (`texts/RocketRPG-MIT.txt`).
MIT는 RocketRPG가 직접 만든 코드에만 적용되며, 함께 배포하는 아래 구성요소에는 **각자의 라이선스**가 적용됩니다.

## 함께 배포하는 구성요소
| 구성요소 | 라이선스 | 소스 | 비고 |
|---|---|---|---|
| EasyRPG Player (RocketRPG 수정본, 0.8.1.1 기반) | GPL-3.0-or-later | https://github.com/LocalXtend/EasyRPG_Patch | 수정함. 릴리즈마다 같은 버전 태그와 소스 압축(`RocketRPG-<버전>-easyrpg-source.zip`)을 함께 올립니다 |
| liblcf 0.8.1, inih r58 | MIT, BSD-3-Clause | https://github.com/EasyRPG/liblcf · https://github.com/benhoyt/inih | EasyRPG Player 안에 정적 링크 |
| mkxp-z 2.4.2 (커밋 a5d5749) | GPL-2.0-or-later, OpenSSL 3 포함 빌드라 **GPL-3.0**으로 배포 | https://github.com/mkxp-z/mkxp-z/tree/a5d574984c68a7a2692fb9b212871f549af80874 | 수정하지 않음. SDL2, SDL_sound, OpenAL Soft(LGPL), PhysFS, Ogg/Vorbis, FreeType, pixman, OpenSSL 3.3.1(Apache-2.0) 포함 |
| Ruby 3.0.0p0 / 3.1.3p185 | Ruby License / BSD-2-Clause | https://github.com/ruby/ruby | mkxp-z용 |
| MSYS2 UCRT64 라이브러리 (SDL2/SDL3, FluidSynth, libsndfile, mpg123, LAME, GLib, libiconv/libintl, Readline, ncurses, GCC 런타임, winpthreads, FreeType, HarfBuzz, Graphite2, ICU, PCRE2, pixman, libpng, fmt, Ogg/Vorbis/Opus/FLAC, SpeexDSP, libxmp, PortAudio, Brotli, bzip2, Expat, zlib) | 각 라이브러리 라이선스 (Zlib, LGPL-2.1+, GPL-3.0+(Readline, GCC 런타임 예외), MIT, BSD, FTL, Unicode 등) | https://packages.msys2.org/ | EasyRPG Player가 쓰는 DLL. 동적 링크라 같은 이름의 DLL로 바꿔 끼울 수 있습니다 |
| GeneralUser GS 1.471 (사운드폰트) | GeneralUser GS License v2.0 | https://schristiancollins.com/generaluser.php | MIDI 소리 |
| 나눔고딕 | SIL Open Font License 1.1 | https://hangeul.naver.com/font | XP/VX/Ace 기본 글꼴 대체용 |
| fd | MIT / Apache-2.0 | https://github.com/sharkdp/fd | 게임 폴더 찾기 |
| mkxp-z 호환 스크립트 (win32_wrap, mkxp_wrap, ruby_classic_wrap) | CC0-1.0 | https://github.com/mkxp-z/mkxp-z | RocketRPG.exe 안. 일부 수정 |
| .NET 런타임 / WPF | MIT (+ ThirdPartyNotices) | https://github.com/dotnet/runtime | RocketRPG.exe 안 |
| Microsoft Edge WebView2 SDK 1.0.4191.47 | Microsoft WebView2 SDK License | https://www.nuget.org/packages/Microsoft.Web.WebView2 | WebView2 런타임은 Windows에 설치된 것을 씀 |
| DiscordRichPresence 1.6.1.70, Newtonsoft.Json 13.0.1 | MIT | https://github.com/Lachee/discord-rpc-csharp · https://www.newtonsoft.com/json | RocketRPG.exe 안 |

라이선스 원문은 모두 `licenses/texts/` 폴더에 있고, RocketRPG의 **정보 > RocketRPG 정보**에서 인터넷 없이 열어 볼 수 있습니다.
구성요소 목록은 `licenses/components.json`에도 있습니다.

### GPL 구성요소의 소스 받기
- **EasyRPG Player 수정본**: https://github.com/LocalXtend/EasyRPG_Patch — RocketRPG 버전과 같은 태그(예: `v1.0.0`). 같은 소스를 각 RocketRPG 릴리즈에 압축 파일로도 첨부합니다. 빌드 방법은 저장소의 `rocketrpg/README.md`에 있습니다.
- **mkxp-z**: 위 커밋의 원본 소스. RocketRPG는 mkxp-z를 수정하지 않았습니다.
- 실행 파일을 내려받을 수 있는 동안 같은 곳(위 저장소와 각 릴리즈)에서 그 실행 파일의 소스도 계속 받을 수 있게 둡니다. 소스를 받을 수 없으면 저장소 이슈로 알려 주세요.

## RPG Maker와 게임 파일
- RocketRPG는 **비공식 호환 프로그램**입니다. "RPG Maker"(RPG 쯔꾸르)는 그 권리자(KADOKAWA / Gotcha Gotcha Games)의 상표이며, RocketRPG는 이 회사들과 관계가 없고 승인이나 후원을 받지 않았습니다. 이름은 어떤 게임과 호환되는지 설명하는 데에만 씁니다.
- RocketRPG는 **게임 파일, RTP(런타임 패키지), RPG Maker 원본 실행 파일·DLL을 포함하지 않습니다.** 사용자가 정당하게 가진 게임과, 각자 공식 경로로 설치한 RTP를 그대로 읽어 실행합니다.
- 게임의 그림·소리·스크립트·플러그인의 권리는 각 게임 제작자와 원래 권리자에게 있습니다. 에셋 보기는 이 PC 안에서 보기만 하는 기능이며 저장·내보내기·전송 기능이 없습니다. 게임 파일을 다른 곳에 올리거나 나눌 때는 각 권리자의 조건을 따라 주세요.
- RTP는 공식 사이트(https://www.rpgmakerweb.com/run-time-package)와 각 제품의 약관에 따라 받아 주세요.

## 보증 없음
RocketRPG와 함께 배포하는 구성요소는 각 라이선스에 적힌 대로 **아무 보증 없이** 제공됩니다.
