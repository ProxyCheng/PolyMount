using QtSharp.Api;

namespace PolyMount.Tray;

/// <summary>The face every card on the performance page is drawn with: a white rounded rectangle
/// with a hairline edge, and its title in the top left corner. A trend card paints its headline and
/// its figures over this and puts its chart below; a pie card has nothing above its chart but the
/// title. It is one place so that the page cannot come to have two kinds of card on it.</summary>
internal static class CardFace
{
    /// <summary>Where the title's baseline sits, and where a card's own text starts — the two are
    /// the same inset from the left edge the charts keep on both sides.</summary>
    internal const int TitleBaseline = 26, TextInset = 18;

    /// <summary>Paints the frame and the title. What else a card has to say above its chart — the
    /// headline and the window's figures — is painted by that card on top of this, and the chart
    /// itself is a child widget drawn over the middle of it.</summary>
    internal static void Paint(QPainter p, int width, int height, string title)
    {
        p.SetBrush(new QBrush(new QColor(TrayTheme.Tokens.Surface)));
        p.SetPen(new QPen(new QColor(TrayTheme.Tokens.Border)));
        p.DrawRoundedRect(1, 1, width - 2, height - 2, 12, 12);
        using var titleFont = new QFont("Segoe UI", 11);
        titleFont.SetBold(true);
        p.SetFont(titleFont);
        p.SetPen(new QPen(new QColor(TrayTheme.Tokens.Body)));
        p.DrawText(TextInset, TitleBaseline, title);
    }
}
