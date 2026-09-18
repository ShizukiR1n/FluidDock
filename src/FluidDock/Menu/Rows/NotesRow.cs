using System.Numerics;
using FluidDock.Graphics;
using Windows.UI.Composition;

namespace FluidDock.Menu.Rows;

/// <summary>
/// A heading and a paragraph under it: what a newer version fixed and added, shown beneath the
/// update row once a check has found one.
///
/// The only row whose height comes from its contents. It is measured when the row is made,
/// because the panel lays rows out by <see cref="MenuRow.Height"/> before any of them is built -
/// the card behind them is baked at that size - and so the wrap has to be settled here, against
/// the width the panel is known to give a row, rather than discovered while drawing.
/// </summary>
internal sealed class NotesRow : MenuRow
{
    private const float PadY = 12f;
    private const float Gap = 6f;

    private readonly string _body;
    private readonly float _headingHeight;
    private readonly float _height;

    public NotesRow(string heading, string body) : base(heading)
    {
        _body = body;
        _headingHeight = TextRaster.Measure(heading, MenuTheme.RowFont).Height;

        float bodyHeight = TextRaster.MeasureWrapped(body, MenuTheme.ValueFont, TextWidth).Height;
        _height = MathF.Ceiling(PadY + _headingHeight + Gap + bodyHeight + PadY);
    }

    /// <summary>What is left of a row for text once the card's and the row's own padding are off.</summary>
    private static float TextWidth =>
        MenuTheme.PanelWidth - MenuTheme.PanelPadX * 2f - MenuTheme.RowPadX * 2f;

    public override float Height => _height;

    public override bool Interactive => false;

    protected override void Build(MenuCanvas canvas)
    {
        SpriteVisual heading = canvas.Text(Title, MenuTheme.RowFont, MenuTheme.TextPrimary);
        heading.Offset = new Vector3(MenuTheme.RowPadX, PadY, 0f);
        Root!.Children.InsertAtTop(heading);

        SpriteVisual body = canvas.Paragraph(_body, MenuTheme.ValueFont, MenuTheme.TextSecondary, TextWidth);
        body.Offset = new Vector3(MenuTheme.RowPadX, PadY + _headingHeight + Gap, 0f);
        Root.Children.InsertAtTop(body);
    }
}
