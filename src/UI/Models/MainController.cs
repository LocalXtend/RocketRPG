#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace RocketRPG.Models;

/// <summary>
/// 전역 설정·게임 감지·실행 기록을 맡습니다. 게임 실행은 엔진별 렌더러(WebView2 / mkxp-z / EasyRPG)가 합니다.
/// (예전의 클래식 모드 — 원본 실행 파일을 띄우고 화면을 캡처 — 는 0.8.0에서 없앴습니다.)
/// </summary>
public class MainController : IDisposable
{
    public CoreInterop.GameInfo Current;
    public double Gamma { get; private set; } = 1.0;

    readonly SettingsService _settingsSvc = new();
    public GlobalSettings Settings;

    public MainController()
    {
        Settings = _settingsSvc.Load();
        MkxpFontCatalog.MissingFontFallback = Settings.MissingFontFallback ?? "";
        RtpResolver.UserPaths = new(Settings.RtpPaths ?? new(), StringComparer.OrdinalIgnoreCase);
        CoreInterop.rpg_core_init(SettingsService.Root());
        Gamma = Math.Clamp(Settings.Gamma, 0.05, 4.0);
    }

    public List<(string dir, string title, int engine)> ScanAll2(string root)
    {
        var list = new List<(string dir, string title, int engine)>();
        if (CoreInterop.rpg_scan_games(root, out var p, out var n) == 0 && n > 0)
        {
            for (int i = 0; i < n; i++)
            {
                var dirPtr = Marshal.ReadIntPtr(p, i * IntPtr.Size);
                var dir = Marshal.PtrToStringUni(dirPtr) ?? "";
                var gi = CoreInterop.GameInfo.Create();
                if (CoreInterop.rpg_detect_game(dir, ref gi) == 0)
                    list.Add((dir, string.IsNullOrWhiteSpace(gi.TitleUtf8) ? Path.GetFileName(dir) : gi.TitleUtf8, gi.Engine));
            }
            CoreInterop.rpg_free_strings(p, n);
        }
        return list;
    }

    public void PushHistory(string dir) => _settingsSvc.PushHistory(Settings, dir);
    public void ClearHistory() => _settingsSvc.ClearHistory(Settings);

    /// <summary>밝기 값을 저장합니다 (실제 적용은 실행 중인 엔진의 SetBrightness).</summary>
    public void ApplyGamma(double g) { Gamma = Math.Clamp(g, 0.05, 4.0); Settings.Gamma = Gamma; _settingsSvc.Save(Settings); }

    /// <summary>볼륨 값을 저장합니다 (실제 적용은 실행 중인 엔진의 SetVolume).</summary>
    public void SetVolume(int pct) { Settings.Volume = pct; _settingsSvc.Save(Settings); }

    /// <summary>전역 설정을 저장합니다 (실패해도 예외를 내지 않음). 게임별 설정은 GameSettingsService(rocket_config.json)가 따로 씁니다.</summary>
    public void SaveSettings() => _settingsSvc.Save(Settings);

    public void Dispose()
    {
        CoreInterop.rpg_core_shutdown();
    }
}
