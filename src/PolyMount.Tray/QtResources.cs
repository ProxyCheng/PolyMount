using QtSharp.QtCore;
using QtSharp.QtGui;
using QtSharp.QtWidgets;

namespace PolyMount.Tray;

internal static class QtResources
{
    private static bool _registered;
    public static void Register()
    {
        if (_registered) return;
        string file = Path.Combine(AppContext.BaseDirectory, "resources", "polymount.rcc");
        if (!QResource.RegisterResource(file)) throw new InvalidOperationException("Cannot register Qt resources: " + file);
        _registered = true;
    }
    public static string ReadText(string path)
    {
        Register();
        using var file = new QFile(path);
        if (!file.Open(QIODeviceBase.OpenMode.ReadOnly)) throw new IOException("Cannot open Qt resource: " + path);
        using var reader = new QTextStream(file);
        return reader.ReadAll();
    }
}
