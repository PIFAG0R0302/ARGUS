using Argus.Core.Configuration;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;

namespace Argus.App.Themes;

public sealed record ArgusThemeDefinition(
    string Id,
    string Name,
    string CategoryLabel,
    bool IsLight,
    string Canvas,
    string Rail,
    string Panel,
    string Card,
    string Border,
    string BorderBright,
    string InputBg,
    string HoverBg,
    string SelectedBg,
    string TextPrimary,
    string TextSecondary,
    string TextMuted,
    string PrimaryAccent,
    string PrimarySoft,
    string SecondaryAccent,
    string OkColor,
    string WarnColor,
    string CritColor,
    string PillOkBg,
    string PillWarnBg,
    string PillDangerBg,
    string ChartPlotBg,
    string ChartGrid,
    string PrimaryBtnBg,
    string PrimaryBtnHoverBg,
    string BackdropStop0,
    string BackdropStop1,
    string BackdropStop2);

public static class ArgusThemeManager
{
    private static ResourceDictionary? _activeThemeDictionary;

    public static IReadOnlyList<ArgusThemeDefinition> Themes { get; } =
    [
        new(
            Id: "deep-observatory",
            Name: "Deep Observatory",
            CategoryLabel: "Темна · Базова",
            IsLight: false,
            Canvas: "#060916",
            Rail: "#0B1026",
            Panel: "#111735",
            Card: "#171F45",
            Border: "#242E63",
            BorderBright: "#3A478F",
            InputBg: "#0A0F24",
            HoverBg: "#18204A",
            SelectedBg: "#1C2152",
            TextPrimary: "#EDF1FF",
            TextSecondary: "#95A0CC",
            TextMuted: "#5E6A96",
            PrimaryAccent: "#7C5CFF",
            PrimarySoft: "#A48BFF",
            SecondaryAccent: "#38D9F5",
            OkColor: "#2DD4A8",
            WarnColor: "#F5A623",
            CritColor: "#F45B69",
            PillOkBg: "#0E2E2B",
            PillWarnBg: "#2E220D",
            PillDangerBg: "#31131D",
            ChartPlotBg: "#080D20",
            ChartGrid: "#162048",
            PrimaryBtnBg: "#5E42E8",
            PrimaryBtnHoverBg: "#7358FA",
            BackdropStop0: "#060916",
            BackdropStop1: "#0B1028",
            BackdropStop2: "#120E2E"),

        new(
            Id: "arctic-lab",
            Name: "Arctic Lab",
            CategoryLabel: "Світла",
            IsLight: true,
            Canvas: "#F1F5F9",
            Rail: "#E2E8F0",
            Panel: "#FFFFFF",
            Card: "#F8FAFC",
            Border: "#CBD5E1",
            BorderBright: "#94A3B8",
            InputBg: "#FFFFFF",
            HoverBg: "#E2E8F0",
            SelectedBg: "#E0F2FE",
            TextPrimary: "#0F172A",
            TextSecondary: "#475569",
            TextMuted: "#64748B",
            PrimaryAccent: "#0284C7",
            PrimarySoft: "#0369A1",
            SecondaryAccent: "#4F46E5",
            OkColor: "#059669",
            WarnColor: "#D97706",
            CritColor: "#DC2626",
            PillOkBg: "#DCFCE7",
            PillWarnBg: "#FEF3C7",
            PillDangerBg: "#FEE2E2",
            ChartPlotBg: "#F8FAFC",
            ChartGrid: "#E2E8F0",
            PrimaryBtnBg: "#0284C7",
            PrimaryBtnHoverBg: "#0369A1",
            BackdropStop0: "#F1F5F9",
            BackdropStop1: "#EDF2F7",
            BackdropStop2: "#E2E8F0"),

        new(
            Id: "daylight-paper",
            Name: "Daylight Paper",
            CategoryLabel: "Світла · Тепла",
            IsLight: true,
            Canvas: "#F4F2EE",
            Rail: "#EAE6DF",
            Panel: "#FCFBF9",
            Card: "#F5F3EF",
            Border: "#D6D0C4",
            BorderBright: "#B5ACA0",
            InputBg: "#FFFFFF",
            HoverBg: "#E8E3DA",
            SelectedBg: "#CCFBF1",
            TextPrimary: "#1C1917",
            TextSecondary: "#57534E",
            TextMuted: "#78716C",
            PrimaryAccent: "#0D9488",
            PrimarySoft: "#0F766E",
            SecondaryAccent: "#D97706",
            OkColor: "#16A34A",
            WarnColor: "#CA8A04",
            CritColor: "#DC2626",
            PillOkBg: "#DCFCE7",
            PillWarnBg: "#FEF3C7",
            PillDangerBg: "#FEE2E2",
            ChartPlotBg: "#F7F5F0",
            ChartGrid: "#E7E2D8",
            PrimaryBtnBg: "#0D9488",
            PrimaryBtnHoverBg: "#0F766E",
            BackdropStop0: "#F4F2EE",
            BackdropStop1: "#EFECE6",
            BackdropStop2: "#EAE6DF"),

        new(
            Id: "frost-quartz",
            Name: "Frost Quartz",
            CategoryLabel: "Світла · Argus",
            IsLight: true,
            Canvas: "#EEF0F8",
            Rail: "#E4E7F4",
            Panel: "#FAFBFD",
            Card: "#F1F3FA",
            Border: "#CBD2EB",
            BorderBright: "#9EA9D6",
            InputBg: "#FFFFFF",
            HoverBg: "#E2E6F5",
            SelectedBg: "#EDE9FE",
            TextPrimary: "#14182E",
            TextSecondary: "#4B5478",
            TextMuted: "#6D769C",
            PrimaryAccent: "#6D28D9",
            PrimarySoft: "#5B21B6",
            SecondaryAccent: "#0891B2",
            OkColor: "#0D9488",
            WarnColor: "#D97706",
            CritColor: "#E11D48",
            PillOkBg: "#CCFBF1",
            PillWarnBg: "#FEF3C7",
            PillDangerBg: "#FFE4E6",
            ChartPlotBg: "#F4F6FC",
            ChartGrid: "#E1E5F2",
            PrimaryBtnBg: "#6D28D9",
            PrimaryBtnHoverBg: "#5B21B6",
            BackdropStop0: "#EEF0F8",
            BackdropStop1: "#E9ECF7",
            BackdropStop2: "#E4E7F4"),

        new(
            Id: "carbon-graphite",
            Name: "Carbon Graphite",
            CategoryLabel: "Темна · Графіт",
            IsLight: false,
            Canvas: "#121316",
            Rail: "#17191D",
            Panel: "#1D2026",
            Card: "#242830",
            Border: "#323743",
            BorderBright: "#4B5263",
            InputBg: "#14161A",
            HoverBg: "#282D36",
            SelectedBg: "#1E293B",
            TextPrimary: "#F3F4F6",
            TextSecondary: "#9CA3AF",
            TextMuted: "#6B7280",
            PrimaryAccent: "#3B82F6",
            PrimarySoft: "#60A5FA",
            SecondaryAccent: "#22D3EE",
            OkColor: "#10B981",
            WarnColor: "#F59E0B",
            CritColor: "#EF4444",
            PillOkBg: "#0D2B24",
            PillWarnBg: "#2E210A",
            PillDangerBg: "#331418",
            ChartPlotBg: "#15171C",
            ChartGrid: "#2A2F3A",
            PrimaryBtnBg: "#2563EB",
            PrimaryBtnHoverBg: "#3B82F6",
            BackdropStop0: "#121316",
            BackdropStop1: "#16181C",
            BackdropStop2: "#1A1D23"),

        new(
            Id: "emerald-telemetry",
            Name: "Emerald Telemetry",
            CategoryLabel: "Темна · Консоль",
            IsLight: false,
            Canvas: "#07100E",
            Rail: "#0B1714",
            Panel: "#10211D",
            Card: "#152B26",
            Border: "#21423A",
            BorderBright: "#2F6155",
            InputBg: "#091411",
            HoverBg: "#17332C",
            SelectedBg: "#14332B",
            TextPrimary: "#E6F4F1",
            TextSecondary: "#8FB8AE",
            TextMuted: "#568076",
            PrimaryAccent: "#10B981",
            PrimarySoft: "#34D399",
            SecondaryAccent: "#38BDF8",
            OkColor: "#34D399",
            WarnColor: "#FBBF24",
            CritColor: "#FB7185",
            PillOkBg: "#0E3026",
            PillWarnBg: "#2E240D",
            PillDangerBg: "#33141D",
            ChartPlotBg: "#091512",
            ChartGrid: "#1B352F",
            PrimaryBtnBg: "#059669",
            PrimaryBtnHoverBg: "#10B981",
            BackdropStop0: "#07100E",
            BackdropStop1: "#0B1815",
            BackdropStop2: "#0F211D"),

        new(
            Id: "amber-command",
            Name: "Amber Command",
            CategoryLabel: "Темна · Термінал",
            IsLight: false,
            Canvas: "#11100E",
            Rail: "#171512",
            Panel: "#1F1C18",
            Card: "#28241E",
            Border: "#3D362C",
            BorderBright: "#594F40",
            InputBg: "#141210",
            HoverBg: "#302A23",
            SelectedBg: "#2E2516",
            TextPrimary: "#F5EFE6",
            TextSecondary: "#B8AD9E",
            TextMuted: "#7D7366",
            PrimaryAccent: "#F59E0B",
            PrimarySoft: "#FBBF24",
            SecondaryAccent: "#FB7185",
            OkColor: "#22C55E",
            WarnColor: "#FBBF24",
            CritColor: "#EF4444",
            PillOkBg: "#112E1C",
            PillWarnBg: "#33250B",
            PillDangerBg: "#361414",
            ChartPlotBg: "#161411",
            ChartGrid: "#2E2922",
            PrimaryBtnBg: "#D97706",
            PrimaryBtnHoverBg: "#F59E0B",
            BackdropStop0: "#11100E",
            BackdropStop1: "#171512",
            BackdropStop2: "#1E1B16"),

        new(
            Id: "midnight-oled",
            Name: "Midnight OLED",
            CategoryLabel: "Ультра-темна",
            IsLight: false,
            Canvas: "#000000",
            Rail: "#08090C",
            Panel: "#0F1117",
            Card: "#161922",
            Border: "#272C3B",
            BorderBright: "#3D455C",
            InputBg: "#08090D",
            HoverBg: "#1A1E2A",
            SelectedBg: "#1F1633",
            TextPrimary: "#FFFFFF",
            TextSecondary: "#A1A9C0",
            TextMuted: "#646C82",
            PrimaryAccent: "#A855F7",
            PrimarySoft: "#C084FC",
            SecondaryAccent: "#2DD4BF",
            OkColor: "#22C55E",
            WarnColor: "#F59E0B",
            CritColor: "#F43F5E",
            PillOkBg: "#0B2918",
            PillWarnBg: "#2E2009",
            PillDangerBg: "#330E18",
            ChartPlotBg: "#06070A",
            ChartGrid: "#1B1E29",
            PrimaryBtnBg: "#9333EA",
            PrimaryBtnHoverBg: "#A855F7",
            BackdropStop0: "#000000",
            BackdropStop1: "#05060A",
            BackdropStop2: "#0B0914"),

        new(
            Id: "nord-slate",
            Name: "Nord Slate",
            CategoryLabel: "Приглушена",
            IsLight: false,
            Canvas: "#1E2430",
            Rail: "#232A38",
            Panel: "#2A3242",
            Card: "#323C4F",
            Border: "#434E66",
            BorderBright: "#586685",
            InputBg: "#222936",
            HoverBg: "#384359",
            SelectedBg: "#364156",
            TextPrimary: "#ECEFF4",
            TextSecondary: "#B4C0D4",
            TextMuted: "#7E8BA3",
            PrimaryAccent: "#88C0D0",
            PrimarySoft: "#A3D4E3",
            SecondaryAccent: "#81A1C1",
            OkColor: "#A3BE8C",
            WarnColor: "#EBCB8B",
            CritColor: "#BF616A",
            PillOkBg: "#27362A",
            PillWarnBg: "#3B3424",
            PillDangerBg: "#3B252A",
            ChartPlotBg: "#222836",
            ChartGrid: "#384257",
            PrimaryBtnBg: "#5E81AC",
            PrimaryBtnHoverBg: "#81A1C1",
            BackdropStop0: "#1E2430",
            BackdropStop1: "#232A38",
            BackdropStop2: "#283040")
    ];

    public static ArgusThemeDefinition CurrentTheme { get; private set; } = Themes[0];

    public static event EventHandler<ArgusThemeDefinition>? ThemeChanged;

    public static ArgusThemeDefinition FindById(string? themeId)
    {
        if (string.IsNullOrWhiteSpace(themeId))
        {
            return Themes[0];
        }

        string normalized = themeId.Trim();
        return Themes.FirstOrDefault(t => t.Id.Equals(normalized, StringComparison.OrdinalIgnoreCase))
            ?? Themes[0];
    }

    public static ArgusThemeDefinition ApplyTheme(string? themeId)
    {
        ArgusThemeDefinition def = FindById(themeId);
        CurrentTheme = def;

        if (Application.Current is { } app)
        {
            ResourceDictionary dict = BuildResourceDictionary(def);
            if (_activeThemeDictionary is not null)
            {
                app.Resources.MergedDictionaries.Remove(_activeThemeDictionary);
            }

            app.Resources.MergedDictionaries.Add(dict);
            _activeThemeDictionary = dict;
            app.RequestedThemeVariant = def.IsLight ? ThemeVariant.Light : ThemeVariant.Dark;
        }

        ThemeChanged?.Invoke(null, def);
        return def;
    }

    public static ResourceDictionary BuildResourceDictionary(ArgusThemeDefinition def)
    {
        Color prim = Color.Parse(def.PrimaryAccent);
        Color sec = Color.Parse(def.SecondaryAccent);
        Color border = Color.Parse(def.Border);

        var dict = new ResourceDictionary
        {
            // Named Color tokens
            ["ArgusObsidianBlue"] = Color.Parse(def.Canvas),
            ["ArgusAbyssIndigo"] = Color.Parse(def.Rail),
            ["ArgusNebulaSlate"] = Color.Parse(def.Panel),
            ["ArgusNebulaElevated"] = Color.Parse(def.Card),
            ["ArgusTwilightEdge"] = border,
            ["ArgusTwilightBright"] = Color.Parse(def.BorderBright),
            ["ArgusIonViolet"] = prim,
            ["ArgusIonVioletSoft"] = Color.Parse(def.PrimarySoft),
            ["ArgusTelemetryCyan"] = sec,
            ["ArgusSignalEmerald"] = Color.Parse(def.OkColor),
            ["ArgusSolarAmber"] = Color.Parse(def.WarnColor),
            ["ArgusThermalCoral"] = Color.Parse(def.CritColor),
            ["ArgusStarlight"] = Color.Parse(def.TextPrimary),
            ["ArgusMistBlue"] = Color.Parse(def.TextSecondary),
            ["ArgusDimSlate"] = Color.Parse(def.TextMuted),

            // Core Surface & Text Brushes
            ["ArgusBgCanvasBrush"] = new SolidColorBrush(Color.Parse(def.Canvas)),
            ["ArgusBgRailBrush"] = new SolidColorBrush(Color.Parse(def.Rail)),
            ["ArgusBgPanelBrush"] = new SolidColorBrush(Color.Parse(def.Panel)),
            ["ArgusBgCardBrush"] = new SolidColorBrush(Color.Parse(def.Card)),
            ["ArgusBorderBrush"] = new SolidColorBrush(border),
            ["ArgusBorderBrightBrush"] = new SolidColorBrush(Color.Parse(def.BorderBright)),
            ["ArgusVioletBrush"] = new SolidColorBrush(prim),
            ["ArgusVioletSoftBrush"] = new SolidColorBrush(Color.Parse(def.PrimarySoft)),
            ["ArgusCyanBrush"] = new SolidColorBrush(sec),
            ["ArgusEmeraldBrush"] = new SolidColorBrush(Color.Parse(def.OkColor)),
            ["ArgusAmberBrush"] = new SolidColorBrush(Color.Parse(def.WarnColor)),
            ["ArgusCoralBrush"] = new SolidColorBrush(Color.Parse(def.CritColor)),
            ["ArgusTextPrimaryBrush"] = new SolidColorBrush(Color.Parse(def.TextPrimary)),
            ["ArgusTextSecondaryBrush"] = new SolidColorBrush(Color.Parse(def.TextSecondary)),
            ["ArgusTextMutedBrush"] = new SolidColorBrush(Color.Parse(def.TextMuted)),

            // Interactive & Semantic State Brushes
            ["ArgusBgInputBrush"] = new SolidColorBrush(Color.Parse(def.InputBg)),
            ["ArgusBgHoverBrush"] = new SolidColorBrush(Color.Parse(def.HoverBg)),
            ["ArgusBgSelectedBrush"] = new SolidColorBrush(Color.Parse(def.SelectedBg)),
            ["ArgusBgPillOkBrush"] = new SolidColorBrush(Color.Parse(def.PillOkBg)),
            ["ArgusBgPillWarnBrush"] = new SolidColorBrush(Color.Parse(def.PillWarnBg)),
            ["ArgusBgPillDangerBrush"] = new SolidColorBrush(Color.Parse(def.PillDangerBg)),
            ["ArgusPrimaryButtonBgBrush"] = new SolidColorBrush(Color.Parse(def.PrimaryBtnBg)),
            ["ArgusPrimaryButtonHoverBrush"] = new SolidColorBrush(Color.Parse(def.PrimaryBtnHoverBg)),
            ["ArgusSecondaryButtonBgBrush"] = new SolidColorBrush(Color.Parse(def.Card)),
            ["ArgusSecondaryButtonHoverBrush"] = new SolidColorBrush(Color.Parse(def.HoverBg)),

            // Signature Gradients
            ["ArgusWorkspaceBackdrop"] = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops =
                [
                    new GradientStop(Color.Parse(def.BackdropStop0), 0),
                    new GradientStop(Color.Parse(def.BackdropStop1), 0.58),
                    new GradientStop(Color.Parse(def.BackdropStop2), 1)
                ]
            },
            ["ArgusAccentLineBrush"] = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
                GradientStops =
                [
                    new GradientStop(sec, 0),
                    new GradientStop(prim, 0.55),
                    new GradientStop(border, 1)
                ]
            },
            ["ArgusHeroCardBorderBrush"] = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops =
                [
                    new GradientStop(Color.FromArgb(0x52, sec.R, sec.G, sec.B), 0),
                    new GradientStop(Color.FromArgb(0x66, prim.R, prim.G, prim.B), 0.45),
                    new GradientStop(border, 1)
                ]
            }
        };

        return dict;
    }
}
