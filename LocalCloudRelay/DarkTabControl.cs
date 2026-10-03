namespace LocalCloudRelay;

/// <summary>
/// A TabControl that paints its own strip and frame. The native control draws a light
/// 3D border and light tab headers whatever BackColor says, which left a white frame
/// around every page of the dark UI.
/// </summary>
internal sealed class DarkTabControl : TabControl
{
    public DarkTabControl()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    /// <summary>Strip and frame colour. TabControl ignores BackColor and reports the system colour.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Color StripColor { get; set; } = Palette.Rack;

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        using (var back = new SolidBrush(StripColor)) g.FillRectangle(back, ClientRectangle);

        var page = DisplayRectangle;
        page.Inflate(1, 1);
        using (var frame = new Pen(Palette.Hairline))
            g.DrawRectangle(frame, page.X, page.Y, page.Width - 1, page.Height - 1);

        for (var i = 0; i < TabCount; i++)
        {
            var bounds = GetTabRect(i);
            var selected = i == SelectedIndex;
            using (var fill = new SolidBrush(selected ? Palette.Panel : Palette.Bezel))
                g.FillRectangle(fill, bounds);
            if (selected)
            {
                using var accent = new SolidBrush(Palette.LinkUp);
                g.FillRectangle(accent, bounds.X, bounds.Y, bounds.Width, 2);
            }
            TextRenderer.DrawText(g, TabPages[i].Text, Font, bounds,
                selected ? Palette.TextPrimary : Palette.TextTertiary,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        }
    }

    protected override void OnSelectedIndexChanged(EventArgs e)
    {
        base.OnSelectedIndexChanged(e);
        Invalidate();
    }
}
