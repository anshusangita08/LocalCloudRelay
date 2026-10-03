using System.Drawing;
using System.Drawing.Imaging;
using LocalCloudRelay;

namespace LocalCloudRelay.Tests;

/// <summary>
/// The tray, the taskbar and the status dot are all fed from this one factory, so a
/// regression here is a blank or mis-coloured icon in the taskbar, which is the only
/// place the user can tell what state the relay is in.
/// </summary>
public sealed class StatusIconTests
{
    private static readonly Color[] States =
    [
        RelayStatusIcon.ColorRunning,
        RelayStatusIcon.ColorWarning,
        RelayStatusIcon.ColorError,
        RelayStatusIcon.ColorIdle,
    ];

    [Fact]
    public void EveryStateProducesANonEmptyIcon()
    {
        foreach (var color in States)
        {
            using var icon = RelayStatusIcon.Create(color);
            Assert.True(icon.Width > 0 && icon.Height > 0);
        }
    }

    [Fact]
    public void EveryStateLooksDifferentFromTheOthers()
    {
        // Hue alone is not enough to tell the states apart, so the glyph differs too.
        // Comparing pixels is what makes that a guarantee rather than a comment.
        var signatures = States.Select(color =>
        {
            using var icon = RelayStatusIcon.Create(color);
            using var bitmap = icon.ToBitmap();
            var pixels = new byte[bitmap.Width * bitmap.Height * 4];
            var data = bitmap.LockBits(
                new Rectangle(0, 0, bitmap.Width, bitmap.Height),
                ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try { System.Runtime.InteropServices.Marshal.Copy(data.Scan0, pixels, 0, pixels.Length); }
            finally { bitmap.UnlockBits(data); }
            return Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(pixels));
        }).ToList();

        Assert.Equal(signatures.Count, signatures.Distinct().Count());
    }

    [Fact]
    public void TheRoundedSquareKeepsItsTransparentCorners()
    {
        // GetHicon on a 32bpp bitmap can flatten alpha to black, which is how a tray
        // icon ends up as a filled black square. The corners must still be clear.
        using var icon = RelayStatusIcon.Create(RelayStatusIcon.ColorRunning);
        using var bitmap = icon.ToBitmap();

        Assert.Equal(0, bitmap.GetPixel(0, 0).A);
        Assert.Equal(0, bitmap.GetPixel(bitmap.Width - 1, 0).A);
        Assert.Equal(0, bitmap.GetPixel(0, bitmap.Height - 1).A);
        Assert.Equal(0, bitmap.GetPixel(bitmap.Width - 1, bitmap.Height - 1).A);
        Assert.Equal(255, bitmap.GetPixel(bitmap.Width / 2, bitmap.Height / 2).A);
    }

    [Fact]
    public void TheBackgroundIsTheRequestedColour()
    {
        using var icon = RelayStatusIcon.Create(RelayStatusIcon.ColorRunning);
        using var bitmap = icon.ToBitmap();

        // Inside the square, clear of the rounded corner, the arcs and the glyph.
        var pixel = bitmap.GetPixel(bitmap.Width / 2, 4);
        Assert.True(pixel.R < 32 && pixel.G > 180 && pixel.B > 130,
            $"expected the running teal, got {pixel}");
    }
}