using System.Globalization;
using NetText.Native;

namespace NetText.UI;

internal sealed unsafe partial class MainWindow
{
    private readonly record struct WorkspaceColorPreset(string Name, string Hex, uint Bgr);

    private static readonly WorkspaceColorPreset[] WorkspaceColors =
    [
        new("Синий", "#75A9FF", ThemePalette.Rgb(0x75, 0xA9, 0xFF)),
        new("Голубой", "#8DD9F5", ThemePalette.Rgb(0x8D, 0xD9, 0xF5)),
        new("Бирюзовый", "#60D4CD", ThemePalette.Rgb(0x60, 0xD4, 0xCD)),
        new("Мятный", "#78DCB0", ThemePalette.Rgb(0x78, 0xDC, 0xB0)),
        new("Зелёный", "#8AD196", ThemePalette.Rgb(0x8A, 0xD1, 0x96)),
        new("Салатовый", "#B7D982", ThemePalette.Rgb(0xB7, 0xD9, 0x82)),
        new("Жёлтый", "#F2DE79", ThemePalette.Rgb(0xF2, 0xDE, 0x79)),
        new("Золотой", "#F1BF69", ThemePalette.Rgb(0xF1, 0xBF, 0x69)),
        new("Персиковый", "#F6AE82", ThemePalette.Rgb(0xF6, 0xAE, 0x82)),
        new("Красный", "#F18A83", ThemePalette.Rgb(0xF1, 0x8A, 0x83)),
        new("Розовый", "#E88EB9", ThemePalette.Rgb(0xE8, 0x8E, 0xB9)),
        new("Сиреневый", "#BAA0F3", ThemePalette.Rgb(0xBA, 0xA0, 0xF3)),
        new("Фиолетовый", "#A58BF0", ThemePalette.Rgb(0xA5, 0x8B, 0xF0)),
        new("Серо-синий", "#8199B6", ThemePalette.Rgb(0x81, 0x99, 0xB6)),
        new("Серый", "#A5ABB6", ThemePalette.Rgb(0xA5, 0xAB, 0xB6)),
        new("Белый", "#EDF0F6", ThemePalette.Rgb(0xED, 0xF0, 0xF6)),
    ];

    internal static bool TryPickCustomColor(nint owner, uint initialBgr, out string hexColor)
    {
        uint* customColors = stackalloc uint[16];
        for (int i = 0; i < 16; i++)
        {
            customColors[i] = WorkspaceColors[i].Bgr;
        }

        var cc = new Win32.CHOOSECOLOR
        {
            lStructSize = (uint)sizeof(Win32.CHOOSECOLOR),
            hwndOwner = owner,
            rgbResult = initialBgr,
            lpCustColors = customColors,
            Flags = Win32.CC_RGBINIT | Win32.CC_FULLOPEN | Win32.CC_ANYCOLOR,
        };

        if (!Win32.ChooseColorW(&cc))
        {
            hexColor = "";
            return false;
        }

        hexColor = BgrToHexColor(cc.rgbResult);
        return true;
    }

    internal static bool TryParseHexColorToBgr(string? hex, out uint bgr)
    {
        bgr = 0;
        if (string.IsNullOrWhiteSpace(hex)) return false;

        var span = hex.AsSpan().Trim();
        if (span.Length != 7 || span[0] != '#') return false;

        if (!byte.TryParse(span.Slice(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte r) ||
            !byte.TryParse(span.Slice(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte g) ||
            !byte.TryParse(span.Slice(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte b))
        {
            return false;
        }

        bgr = ThemePalette.Rgb(r, g, b);
        return true;
    }

    internal static string BgrToHexColor(uint bgr)
    {
        byte r = (byte)(bgr & 0xFF);
        byte g = (byte)((bgr >> 8) & 0xFF);
        byte b = (byte)((bgr >> 16) & 0xFF);
        return $"#{r:X2}{g:X2}{b:X2}";
    }
}
