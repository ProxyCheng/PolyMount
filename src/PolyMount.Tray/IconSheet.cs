using QtSharp.QtCore;
using QtSharp.QtGui;
using QtSharp.QtWidgets;

namespace PolyMount.Tray;

/// <summary>Every glyph this tray draws, on one sheet, at the sizes it is actually used at.
///
/// An icon set is looked at, not asserted: the questions — is this readable at eighteen pixels,
/// do these two read as different things, does that one still look like what it claims to be — are
/// answered by the picture. The sheet is part of <c>--render</c> because that is where the
/// controls' pictures live, and it needs neither a service nor a window.
///
/// It also answers the one question a picture cannot: a name with no case behind it draws nothing
/// at all, and a stroke that fell outside the box looks the same as a glyph that was never written.
/// <see cref="Verify"/> counts the ink of every name so a silently empty icon fails a check.</summary>
internal static class IconSheet
{
    /// <summary>The sizes the tray asks for: a row's mark, a button, a section's title, the
    /// sidebar, and the two the shell and the app icon are drawn from.</summary>
    private static readonly int[] Sizes = { 16, 18, 20, 24, 32, 48, 64 };

    /// <summary>Draws the sheet into <paramref name="directory"/> and says which names came out
    /// empty. Both schemes are not drawn: the sheet uses the scheme in force, and the only thing
    /// that changes with it is the ink.</summary>
    internal static void Run(string directory)
    {
        var names = TrayIcons.Names;
        const int pad = 20, label = 150, gap = 8;
        int cell = Sizes[^1] + gap * 2;
        int width = pad * 2 + label + cell * Sizes.Length;
        int rowHeight = Sizes[^1] + 12;
        int height = pad * 2 + rowHeight * names.Count + 30;
        using var image = new QImage(width, height, QImage.Format.Format_ARGB32_Premultiplied);
        image.Fill(new QColor(TrayTheme.Tokens.Surface));
        using (var p = new QPainter(image))
        {
            p.SetRenderHint(QPainter.RenderHint.Antialiasing);
            using var labelFont = new QFont("Segoe UI", 9);
            using var headFont = new QFont("Segoe UI", 9);
            headFont.SetBold(true);
            var ink = new QColor(TrayTheme.Tokens.Muted);
            var body = new QColor(TrayTheme.Tokens.Body);
            p.SetFont(headFont);
            p.SetPen(new QPen(ink));
            for (int s = 0; s < Sizes.Length; s++)
                p.DrawText(pad + label + s * cell, pad + 6, Sizes[s] + " px");
            p.SetFont(labelFont);
            for (int row = 0; row < names.Count; row++)
            {
                int y = pad + 30 + row * rowHeight;
                p.SetPen(new QPen(body));
                p.DrawText(pad, y + rowHeight / 2 + 4, names[row]);
                for (int s = 0; s < Sizes.Length; s++)
                {
                    using var pixmap = TrayIcons.Get(names[row]).Pixmap(Sizes[s], Sizes[s]);
                    int x = pad + label + s * cell + (cell - Sizes[s]) / 2;
                    p.DrawPixmap(x, y + (rowHeight - Sizes[s]) / 2, pixmap);
                }
            }
            p.End();
        }
        using var sheet = QPixmap.FromImage(image);
        string path = Path.Combine(directory, "icons.png");
        sheet.Save(path);
        var empty = names.Where(name => Ink(name) == 0).ToList();
        Console.WriteLine($"icons.png                 {names.Count} glyphs at {string.Join("/", Sizes)} px" +
            (empty.Count == 0 ? "" : "  EMPTY: " + string.Join(", ", empty)));
    }

    /// <summary>Every name has to draw something. A glyph is a handful of coordinates, and one
    /// typed outside the box draws nothing at all — which no reader reports, because they only see
    /// that an icon is missing.</summary>
    internal static void Verify()
    {
        var empty = TrayIcons.Names.Where(name => Ink(name) == 0).ToList();
        if (empty.Count > 0)
            throw new InvalidOperationException("These icons draw nothing: " + string.Join(", ", empty));
    }

    /// <summary>How many pixels a glyph puts ink on, at the size the credential rows use.</summary>
    private static int Ink(string name)
    {
        // The icon belongs to TrayIcons' cache and is shared with every window that draws it:
        // disposing it here would leave the cache handing out a dead wrapper, which surfaces as an
        // ObjectDisposedException in whichever control draws that mark next.
        using var pixmap = TrayIcons.Get(name).Pixmap(24, 24);
        using var image = pixmap.ToImage();
        int ink = 0;
        for (int y = 0; y < image.Height(); y++)
            for (int x = 0; x < image.Width(); x++)
                if (image.PixelColor(x, y).Alpha() > 24) ink++;
        return ink;
    }
}
