namespace NetText.UI;

internal enum ThemeKind
{
    None = 0,
    Focus = 1,
    Neon = 2,
    ViceCity = 3,
}

internal static class ThemePalette
{
    private sealed record Palette(
        ThemeKind Kind,
        string Name,
        uint Chrome,
        uint Deep,
        uint Editor,
        uint Panel,
        uint Field,
        uint Border,
        uint Text,
        uint Muted,
        uint Gutter,
        uint Accent,
        uint AccentHover,
        uint Hover,
        uint Selection,
        uint Match,
        uint CurrentMatch,
        uint Warning,
        uint Danger,
        uint DangerSurface,
        uint Success,
        uint Link,
        uint VisitedLink,
        uint UpdateSurface,
        uint UpdateBorder,
        uint UpdateIconSurface,
        uint UpdateText,
        uint JsonKey,
        uint JsonString,
        uint JsonNumber,
        uint JsonLiteral,
        uint EditorCaret,
        uint EditorSelection,
        uint EditorSelectionOutline,
        uint InactiveEditorSelection,
        uint InactiveEditorSelectionOutline,
        uint Toolbar,
        uint AppIconForeground,
        uint AppIconBackground,
        uint AppIconBorder,
        uint StatusBorder,
        uint ActionBorder,
        uint MinimapBackground,
        uint MinimapViewport,
        uint DocumentMinimapInk,
        uint LogMinimapInk,
        uint TitleMotif,
        uint TitleGlow,
        bool HasTitleMotif);

    private static readonly Palette FocusPalette = new(
        Kind: ThemeKind.Focus,
        Name: "focus",
        Chrome: 0x27221D,
        Deep: 0x211C18,
        Editor: 0x413830,
        Panel: 0x332C25,
        Field: 0x2C2620,
        Border: 0x544B41,
        Text: 0xE9DED8,
        Muted: 0xBCB0A6,
        Gutter: 0xAEA196,
        Accent: 0xC6C6CC,
        AccentHover: 0xD6D6DB,
        Hover: 0x3C342C,
        Selection: 0x584D44,
        Match: 0x3A5869,
        CurrentMatch: 0x43657B,
        Warning: 0x85BDE5,
        Danger: 0x9DA0F0,
        DangerSurface: 0x33304A,
        Success: 0xB4CEB3,
        Link: 0xE6C772,
        VisitedLink: 0xE1B9CD,
        UpdateSurface: 0x282E34,
        UpdateBorder: 0x42576B,
        UpdateIconSurface: 0x2C3947,
        UpdateText: 0xD2E4F3,
        JsonKey: 0xE1C9AE,
        JsonString: 0xAAD3BD,
        JsonNumber: 0x85BDE5,
        JsonLiteral: 0xE1B9CD,
        EditorCaret: 0xFFF7F2,
        EditorSelection: 0x574838,
        EditorSelectionOutline: 0xE0BF93,
        InactiveEditorSelection: 0x4B4035,
        InactiveEditorSelectionOutline: 0x9F8B75,
        Toolbar: 0xBCB0A6,
        AppIconForeground: 0x85BDE5,
        AppIconBackground: 0x2C3947,
        AppIconBorder: 0x42576B,
        StatusBorder: 0x544B41,
        ActionBorder: 0x544B41,
        MinimapBackground: 0x3C342C,
        MinimapViewport: 0x584D44,
        DocumentMinimapInk: 0xBCB0A6,
        LogMinimapInk: 0xBCB0A6,
        TitleMotif: 0x00000000,
        TitleGlow: 0x27221D,
        HasTitleMotif: false);

    private static readonly Palette NeonPalette = new(
        Kind: ThemeKind.Neon,
        Name: "neon",
        Chrome: 0x150B09,
        Deep: 0x120907,
        Editor: 0x261410,
        Panel: 0x2D1B17,
        Field: 0x20110D,
        Border: 0x5D4033,
        Text: 0xFFF6E9,
        Muted: 0xD0B4A4,
        Gutter: 0xAA8878,
        Accent: 0xFFE622,
        AccentHover: 0xFFF275,
        Hover: 0x492D20,
        Selection: 0x684428,
        Match: 0x284A5A,
        CurrentMatch: 0x276180,
        Warning: 0x66D1FF,
        Danger: 0x915CFF,
        DangerSurface: 0x3D2348,
        Success: 0xC8F566,
        Link: 0xFFCF76,
        VisitedLink: 0xB87DFF,
        UpdateSurface: 0x472026,
        UpdateBorder: 0x914F5E,
        UpdateIconSurface: 0x5D2737,
        UpdateText: 0xFFE9F0,
        JsonKey: 0xFFDF8C,
        JsonString: 0xD2F282,
        JsonNumber: 0x66D1FF,
        JsonLiteral: 0xB87DFF,
        EditorCaret: 0xFFF271,
        EditorSelection: 0x462D19,
        EditorSelectionOutline: 0xF6DF6C,
        InactiveEditorSelection: 0x38241A,
        InactiveEditorSelectionOutline: 0xA68763,
        Toolbar: 0xD0B4A4,
        AppIconForeground: 0x66D1FF,
        AppIconBackground: 0x5D2737,
        AppIconBorder: 0x914F5E,
        StatusBorder: 0x5D4033,
        ActionBorder: 0x5D4033,
        MinimapBackground: 0x492D20,
        MinimapViewport: 0x684428,
        DocumentMinimapInk: 0xD0B4A4,
        LogMinimapInk: 0xD0B4A4,
        TitleMotif: 0x00000000,
        TitleGlow: 0x150B09,
        HasTitleMotif: false);

    private static readonly Palette ViceCityPalette = new(
        Kind: ThemeKind.ViceCity,
        Name: "vicecity",
        Chrome: 0x1E1215,
        Deep: 0x1A0F12,
        Editor: 0x241519,
        Panel: 0x301B22,
        Field: 0x2D1A20,
        Border: 0x59364A,
        Text: 0xF3DEE8,
        Muted: 0xCAAAB9,
        Gutter: 0xAE8698,
        Accent: 0xAD62FF,
        AccentHover: 0xC591FF,
        Hover: 0x3D232D,
        Selection: 0x523049,
        Match: 0x344355,
        CurrentMatch: 0x3C5076,
        Warning: 0x8FD0FF,
        Danger: 0x9C82FF,
        DangerSurface: 0x321E3B,
        Success: 0xCDE089,
        Link: 0xD6E069,
        VisitedLink: 0xF4A4B6,
        UpdateSurface: 0x3C212F,
        UpdateBorder: 0x785482,
        UpdateIconSurface: 0x412538,
        UpdateText: 0xECDFF5,
        JsonKey: 0xBC83FF,
        JsonString: 0xCDE089,
        JsonNumber: 0x8FD0FF,
        JsonLiteral: 0xF4A4B6,
        EditorCaret: 0xC89BFF,
        EditorSelection: 0x3E2636,
        EditorSelectionOutline: 0xCEA4EA,
        InactiveEditorSelection: 0x36222C,
        InactiveEditorSelectionOutline: 0x9F7BAA,
        Toolbar: 0xD6E069,
        AppIconForeground: 0xD6E069,
        AppIconBackground: 0x301B22,
        AppIconBorder: 0xAD62FF,
        StatusBorder: 0xD6E069,
        ActionBorder: 0xD6E069,
        MinimapBackground: 0x211317,
        MinimapViewport: 0x523049,
        DocumentMinimapInk: 0xA87A92,
        LogMinimapInk: 0xA87A92,
        TitleMotif: 0x211319,
        TitleGlow: 0x492349,
        HasTitleMotif: true);

    private static Palette _current = FocusPalette;

    public static ThemeKind CurrentKind => _current.Kind;
    public static string CurrentName => _current.Name;

    public static uint Chrome => _current.Chrome;
    public static uint Deep => _current.Deep;
    public static uint Editor => _current.Editor;
    public static uint Panel => _current.Panel;
    public static uint Field => _current.Field;
    public static uint Border => _current.Border;
    public static uint Text => _current.Text;
    public static uint Muted => _current.Muted;
    public static uint Gutter => _current.Gutter;
    public static uint Accent => _current.Accent;
    public static uint AccentHover => _current.AccentHover;
    public static uint Hover => _current.Hover;
    public static uint Selection => _current.Selection;
    public static uint Match => _current.Match;
    public static uint CurrentMatch => _current.CurrentMatch;
    public static uint Warning => _current.Warning;
    public static uint Danger => _current.Danger;
    public static uint DangerSurface => _current.DangerSurface;
    public static uint Success => _current.Success;
    public static uint Link => _current.Link;
    public static uint VisitedLink => _current.VisitedLink;
    public static uint UpdateSurface => _current.UpdateSurface;
    public static uint UpdateBorder => _current.UpdateBorder;
    public static uint UpdateIconSurface => _current.UpdateIconSurface;
    public static uint UpdateText => _current.UpdateText;
    public static uint JsonKey => _current.JsonKey;
    public static uint JsonString => _current.JsonString;
    public static uint JsonNumber => _current.JsonNumber;
    public static uint JsonLiteral => _current.JsonLiteral;
    public static uint EditorCaret => _current.EditorCaret;
    public static uint EditorSelection => _current.EditorSelection;
    public static uint EditorSelectionOutline => _current.EditorSelectionOutline;
    public static uint InactiveEditorSelection => _current.InactiveEditorSelection;
    public static uint InactiveEditorSelectionOutline => _current.InactiveEditorSelectionOutline;
    public static uint Toolbar => _current.Toolbar;
    public static uint AppIconForeground => _current.AppIconForeground;
    public static uint AppIconBackground => _current.AppIconBackground;
    public static uint AppIconBorder => _current.AppIconBorder;
    public static uint StatusBorder => _current.StatusBorder;
    public static uint ActionBorder => _current.ActionBorder;
    public static uint MinimapBackground => _current.MinimapBackground;
    public static uint MinimapViewport => _current.MinimapViewport;
    public static uint DocumentMinimapInk => _current.DocumentMinimapInk;
    public static uint LogMinimapInk => _current.LogMinimapInk;
    public static uint TitleMotif => _current.TitleMotif;
    public static uint TitleGlow => _current.TitleGlow;
    public static bool HasTitleMotif => _current.HasTitleMotif;

    public static uint Bg => _current.Editor;
    public static uint TabBar => _current.Chrome;
    public static uint TabActiveBg => _current.Editor;
    public static uint TabActiveBorder => _current.Accent;
    public static uint TabInactiveBg => _current.Panel;
    public static uint TabInactiveBorder => _current.Border;
    public static uint HoverBg => _current.Hover;
    public static uint SelectedBg => _current.Selection;
    public static uint Dim => _current.Gutter;
    public static uint Error => _current.Danger;
    public static uint SelectionBg => _current.EditorSelection;
    public static uint SearchMatchBg => _current.Match;
    public static uint CaretLineBg => _current.Panel;

    public static ThemeKind ParseKind(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return ThemeKind.Focus;
        return name.Trim().ToLowerInvariant() switch
        {
            "neon" => ThemeKind.Neon,
            "vicecity" or "vice-city" or "vice city" or "vice_city" => ThemeKind.ViceCity,
            _ => ThemeKind.Focus,
        };
    }

    public static bool Apply(string? name) => Apply(ParseKind(name));

    public static bool Apply(ThemeKind kind)
    {
        var next = kind switch
        {
            ThemeKind.Neon => NeonPalette,
            ThemeKind.ViceCity => ViceCityPalette,
            _ => FocusPalette,
        };
        if (ReferenceEquals(_current, next)) return false;
        _current = next;
        return true;
    }

    public static uint Rgb(byte r, byte g, byte b) => (uint)(r | (g << 8) | (b << 16));
}
