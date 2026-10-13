using QtSharp.Api;

namespace PolyMount.Tray;

/// <summary>Original, consistently stroked icons drawn with Qt; no font or SVG plugin dependency.</summary>
internal static class TrayIcons
{
    private static readonly Dictionary<string, QIcon> Cache = new();
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

    public static QIcon Get(string name, string color = "#526786")
    {
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
            case "app":
                p.SetBrush(new QBrush(new QColor("#2563eb")));
                p.SetPen(new QPen(new QColor("#2563eb")));
                p.DrawRoundedRect(1, 1, 22, 22, 6, 6);
                p.SetPen(pen);
                p.SetBrush(new QBrush(Qt.BrushStyle.NoBrush));
                Path((5, 9), (12, 5), (19, 9), (12, 13), (5, 9));
                Path((5, 13), (12, 17), (19, 13));
                Path((5, 17), (12, 21), (19, 17));
                break;
            case "layers": Path((3, 8), (12, 3), (21, 8), (12, 13), (3, 8)); Path((3, 12), (12, 17), (21, 12)); Path((3, 16), (12, 21), (21, 16)); break;
            case "drive": p.DrawRoundedRect(3, 5, 18, 14, 3, 3); Line(3, 14, 21, 14); Line(16, 17, 18, 17); break;
            case "folder": Path((3, 7), (3, 19), (21, 19), (21, 7), (11, 7), (9, 4), (3, 4), (3, 7)); break;
            case "plus": Line(12, 5, 12, 19); Line(5, 12, 19, 12); break;
            case "check": p.DrawEllipse(3, 3, 18, 18); Path((7, 12), (10, 15), (17, 8)); break;
            case "alert": Path((12, 3), (22, 20), (2, 20), (12, 3)); Line(12, 9, 12, 13); Line(12, 16, 12, 17); break;
            case "play": Path((7, 4), (20, 12), (7, 20), (7, 4)); break;
            case "stop": p.DrawRoundedRect(5, 5, 14, 14, 2, 2); break;
            case "chart": Line(3, 3, 3, 21); Line(3, 21, 21, 21); Path((6, 16), (10, 10), (14, 14), (20, 5)); break;
            case "trash": Line(4, 6, 20, 6); Path((6, 6), (7, 21), (17, 21), (18, 6)); Path((9, 6), (9, 3), (15, 3), (15, 6)); Line(10, 10, 10, 17); Line(14, 10, 14, 17); break;
            case "refresh": p.DrawArc(4, 4, 16, 16, 40 * 16, 280 * 16); Path((20, 3), (20, 9), (14, 9)); break;
            case "settings": p.DrawEllipse(8, 8, 8, 8); for (int i = 0; i < 8; i++) { p.Save(); p.Translate(12, 12); p.Rotate(i * 45); Line(0, 7, 0, 10); p.Restore(); } break;
        }
        p.End();
        using var pixmap = QPixmap.FromImage(image);
        var icon = new QIcon(pixmap);
        Cache[key] = icon;
        return icon;
    }
}
