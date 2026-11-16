namespace NetText.UI;

internal static class ThemePalette
{
    public const uint Bg = 0x1F1610;
    public const uint Panel = 0x2B2018;
    public const uint TabBar = 0x241A13;
    public const uint Border = 0x423429;
    public const uint TabActiveBg = 0x4A3626;
    public const uint TabActiveBorder = 0x80634A;
    public const uint TabInactiveBg = 0x2B2018;
    public const uint TabInactiveBorder = 0x413126;
    public const uint HoverBg = 0x3A2C21;
    public const uint SelectedBg = 0x4A3626;
    public const uint Text = 0xF2EAE4;
    public const uint Muted = 0xD0BDAE;
    public const uint Dim = 0x8A7665;
    public const uint Error = 0xADB4FF;
    public const uint SelectionBg = 0x5A402A;
    public const uint SearchMatchBg = 0x2F5266;
    public const uint CaretLineBg = 0x261C14;

    public static uint Rgb(byte r, byte g, byte b) => (uint)(r | (g << 8) | (b << 16));
}
