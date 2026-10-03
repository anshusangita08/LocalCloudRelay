namespace LocalCloudRelay;

/// <summary>
/// Design tokens, named for the product's world rather than for a generic scale: this is
/// a patch panel in an unlit rack, not a consumer app. Every foreground token is checked
/// against every surface it can legally sit on, so a token can never be introduced with
/// a ratio that fails. See TokenContrastTests.
/// </summary>
public static class Palette
{
    // Surfaces. Elevation is a few percent of lightness; there are no shadows in a dark UI.
    public static readonly Color Rack = Color.FromArgb(0x14, 0x14, 0x1F);    // window
    public static readonly Color Bezel = Color.FromArgb(0x1B, 0x1B, 0x28);  // nav / band
    public static readonly Color Panel = Color.FromArgb(0x23, 0x23, 0x2F);   // cards, grids
    public static readonly Color Ledger = Color.FromArgb(0x1E, 0x1E, 0x2E);  // data readout
    public static readonly Color Input = Color.FromArgb(0x0E, 0x0E, 0x18);   // inset, darker

    public static readonly Color Seam = Color.FromArgb(0x1F, 0x1F, 0x2B);     // softer separation
    public static readonly Color Hairline = Color.FromArgb(0x2A, 0x2A, 0x3A); // standard separation

    // Four text levels. Muted is the lowest and is never used for anything a user must read.
    public static readonly Color TextPrimary = Color.FromArgb(0xE4, 0xE4, 0xEC);
    public static readonly Color TextSecondary = Color.FromArgb(0xB4, 0xB4, 0xC8);
    public static readonly Color TextTertiary = Color.FromArgb(0x9A, 0x9A, 0xB4);
    public static readonly Color TextMuted = Color.FromArgb(0x8C, 0x8C, 0xAA);

    // Link states, borrowed from rack LEDs. The tray icon carries a matching glyph for
    // each, so state is never communicated by hue alone.
    public static readonly Color LinkUp = Color.FromArgb(0x00, 0xD4, 0xAA);
    public static readonly Color LinkPending = Color.FromArgb(0xE0, 0xA0, 0x3C);
    public static readonly Color LinkDown = Color.FromArgb(0xE8, 0x6A, 0x6A);

    public static IReadOnlyList<Color> Surfaces { get; } = [Rack, Bezel, Panel, Ledger, Input];

    public static IReadOnlyList<Color> Foregrounds { get; } =
        [TextPrimary, TextSecondary, TextTertiary, TextMuted, LinkUp, LinkPending, LinkDown];

    public static Color ForLink(bool healthy) => healthy ? LinkUp : LinkDown;

    /// <summary>WCAG 2.1 relative luminance.</summary>
    public static double Luminance(Color color) => 0.2126 * Channel(color.R) + 0.7152 * Channel(color.G) + 0.0722 * Channel(color.B);

    public static double ContrastRatio(Color foreground, Color background)
    {
        var a = Luminance(foreground);
        var b = Luminance(background);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    private static double Channel(byte value)
    {
        var c = value / 255.0;
        return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }

    public static string Hex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";
}
