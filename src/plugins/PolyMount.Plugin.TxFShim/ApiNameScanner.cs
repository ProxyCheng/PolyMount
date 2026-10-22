using System.Text;

namespace PolyMount.Plugins.TxFShim;

/// <summary>
/// Finds an API name quoted anywhere in an image.
///
/// The import table is not the whole story: a program that resolves its file APIs at run time —
/// `GetModuleHandleW(L"kernel32.dll")` once, then a name table of `GetProcAddress` calls — imports
/// nothing, and the strings it looks up are all that is left to see. That is exactly the shape of
/// the engine this was written for, so "mentions the name" has to count as evidence alongside
/// "imports it".
///
/// It over-approximates on purpose: a name in a message or a log format string matches too. The
/// cost of that is one process started through a shim it never calls; the cost of missing it is the
/// error this plugin exists to prevent, and the evidence is reported so a reader can tell which
/// kind of match it was.
/// </summary>
internal static class ApiNameScanner
{
    private const int ChunkSize = 1 << 20;

    /// <summary>Enough to hold a name that straddles two reads.</summary>
    private const int Overlap = 64;

    /// <summary>The first API name the image mentions, or null. Names are matched as whole ASCII
    /// bytes; <c>CreateFileTransactedWEx</c> would not match <c>CreateFileTransactedW</c> only if
    /// it never appeared on its own, which is not worth distinguishing here.</summary>
    internal static string? First(string path)
    {
        try
        {
            var needles = TxFImports.Apis.Select(api => Encoding.ASCII.GetBytes(api)).ToArray();
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var buffer = new byte[ChunkSize + Overlap];
            int carry = 0;
            while (true)
            {
                int read = stream.Read(buffer, carry, ChunkSize);
                if (read <= 0) break;
                var window = buffer.AsSpan(0, carry + read);
                for (int index = 0; index < needles.Length; index++)
                    if (window.IndexOf(needles[index]) >= 0) return TxFImports.Apis[index];
                if (read < ChunkSize) break;
                carry = Math.Min(Overlap, window.Length);
                window[^carry..].CopyTo(buffer);
            }
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }
}
