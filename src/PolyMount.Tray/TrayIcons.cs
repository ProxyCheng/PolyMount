using QtSharp.QtCore;
using QtSharp.QtGui;
using QtSharp.QtWidgets;

namespace PolyMount.Tray;

/// <summary>Original, consistently stroked icons drawn with Qt; no font or SVG plugin dependency.</summary>
internal static class TrayIcons
{
    private static readonly Dictionary<string, QIcon> Cache = new();

    /// <summary>Every glyph this set draws, for the sheet that is looked at and the check that
    /// every name still draws something. A name missing here is a mark nobody can look at; a name
    /// here with no case below is an icon that comes out empty.</summary>
    internal static IReadOnlyList<string> Names { get; } = new[]
    {
        "close", "app", "layers", "plugin", "drive", "info", "key", "certificate", "certificate-store", "credential",
        "tortoise", "sign-in", "folder", "edit-layer", "disk-cache", "memory-chip", "predict-path",
        "warm-listings", "repository", "plus", "chevron-right", "chevron-down", "check", "alert",
        "play", "stop", "chart", "trash", "refresh", "export", "record", "save", "settings", "default"
    };

    public static QIcon App => Get("app", "#ffffff");
    public static QPixmap BrandPixmap
    {
        get
        {
            using var image = new QImage(336, 72, QImage.Format.Format_ARGB32_Premultiplied);
            image.Fill(0u);
            using var p = new QPainter(image);
            p.SetRenderHint(QPainter.RenderHint.Antialiasing);
            p.DrawPixmap(0, 4, App.Pixmap(60, 60));
            p.SetPen(new QPen(new QColor("#ffffff")));
            using var font = new QFont("Segoe UI", 22);
            font.SetBold(true);
            p.SetFont(font);
            p.DrawText(76, 49, "PolyMount");
            p.End();
            var pixmap = QPixmap.FromImage(image);
            pixmap.SetDevicePixelRatio(2);
            return pixmap;
        }
    }

    public static QIcon Get(string name, string? color = null)
    {
        // No colour given means the scheme's own icon ink: an icon stroke has to be readable on the
        // surface it is drawn on, and that is a question the theme answers, not the caller.
        color ??= TrayTheme.Tokens.Icon;
        string key = name + color;
        if (Cache.TryGetValue(key, out var existing)) return existing;
        using var image = new QImage(64, 64, QImage.Format.Format_ARGB32_Premultiplied);
        image.Fill(0u);
        using var p = new QPainter(image);
        p.SetRenderHint(QPainter.RenderHint.Antialiasing);
        p.Scale(64.0 / 24, 64.0 / 24);
        using var pen = new QPen(new QColor(color));
        pen.SetWidthF(1.7);
        pen.SetCapStyle(Qt.PenCapStyle.RoundCap);
        pen.SetJoinStyle(Qt.PenJoinStyle.RoundJoin);
        p.SetPen(pen);
        p.SetBrush(new QBrush(Qt.BrushStyle.NoBrush));
        void Line(int x, int y, int x2, int y2) => p.DrawLine(x, y, x2, y2);
        void Path(params (double X, double Y)[] points)
        {
            using var path = new QPainterPath();
            path.MoveTo(points[0].X, points[0].Y);
            foreach (var point in points.Skip(1)) path.LineTo(point.X, point.Y);
            p.DrawPath(path);
        }
        switch (name)
        {
            case "close": Line(7, 7, 17, 17); Line(17, 7, 7, 17); break;
            case "app":
                p.SetBrush(new QBrush(new QColor(TrayTheme.Tokens.Primary)));
                p.SetPen(new QPen(new QColor(TrayTheme.Tokens.Primary)));
                p.DrawRoundedRect(1, 1, 22, 22, 6, 6);
                p.SetPen(pen);
                p.SetBrush(new QBrush(Qt.BrushStyle.NoBrush));
                Path((5, 9), (12, 5), (19, 9), (12, 13), (5, 9));
                Path((5, 13), (12, 17), (19, 13));
                Path((5, 17), (12, 21), (19, 17));
                break;
            case "layers": Path((3, 8), (12, 3), (21, 8), (12, 13), (3, 8)); Path((3, 12), (12, 17), (21, 12)); Path((3, 16), (12, 21), (21, 16)); break;
            // A plug: two prongs, the body, and the cable. It is the one mark that says
            // "this is something you plug in" without borrowing a product's logo.
            case "plugin": Line(9, 3, 9, 7); Line(15, 3, 15, 7); p.DrawRoundedRect(7, 7, 10, 9, 3, 3); Line(12, 16, 12, 21); break;
            case "drive": p.DrawRoundedRect(3, 5, 18, 14, 3, 3); Line(3, 14, 21, 14); Line(16, 17, 18, 17); break;
            // A circled "i": the mark every program uses for "what is this", drawn rather than
            // borrowed, in the same 24-pixel box and stroke as the rest.
            case "info": p.DrawEllipse(3, 3, 18, 18); p.DrawEllipse(11, 7, 2, 2); Line(12, 11, 12, 17); break;
            // A key: the ring and the shaft with two teeth. What the accounts section is. The
            // ring is as large as the box allows and the teeth differ in length, because at
            // sixteen pixels two equal teeth read as one dash.
            case "key":
                p.DrawEllipse(3, 8, 9, 9);
                Line(12, 12, 21, 12);
                Line(17, 12, 17, 17); Line(13, 12, 13, 15);
                break;
            // A certificate: a shield with a check in it, which is the mark browsers put beside a
            // connection they have verified. A sheet of paper with a stamp was the other
            // candidate and lost twice — the sheet is too close to the pass the plugin kinds wear,
            // and its internal lines turn to mush at the eighteen pixels a credential row gives.
            case "certificate":
                Path((12, 2), (20, 5), (20, 11), (12, 22), (4, 11), (4, 5), (12, 2));
                Path((8.5, 11), (11, 13.5), (15.5, 9));
                break;
            // The same shield with a keyhole where the tick was: a certificate whose private key
            // never left the store it was installed in. The difference from the certificate above
            // has to survive eighteen pixels, and a tick against a keyhole does; a second outline
            // or an added corner would not.
            case "certificate-store":
                Path((12, 2), (20, 5), (20, 11), (12, 22), (4, 11), (4, 5), (12, 2));
                p.DrawEllipse(10, 8, 4, 4);
                Line(12, 12, 12, 16);
                break;
            // A pass: the generic credential, worn by a kind a plugin described — the window got
            // fields and a title for it, never a picture. A card with a portrait and the name
            // beside it is what an identity card looks like, and it survives eighteen pixels; a
            // second line of text does not, it merges into the first.
            case "credential":
                p.DrawRoundedRect(2, 5, 20, 14, 2, 2);
                p.DrawEllipse(5, 10, 5, 5);
                Line(13, 12, 19, 12);
                break;
            // A tortoise: what a credential that reads TortoiseSVN's own cache wears. The mark of a
            // program is worth borrowing as long as it is drawn here rather than copied — a shell,
            // a head and two legs is the whole animal, and it stays one animal at eighteen pixels.
            case "tortoise":
                p.DrawArc(3, 6, 16, 14, 0, 180 * 16);
                Line(3, 13, 19, 13);
                p.DrawEllipse(18, 9, 5, 5);
                Line(6, 13, 6, 18); Line(14, 13, 14, 18);
                Path((3, 13), (1, 16));
                break;
            // Signing in: a door with an arrow going through it. What a single sign-on credential
            // wears — the thing it does rather than the company that runs it.
            case "sign-in":
                Path((13, 3), (4, 3), (4, 21), (13, 21));
                Line(11, 12, 21, 12);
                Path((17, 8), (21, 12), (17, 16));
                break;
            case "folder": Path((3, 7), (3, 19), (21, 19), (21, 7), (11, 7), (9, 4), (3, 4), (3, 7)); break;
            // A pencil, and only a pencil. It used to carry three hairline strokes beside it, to
            // suggest the layer it edits; at the sixteen pixels a layer row gives this they were
            // noise around the one shape that says what the action is.
            case "edit-layer":
                Path((4, 20), (4, 16), (15, 5), (19, 9), (8, 20), (4, 20));
                Path((12.5, 7.5), (16.5, 11.5));
                break;
            // A cylinder: somewhere things are kept. It used to be a square with a magnifier
            // hanging off the corner, which is two ideas and neither survives the small size — a
            // cache's whole job is to be a store.
            case "disk-cache":
                p.DrawEllipse(4, 2, 16, 6);
                Path((4, 5), (4, 19)); Path((20, 5), (20, 19));
                p.DrawArc(4, 16, 16, 6, 0, -180 * 16);
                break;
            // A chip: body, die, and three pins on the top and the bottom — a package with two
            // rows of legs, which is what a chip looks like. Pins on all four sides made it a
            // starburst at the small sizes, and put it one step from the cog beside it.
            case "memory-chip":
                p.DrawRoundedRect(6, 6, 12, 12, 2, 2); p.DrawRect(9, 9, 6, 6);
                for (int pin = 8; pin <= 16; pin += 4)
                { Line(pin, 3, pin, 6); Line(pin, 18, pin, 21); }
                break;
            case "predict-path":
                p.DrawEllipse(3, 15, 5, 5);
                Path((8, 17.5), (11, 17.5), (11, 7), (18, 7)); Path((15, 4), (18, 7), (15, 10));
                Line(20, 13, 20, 19); Line(17, 16, 23, 16);
                break;
            case "warm-listings":
                // A folder with the listing being filled in ahead of the reader.
                Path((3, 7), (3, 19), (21, 19), (21, 7), (11, 7), (9, 4), (3, 4), (3, 7));
                Path((8, 12.5), (12, 8.5), (16, 12.5)); Line(12, 9, 12, 16);
                break;
            case "repository":
                p.DrawEllipse(3, 3, 5, 5); p.DrawEllipse(3, 16, 5, 5); p.DrawEllipse(16, 3, 5, 5);
                Line(5, 8, 5, 16); Path((18.5, 8), (18.5, 11), (5.5, 15));
                break;
            case "plus": Line(12, 5, 12, 19); Line(5, 12, 19, 12); break;
            case "chevron-right": Path((10, 6), (15, 12), (10, 18)); break;
            case "chevron-down": Path((6, 10), (12, 15), (18, 10)); break;
            case "check": p.DrawEllipse(3, 3, 18, 18); Path((7, 12), (10, 15), (17, 8)); break;
            case "alert": Path((12, 3), (22, 20), (2, 20), (12, 3)); Line(12, 9, 12, 13); Line(12, 16, 12, 17); break;
            case "play": Path((7, 4), (20, 12), (7, 20), (7, 4)); break;
            case "stop": p.DrawRoundedRect(5, 5, 14, 14, 2, 2); break;
            case "chart": Line(3, 3, 3, 21); Line(3, 21, 21, 21); Path((6, 16), (10, 10), (14, 14), (20, 5)); break;
            case "trash": Line(4, 6, 20, 6); Path((6, 6), (7, 21), (17, 21), (18, 6)); Path((9, 6), (9, 3), (15, 3), (15, 6)); Line(10, 10, 10, 17); Line(14, 10, 14, 17); break;
            case "refresh": p.DrawArc(4, 4, 16, 16, 40 * 16, 280 * 16); Path((20, 3), (20, 9), (14, 9)); break;
            case "export": Path((4, 15), (4, 20), (20, 20), (20, 15)); Line(12, 16, 12, 5); Path((7.5, 9.5), (12, 5), (16.5, 9.5)); break;
            // A filled disc, meant to be asked for in the recording red: a live recording is a
            // state, not a button, and a dot is what says so at a glance.
            case "record":
                p.SetBrush(new QBrush(new QColor(color)));
                p.SetPen(new QPen(new QColor(color)));
                p.DrawEllipse(7, 7, 10, 10);
                break;
            // A floppy: the body, the shutter on top and the label plate, in that order of size so
            // the small size keeps the silhouette instead of filling in.
            case "save":
                p.DrawRoundedRect(4, 3, 16, 18, 3, 3);
                Path((8, 3), (8, 9), (16, 9), (16, 3));
                p.DrawRoundedRect(8, 13, 8, 8, 1, 1);
                break;
            // A cog: rim, hub, and eight teeth drawn thick and outside the rim. Thin rays around a
            // small circle is a sun — the difference is where the weight sits and that a cog has a
            // hole in the middle.
            case "settings":
                p.DrawEllipse(5, 5, 14, 14); p.DrawEllipse(10, 10, 4, 4);
                using (var teeth = new QPen(new QColor(color)))
                {
                    teeth.SetWidthF(2.6);
                    teeth.SetCapStyle(Qt.PenCapStyle.FlatCap);
                    p.SetPen(teeth);
                    for (int i = 0; i < 8; i++)
                    { p.Save(); p.Translate(12, 12); p.Rotate(i * 45); Line(0, -7, 0, -10); p.Restore(); }
                }
                p.SetPen(pen);
                break;
            default: Path((3, 8), (12, 3), (21, 8), (12, 13), (3, 8)); Path((3, 12), (12, 17), (21, 12)); break;
        }
        p.End();
        using var pixmap = QPixmap.FromImage(image);
        var icon = new QIcon(pixmap);
        Cache[key] = icon;
        return icon;
    }
}
