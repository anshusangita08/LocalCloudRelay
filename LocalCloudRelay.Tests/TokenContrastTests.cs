using LocalCloudRelay;

namespace LocalCloudRelay.Tests;

/// <summary>
/// Build-failing guard on the design tokens. FgMuted in the old theme sat at 2.62:1 on
/// the card surface, below even the 3:1 non-text floor, and nothing caught it because
/// the tokens were untyped color constants with no declared surface. Every foreground
/// token is now checked against every surface it can sit on.
/// </summary>
public sealed class TokenContrastTests
{
    private const double WcagAaText = 4.5;

    [Fact]
    public void EveryForegroundTokenMeetsAaOnEverySurface()
    {
        var failures = new List<string>();
        foreach (var foreground in Palette.Foregrounds)
            foreach (var background in Palette.Surfaces)
            {
                var ratio = Palette.ContrastRatio(foreground, background);
                if (ratio < WcagAaText)
                    failures.Add($"{Palette.Hex(foreground)} on {Palette.Hex(background)} = {ratio:0.00}:1 (needs {WcagAaText:0.0})");
            }

        Assert.True(failures.Count == 0,
            "Design tokens fail WCAG AA:" + Environment.NewLine + string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public void MutedTextStaysReadableEvenOnTheLightestSurface()
    {
        // The specific regression: TextMuted is the lowest level and must still clear AA.
        var lightest = Palette.Surfaces.MaxBy(Palette.Luminance)!;
        Assert.True(Palette.ContrastRatio(Palette.TextMuted, lightest) >= WcagAaText);
    }

    [Fact]
    public void InputsAreDarkerThanTheirSurroundings()
    {
        // Inputs are inset, so they receive content; a lighter input reads as raised.
        Assert.True(Palette.Luminance(Palette.Input) < Palette.Luminance(Palette.Panel));
    }

    [Fact]
    public void HairlinesAreVisibleButNeverDominant()
    {
        // A border you notice first is too strong.
        var ratio = Palette.ContrastRatio(Palette.Hairline, Palette.Rack);
        Assert.InRange(ratio, 1.1, 2.0);
    }
}
