#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using RocketRPG.Models;

namespace RocketRPG.Views;

public partial class FontSettingsWindow : Window
{
    private readonly MainController _ctl;
    private readonly Action<string> _applyFontCallback;
    private readonly List<FontFamily> _allFonts;
    public const string DefaultFontName = "맑은 고딕, Segoe UI, sans-serif";
    public const string DefaultInGameFontName = "Malgun Gothic";

    // 인게임 글꼴·크기·굵기는 게임별 설정이라, 게임이 꺼져 있으면 바꿀 대상이 없습니다 (전체 설정인 '게임 글꼴이 없을 때'만 가능).
    private readonly bool _hasGame;
    // 2000/2003(EasyRPG)은 게임 화면(320x240)에 12px 흑백으로 그린 뒤 확대하므로, 미리보기도 그 모양으로 보여 줍니다.
    private readonly bool _isRpg2k;

    // 설치된 글꼴 목록은 처음 한 번만 만들고 다시 씁니다 (창을 열 때마다 400여 개를 정렬하던 것이 느렸음).
    static List<FontFamily>? _fontCache;

    public FontSettingsWindow(MainController ctl, Action<string> applyFontCallback, int initialTabIndex = 0, bool hasGame = true, int engine = 0)
    {
        InitializeComponent();
        _ctl = ctl;
        _hasGame = hasGame;
        _isRpg2k = hasGame && engine is CoreInterop.Engine2000 or CoreInterop.Engine2003;
        _applyFontCallback = applyFontCallback;
        if (initialTabIndex >= 0 && initialTabIndex < 2)
        {
            FontTabs.SelectedIndex = initialTabIndex;
        }

        _allFonts = _fontCache ??= Fonts.SystemFontFamilies.OrderBy(f => f.Source).ToList();
        _searchTimer.Tick += (_, _) => { _searchTimer.Stop(); ApplyFilter(); ApplyInGameFilter(); };

        ApplyFilter();
        ApplyInGameFilter();

        // 1. UI Font init
        string currentFont = string.IsNullOrWhiteSpace(_ctl.Settings.UiFontFamily)
            ? DefaultFontName
            : _ctl.Settings.UiFontFamily;
        SelectFont(currentFont);

        // 2. In-Game Font init
        bool gameDefault = string.IsNullOrWhiteSpace(_ctl.Settings.InGameFontFamily);
        SelectInGameFont(gameDefault ? DefaultInGameFontName : _ctl.Settings.InGameFontFamily);
        ChkGameDefaultFont.IsChecked = gameDefault;
        UpdateGameDefaultFontState();

        if (_ctl.Settings.InGameFontSize > 0)
        {
            ChkDefaultSize.IsChecked = false;
            SliderFontSize.IsEnabled = true;
            SliderFontSize.Value = _ctl.Settings.InGameFontSize;
            TxtFontSize.Text = $"{_ctl.Settings.InGameFontSize}px";
        }
        else
        {
            ChkDefaultSize.IsChecked = true;
            SliderFontSize.IsEnabled = false;
        }

        ChkInGameBold.IsChecked = _ctl.Settings.InGameFontBold;
        UpdateInGamePreview();

        // 3. 게임 글꼴이 없을 때 대신 쓸 글꼴 (첫 항목 = 자동)
        CmbMissingFallback.ItemsSource = new[] { AutoFallbackLabel }.Concat(_allFonts.Select(f => f.Source)).ToList();
        string fb = _ctl.Settings.MissingFontFallback ?? "";
        CmbMissingFallback.SelectedItem = string.IsNullOrWhiteSpace(fb) ? AutoFallbackLabel
            : (FindMatchingFont(_allFonts, fb)?.Source ?? AutoFallbackLabel);

        if (!_hasGame)
        {
            InGameIntroText.Text = "게임을 실행하면 그 게임의 인게임 글꼴을 설정할 수 있습니다. " +
                                   "(아래 '게임 글꼴이 없을 때'는 모든 게임에 쓰이는 설정이라 지금도 바꿀 수 있습니다)";
            foreach (var c in new UIElement[] { ChkGameDefaultFont, SearchInGameFontBox, BtnClearInGameFontSearch, InGameFontListBox, ChkDefaultSize, SliderFontSize, ChkInGameBold })
                c.IsEnabled = false;
        }
    }

    const string AutoFallbackLabel = "자동 (이름으로 비슷한 글꼴 찾기)";

    public static FontFamily? FindMatchingFont(IEnumerable<FontFamily> fonts, string fontName)
    {
        if (string.IsNullOrWhiteSpace(fontName)) return null;

        var candidates = fontName.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        foreach (var cand in candidates)
        {
            var match = fonts.FirstOrDefault(f =>
                f.Source.Equals(cand, StringComparison.OrdinalIgnoreCase) ||
                f.FamilyNames.Values.Any(v => v.Equals(cand, StringComparison.OrdinalIgnoreCase)));
            if (match != null) return match;
        }

        return null;
    }

    private void SelectFont(string fontName)
    {
        var match = FindMatchingFont(_allFonts, fontName);
        if (match != null)
        {
            FontListBox.SelectedItem = match;
            FontListBox.ScrollIntoView(match);
            PreviewText.FontFamily = match;
        }
        else
        {
            FontListBox.SelectedItem = null;
            PreviewText.FontFamily = new FontFamily(fontName);
        }
    }

    private void SelectInGameFont(string fontName)
    {
        var match = FindMatchingFont(_allFonts, fontName);
        if (match != null)
        {
            InGameFontListBox.SelectedItem = match;
            InGameFontListBox.ScrollIntoView(match);
            InGamePreviewText.FontFamily = match;
        }
        else
        {
            InGameFontListBox.SelectedItem = null;
            InGamePreviewText.FontFamily = new FontFamily(fontName);
        }
    }

    private void ApplyFilter()
    {
        string q = SearchFontBox.Text.Trim();
        if (string.IsNullOrEmpty(q))
        {
            FontListBox.ItemsSource = _allFonts;
        }
        else
        {
            FontListBox.ItemsSource = _allFonts
                .Where(f => f.Source.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                            f.FamilyNames.Values.Any(v => v.Contains(q, StringComparison.OrdinalIgnoreCase)))
                .ToList();
        }
    }

    private void ApplyInGameFilter()
    {
        string q = SearchInGameFontBox.Text.Trim();
        if (string.IsNullOrEmpty(q))
        {
            InGameFontListBox.ItemsSource = _allFonts;
        }
        else
        {
            InGameFontListBox.ItemsSource = _allFonts
                .Where(f => f.Source.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                            f.FamilyNames.Values.Any(v => v.Contains(q, StringComparison.OrdinalIgnoreCase)))
                .ToList();
        }
    }

    // 글자를 칠 때마다 400여 개를 다시 거르지 않고, 입력이 멈추면 한 번 거릅니다.
    readonly System.Windows.Threading.DispatcherTimer _searchTimer = new() { Interval = TimeSpan.FromMilliseconds(180) };

    private void SearchFontBox_TextChanged(object sender, TextChangedEventArgs e) { _searchTimer.Stop(); _searchTimer.Start(); }
    private void BtnClearFontSearch_Click(object sender, RoutedEventArgs e) => SearchFontBox.Text = "";

    private void SearchInGameFontBox_TextChanged(object sender, TextChangedEventArgs e) { _searchTimer.Stop(); _searchTimer.Start(); }
    private void BtnClearInGameFontSearch_Click(object sender, RoutedEventArgs e) => SearchInGameFontBox.Text = "";

    private void FontListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FontListBox.SelectedItem is FontFamily selected)
        {
            PreviewText.FontFamily = selected;
        }
    }

    private void InGameFontListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (InGameFontListBox.SelectedItem is FontFamily selected)
        {
            InGamePreviewText.FontFamily = selected;
        }
        UpdateEasyRpgPreview();
    }

    private void ChkGameDefaultFont_Changed(object sender, RoutedEventArgs e)
    {
        UpdateGameDefaultFontState();
        UpdateEasyRpgPreview();
    }

    /// <summary>
    /// EasyRPG처럼 px 크기로 샘플 문장을 1배 크기로 그립니다. 글자가 절반 이상 덮은 픽셀만 칠합니다 (FreeType 1비트 렌더와 같은 결과).
    /// WPF는 비트맵에 그릴 때 Aliased 글자 모드를 무시하므로 덮임 정도(알파)를 직접 픽셀로 바꿉니다.
    /// </summary>
    public static System.Windows.Media.Imaging.BitmapSource RenderEasyRpgSample(FontFamily fam, int px, bool bold)
    {
        var tf = new Typeface(fam, FontStyles.Normal, bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal);
        var ft = new FormattedText("용사여, 환영합니다! 0123 ABC", System.Globalization.CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight, tf, px, Brushes.White, 1.0);
        int w = (int)Math.Ceiling(ft.WidthIncludingTrailingWhitespace) + 8, h = (int)Math.Ceiling(ft.Height) + 4;
        var dv = new DrawingVisual();
        TextOptions.SetTextFormattingMode(dv, TextFormattingMode.Display);
        using (var dc = dv.RenderOpen()) dc.DrawText(ft, new Point(4, 2));   // 투명 바탕에 흰 글자 → 알파 = 덮임 정도
        var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        var cov = new byte[w * h * 4];
        rtb.CopyPixels(cov, w * 4, 0);
        const byte bgR = 0x18, bgG = 0x28, bgB = 0x48;
        var outPx = new byte[w * h * 4];
        for (int i = 0; i < cov.Length; i += 4)
        {
            int a = cov[i + 3];
            a = a >= 128 ? 255 : 0;
            outPx[i] = (byte)(bgB + (255 - bgB) * a / 255);
            outPx[i + 1] = (byte)(bgG + (255 - bgG) * a / 255);
            outPx[i + 2] = (byte)(bgR + (255 - bgR) * a / 255);
            outPx[i + 3] = 255;
        }
        var bmp = System.Windows.Media.Imaging.BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, outPx, w * 4);
        bmp.Freeze();
        return bmp;
    }

    /// <summary>
    /// 미리보기 하나로 합쳤습니다: 지금 게임이 2000/2003이면 게임 화면처럼 12px 흑백을 픽셀 그대로 확대한 모양,
    /// 그 밖(XP/VX/Ace/MV/MZ, 게임 없음)에는 윈도우 글자 그대로 보여 줍니다.
    /// </summary>
    private void UpdateEasyRpgPreview()
    {
        if (EasyRpgPreview == null || InGamePreviewText == null) return;
        var fam = ChkGameDefaultFont.IsChecked == true ? null : InGameFontListBox.SelectedItem as FontFamily;
        bool pixel = _isRpg2k && fam != null;
        PreviewGroup.Header = pixel ? "미리보기 (2000/2003 게임 화면 모양)" : "미리보기";
        EasyRpgPreview.Visibility = pixel ? Visibility.Visible : Visibility.Collapsed;
        InGamePreviewText.Visibility = pixel ? Visibility.Collapsed : Visibility.Visible;
        if (!pixel || fam == null)
        {
            EasyRpgPreview.Source = null;
            return;
        }
        int px = ChkDefaultSize.IsChecked == true ? 12 : (int)Math.Round(SliderFontSize.Value);
        var rtb = RenderEasyRpgSample(fam, px, ChkInGameBold.IsChecked == true);
        int w = rtb.PixelWidth, h = rtb.PixelHeight;
        double scale = Math.Max(1, Math.Min(3, Math.Floor(440.0 / w)));
        EasyRpgPreview.Source = rtb;
        EasyRpgPreview.Stretch = Stretch.Fill;
        EasyRpgPreview.Width = w * scale;
        EasyRpgPreview.Height = h * scale;
    }

    private void UpdateGameDefaultFontState()
    {
        if (InGameFontListBox == null || SearchInGameFontBox == null) return;
        bool on = ChkGameDefaultFont.IsChecked == true;
        InGameFontListBox.IsEnabled = !on;
        SearchInGameFontBox.IsEnabled = !on;
    }

    private void ChkDefaultSize_Changed(object sender, RoutedEventArgs e)
    {
        if (SliderFontSize == null || TxtFontSize == null) return;
        bool isDefault = ChkDefaultSize.IsChecked == true;
        SliderFontSize.IsEnabled = !isDefault;
        UpdateInGamePreview();
    }

    private void SliderFontSize_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TxtFontSize == null) return;
        int sz = (int)Math.Round(SliderFontSize.Value);
        TxtFontSize.Text = $"{sz}px";
        UpdateInGamePreview();
    }

    private void ChkInGameBold_Changed(object sender, RoutedEventArgs e)
    {
        UpdateInGamePreview();
    }

    private void UpdateInGamePreview()
    {
        if (InGamePreviewText == null) return;
        int sz = (ChkDefaultSize?.IsChecked == true) ? 14 : (int)Math.Round(SliderFontSize?.Value ?? 14);
        InGamePreviewText.FontSize = Math.Clamp(sz, 10, 32);
        InGamePreviewText.FontWeight = (ChkInGameBold?.IsChecked == true) ? FontWeights.Bold : FontWeights.Normal;
        UpdateEasyRpgPreview();
    }

    private void BtnResetDefault_Click(object sender, RoutedEventArgs e)
    {
        if (FontTabs.SelectedIndex == 0)
        {
            SearchFontBox.Text = "";
            SelectFont(DefaultFontName);
            PreviewText.FontFamily = new FontFamily(DefaultFontName);
        }
        else if (!_hasGame)
        {
            CmbMissingFallback.SelectedItem = AutoFallbackLabel;
        }
        else
        {
            SearchInGameFontBox.Text = "";
            SelectInGameFont(DefaultInGameFontName);
            ChkGameDefaultFont.IsChecked = true;
            ChkDefaultSize.IsChecked = true;
            ChkInGameBold.IsChecked = false;
            CmbMissingFallback.SelectedItem = AutoFallbackLabel;
            UpdateInGamePreview();
        }
    }

    private void BtnApply_Click(object sender, RoutedEventArgs e)
    {
        ApplySelectedFont();
    }

    private void BtnOk_Click(object sender, RoutedEventArgs e)
    {
        ApplySelectedFont();
        DialogResult = true;
        Close();
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void ApplySelectedFont()
    {
        // 1. UI Font
        string uiFontName;
        if (FontListBox.SelectedItem is FontFamily selected)
        {
            uiFontName = selected.Source;
        }
        else
        {
            uiFontName = DefaultFontName;
        }
        _applyFontCallback(uiFontName);

        // 2. In-Game Font (게임별 — 게임이 켜져 있을 때만)
        // 빈 값 = 게임 기본 글꼴 (RocketRPG가 글꼴을 바꾸지 않음)
        if (_hasGame)
        {
            string inGameFontName = "";
            if (ChkGameDefaultFont.IsChecked != true)
            {
                inGameFontName = InGameFontListBox.SelectedItem is FontFamily inGameSelected ? inGameSelected.Source : DefaultInGameFontName;
            }
            _ctl.Settings.InGameFontFamily = inGameFontName;
            _ctl.Settings.InGameFontSize = (ChkDefaultSize.IsChecked == true) ? 0 : (int)Math.Round(SliderFontSize.Value);
            _ctl.Settings.InGameFontBold = ChkInGameBold.IsChecked == true;
        }
        string fallback = CmbMissingFallback.SelectedItem is string s && s != AutoFallbackLabel ? s : "";
        _ctl.Settings.MissingFontFallback = fallback;
        MkxpFontCatalog.MissingFontFallback = fallback;
        _ctl.SaveSettings();
    }
}
