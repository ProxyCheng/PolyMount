using System.Runtime.InteropServices;

namespace PolyMount.Core;

public static class WinFspBootstrap
{
    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetDllDirectory(string lpPathName);

    private static bool _done;
    private static readonly object Gate = new();

    public static void Ensure(string winFspBin)
    {
        lock (Gate)
        {
            if (_done) return;
            if (Directory.Exists(winFspBin)) SetDllDirectory(winFspBin);
            _done = true;
        }
    }
}
