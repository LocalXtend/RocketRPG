#nullable enable
using System;

namespace RocketRPG.Models;

/// <summary>
/// 화면 필터 이름과 엔진별 대응. 실제 필터는 각 엔진(WebView2 / mkxp-z / EasyRPG)이 적용합니다.
/// </summary>
public static class RocketShaderSystem
{
    public const string FilterNone = "none";
    public const string FilterXbrzCas = "xbrz_cas";
    public const string FilterFsrCas = "fsr_cas";
    public const string FilterScaleFxCas = "scalefx_cas";
    public const string FilterCrtRoyale = "crt_royale";
    public const string FilterLegacyQuality = "quality";

    /// <summary>
    /// mkxp-z 내장 확대 보간(smoothScaling): 0=최근접, 1=바이리니어, 2=바이큐빅, 3=Lanczos3, 4=xBRZ.
    /// 각 필터를 가장 가까운 내장 보간으로 대응합니다. null이면 필터 없음.
    /// </summary>
    public static int? ToMkxpScaling(string? filterName) => (filterName ?? FilterNone).ToLowerInvariant() switch
    {
        FilterXbrzCas => 4,
        FilterFsrCas => 3,
        FilterScaleFxCas => 2,
        FilterLegacyQuality => 1,
        FilterCrtRoyale => 1,
        _ => null
    };

    public static string GetFilterDisplayName(string? filterName)
    {
        return (filterName ?? "").ToLowerInvariant() switch
        {
            FilterXbrzCas => "도트 확대(xBRZ) + 선명하게",
            FilterFsrCas => "부드러운 확대(Lanczos) + 선명하게",
            FilterScaleFxCas => "부드러운 확대(Bicubic) + 선명하게",
            FilterCrtRoyale => "옛날 TV(CRT) 느낌",
            FilterLegacyQuality => "선명하게",
            _ => "원본(없음)"
        };
    }
}
