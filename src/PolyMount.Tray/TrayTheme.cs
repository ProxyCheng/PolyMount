using System.Globalization;
using System.Text.RegularExpressions;
using QtSharp.Api;

namespace PolyMount.Tray;

/// <summary>What the tray is painted with: one set of named colours per scheme, the style sheet
/// built from them, and which of them is in force.
///
/// The windows used to name their colours where they used them — a style sheet of literals, and a
/// dozen painters with a hex string each — which is a light theme by construction: there is nowhere
/// to say what the same surface is when the lights are off. So the colours live here as tokens. The
/// style sheet carries <c>@token</c> names instead of hex, and a painter asks for
/// <see cref="Ink"/>/<see cref="Current"/> rather than for a colour, which is also what lets a
/// switch reach them: both are read at paint time, so a repaint is all a theme change needs.
///
/// The dark palette is not the light one inverted. Its surfaces go <em>up</em> from the window
/// rather than down (a card that is lighter than the page behind it is what reads as raised when
/// the light comes from the screen), its text keeps a blue cast so it belongs to the surfaces, and
/// the accent is a step brighter, because a colour that carries on white is a colour that recedes
/// on near-black.</summary>
internal static class TrayTheme
{
    /// <summary>Which scheme is in force, and what the reader asked for. <c>System</c> follows the
    /// platform's own choice, which Qt reports and reports again when it changes; the other two are
    /// the reader overruling it.</summary>
    internal enum ThemeMode { System, Light, Dark }

    /// <summary>Every colour the tray paints with. Named for what it is for rather than for what it
    /// looks like, so the two schemes can be read side by side.</summary>
    internal sealed record Palette(
        string AppBackground,      // the window behind everything, and the tab pane
        string Surface,            // cards, tables, inputs, the plot itself
        string SurfaceAlt,         // table headers, a fill one step off the surface
        string SurfaceSunken,      // progress track, the tab bar
        string Border,             // card and input outlines
        string BorderSoft,         // hairline separators inside a surface
        string BorderStrong,       // input outlines, tab outlines
        string Text,               // headings and values
        string Body,               // running text
        string Muted,              // secondary labels, axis labels, captions
        string Faint,              // the emptiest state text
        string Disabled,           // disabled labels
        string DisabledBackground, // disabled buttons
        string Primary,            // the one accent: primary buttons, selection, focus
        string PrimaryHover,
        string PrimaryPressed,
        string PrimarySoft,        // selected row and tab backgrounds
        string PrimaryTint,        // hover fills that should stay quiet
        string PrimaryTintStrong,  // pressed fills
        string PrimaryDisabled,    // a primary button nobody can press
        string OnPrimary,          // text and icons on the accent
        string HoverSoft,          // the whisper of a fill under a hovered control
        string AccentBorder,       // the accent as an outline rather than as a fill
        string ControlBorder,      // a checkbox or an unselected control's outline
        string SplitterHover,      // the grab handle between panes
        string TabIdle,            // a tab that is not the one being read
        string SelectedText,       // the ink inside a selected row, tab or menu item
        string TooltipBackground,  // the bubble, which is inverted in both schemes
        string TooltipText,
        string Sidebar,
        string SidebarText,
        string SidebarMuted,
        string SidebarActive,
        string SidebarBorder,
        string SidebarDisabled,
        string Danger,             // error text
        string DangerStrong,       // error marks and state
        string DangerSoft,         // error background
        string DangerBorder,
        string Success,            // mounted, connected
        string Warning,            // reconnecting
        string Info,               // the quiet notice
        string InfoSoft,
        string InfoBorder,
        string Scrollbar,
        string Grid,               // chart gridlines
        string Icon,               // icon strokes on a surface
        string ChevronLeft,        // the tab bar's overflow arrows and a combo's drop-down arrow:
        string ChevronRight,       // resource paths rather than colours, because a rule that points
        string ChevronDown,        // at a picture cannot be recoloured the way a colour can
        double SeriesLighten,      // how far the layers' own colours move toward the light
        int SeriesFillAlpha);      // how solid an area fill is

    /// <summary>The scheme the tray has always had.</summary>
    private static readonly Palette Light = new(
        AppBackground: "#f3f6fb", Surface: "#ffffff", SurfaceAlt: "#f7f9fc", SurfaceSunken: "#edf1f7",
        Border: "#e2e8f0", BorderSoft: "#eef2f7", BorderStrong: "#dbe3ef",
        Text: "#17243b", Body: "#334155", Muted: "#718096", Faint: "#94a3b8",
        Disabled: "#a3afc0", DisabledBackground: "#f6f8fb",
        Primary: "#2563eb", PrimaryHover: "#1d4ed8", PrimaryPressed: "#1e40af",
        PrimarySoft: "#eaf1ff", PrimaryTint: "#dbeafe", PrimaryTintStrong: "#bfdbfe",
        PrimaryDisabled: "#c9d8f4", OnPrimary: "#ffffff",
        HoverSoft: "#f0f5ff", AccentBorder: "#9db8e6", ControlBorder: "#b8c6d9",
        SplitterHover: "#60a5fa", TabIdle: "#e8edf5",
        SelectedText: "#1e40af", TooltipBackground: "#26354b", TooltipText: "#ffffff",
        Sidebar: "#142237", SidebarText: "#c8d4e6", SidebarMuted: "#93a4bd",
        SidebarActive: "#253d61", SidebarBorder: "#33445d", SidebarDisabled: "#63758d",
        Danger: "#b42335", DangerStrong: "#dc2626", DangerSoft: "#fff1f2", DangerBorder: "#fecdd3",
        Success: "#059669", Warning: "#d97706", Info: "#315ca8", InfoSoft: "#eef4ff", InfoBorder: "#dae6fc",
        Scrollbar: "#cbd5e1", Grid: "#e7edf5", Icon: "#526786",
        ChevronLeft: ":/polymount/chevron-left.png", ChevronRight: ":/polymount/chevron-right.png",
        ChevronDown: ":/polymount/chevron-down.png",
        SeriesLighten: 0.0, SeriesFillAlpha: 155);

    /// <summary>The same page with the lights off: a deep blue-slate window, surfaces that step up
    /// from it, and a softer accent than the light scheme's - the ground a colour sits on decides
    /// how much of it is enough.</summary>
    private static readonly Palette Dark = new(
        AppBackground: "#0e1622", Surface: "#16212f", SurfaceAlt: "#1b2735", SurfaceSunken: "#101a26",
        Border: "#24344a", BorderSoft: "#1e2b3c", BorderStrong: "#2f4358",
        Text: "#e8eef7", Body: "#c3cfdf", Muted: "#8fa0b8", Faint: "#6b7d94",
        Disabled: "#64748b", DisabledBackground: "#17222f",
        Primary: "#3b82f6", PrimaryHover: "#60a5fa", PrimaryPressed: "#2563eb",
        PrimarySoft: "#1d3350", PrimaryTint: "#22406a", PrimaryTintStrong: "#2b4f80",
        PrimaryDisabled: "#27456e", OnPrimary: "#ffffff",
        HoverSoft: "#1b2b40", AccentBorder: "#3f6392", ControlBorder: "#3a4c63",
        SplitterHover: "#3b82f6", TabIdle: "#131d29",
        SelectedText: "#dbeafe", TooltipBackground: "#223142", TooltipText: "#e8eef7",
        Sidebar: "#0a1017", SidebarText: "#c8d4e6", SidebarMuted: "#7d8ea6",
        SidebarActive: "#1d3350", SidebarBorder: "#24344a", SidebarDisabled: "#5b6a80",
        Danger: "#fca5a5", DangerStrong: "#f87171", DangerSoft: "#3a1d22", DangerBorder: "#5b2a30",
        Success: "#34d399", Warning: "#fbbf24", Info: "#9dc0f5", InfoSoft: "#16283f", InfoBorder: "#274160",
        Scrollbar: "#33445d", Grid: "#23334a", Icon: "#9fb0c8",
        ChevronLeft: ":/polymount/chevron-left-dark.png", ChevronRight: ":/polymount/chevron-right-dark.png",
        ChevronDown: ":/polymount/chevron-down-dark.png",
        SeriesLighten: 0.20, SeriesFillAlpha: 108);

    private static string _qssLight = "", _qssDark = "";
    private static QStyleHints? _hints;

    /// <summary>The reader's choice, remembered beside the language.</summary>
    public static ThemeMode Mode { get; private set; } = ThemeMode.System;

    /// <summary>What is actually being painted: <see cref="Mode"/> unless that says to follow the
    /// platform. Resolved on every read rather than cached, because the platform can change its
    /// mind and a window that is painted later should already know.</summary>
    public static bool IsDark => Mode switch
    {
        ThemeMode.Dark => true,
        ThemeMode.Light => false,
        _ => SystemIsDark()
    };

    /// <summary>Raised when the scheme in force changes, so every window can put its style sheet on
    /// again and every painter can repaint.</summary>
    public static event Action? Changed;

    internal static Palette Tokens => IsDark ? Dark : Light;

    /// <summary>A layer's own colour as this scheme needs it. The service picks these from one
    /// palette meant for white paper, so on a dark plot they are lifted toward the light - a colour
    /// has to be brighter than its ground to sit on it at all - and the fills are laid on thinner,
    /// because an area of near-black under a stroke reads as a hole rather than as a fill.</summary>
    internal static QColor Series(string color)
    {
        var ink = new QColor(color);
        if (!ink.IsValid() || Tokens.SeriesLighten <= 0) return ink;
        using var white = new QColor("#ffffff");
        var mixed = Blend(ink, white, Tokens.SeriesLighten);
        ink.Dispose();
        return mixed;
    }

    /// <summary>Two colours mixed, <paramref name="toward"/> at <paramref name="weight"/>.</summary>
    private static QColor Blend(QColor from, QColor toward, double weight)
    {
        double Mix(int a, int b) => a + (b - a) * weight;
        return new QColor(
            (int)Math.Round(Mix(from.Red(), toward.Red())),
            (int)Math.Round(Mix(from.Green(), toward.Green())),
            (int)Math.Round(Mix(from.Blue(), toward.Blue())));
    }

    /// <summary>The style sheet of the scheme in force, its tokens resolved.</summary>
    public static string StyleSheet => Resolve(IsDark ? Dark : Light);

    /// <summary>Reads the remembered choice, starts following the platform, and puts the scheme on
    /// the application. Called once, before any window is built: the application carries the style
    /// sheet rather than each window, so there is one place to change when the scheme changes and no
    /// window that can be left behind.</summary>
    public static void Initialize()
    {
        Mode = UiSettings.Get("Theme") switch
        {
            "light" => ThemeMode.Light,
            "dark" => ThemeMode.Dark,
            _ => ThemeMode.System
        };
        _hints = QGuiApplication.StyleHints();
        if (_hints != null) _hints.ColorSchemeChanged += _ => { if (Mode == ThemeMode.System) Changed?.Invoke(); };
        Apply();
    }

    /// <summary>The reader's choice, saved where the language is saved. Neither the resolution of
    /// <c>System</c> nor the platform's own setting is written down: what is remembered is the
    /// choice, and a machine that turns dark tonight turns this tray dark with it.</summary>
    public static void SetMode(ThemeMode mode)
    {
        if (Mode == mode) return;
        Mode = mode;
        UiSettings.Set("Theme", mode switch
        {
            ThemeMode.Light => "light",
            ThemeMode.Dark => "dark",
            _ => "system"
        });
        Changed?.Invoke();
        Apply();
    }

    /// <summary>Asks to be told when the scheme changes, and to stop being told when the widget
    /// that asked goes away — the same shape the language uses, so a window that closes is not kept
    /// alive by the theme.</summary>
    public static void OnChanged(Action handler, QObject owner)
    {
        Changed += handler;
        owner.Destroyed += _ => Changed -= handler;
    }

    /// <summary>Puts the style sheet of the scheme in force on the application, and repaints what a
    /// style sheet cannot reach: the cards and charts read their colours at paint time, so they only
    /// need telling that the ground moved.</summary>
    public static void Apply()
    {
        if (QCoreApplication.Instance() is QApplication app) app.StyleSheet = StyleSheet;
    }

    private static bool SystemIsDark() =>
        _hints?.ColorScheme == Qt.ColorScheme.Dark;

    /// <summary>Fills in the <c>@token</c> names a style sheet is written with. Done once per scheme
    /// and kept, because it is a few hundred replacements and a style sheet is handed to every
    /// window that opens.</summary>
    private static string Resolve(Palette palette)
    {
        bool dark = ReferenceEquals(palette, Dark);
        ref string cached = ref (dark ? ref _qssDark : ref _qssLight);
        if (cached.Length > 0) return cached;
        string source = QtResources.ReadText(":/polymount/theme.qss");
        cached = Regex.Replace(source, "@([A-Za-z][A-Za-z0-9]*)", match => Lookup(palette, match.Groups[1].Value));
        return cached;
    }

    private static string Lookup(Palette p, string token)
    {
        var property = typeof(Palette).GetProperty(token);
        if (property == null) throw new InvalidOperationException("the theme has no token named " + token);
        object? value = property.GetValue(p);
        return value is double number
            ? number.ToString("0.##", CultureInfo.InvariantCulture)
            : value is int count
                ? count.ToString(CultureInfo.InvariantCulture)
                : (string)value!;
    }
}
