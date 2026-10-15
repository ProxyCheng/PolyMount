using PolyMount.Core;
using PolyMount.Core.Ipc;

namespace PolyMount.Service;

/// <summary>Something that answers the control protocol: the supervisor, or one instance's worker.
///
/// Both ends are spoken the same way — a line of JSON in, a line of JSON out — so the pipe server
/// knows nothing about either, and each process decides what it is the right one to answer.</summary>
public interface IControlHost
{
    ControlResponse Handle(ControlRequest request);
}

/// <summary>What every response carries whatever it was asked: the plugins and layer types this
/// process loaded, which is what a front-end needs to draw a chain built from types it may not
/// know about.</summary>
internal static class ControlEnvelope
{
    public static ControlResponse New() => new()
    {
        SourceTypes = SourceRegistry.Types.ToList(),
        LayerDescriptors = LayerRegistry.Descriptors.ToList(),
        Presets = LayerPresetRegistry.All.ToList()
    };
}

/// <summary>The diagnostics an export can carry from one process: a rolled log's whole family
/// rather than a single name, described with the path it should have inside a bundle. A front-end
/// copies the file itself, so what is reported here is where it lives.</summary>
internal static class LogInventory
{
    /// <summary>Every segment of a rolled log, oldest first.</summary>
    public static IEnumerable<string> Family(string directory, string fileName)
    {
        try { return Directory.GetFiles(directory, Path.GetFileNameWithoutExtension(fileName) + "*.log").Order(StringComparer.Ordinal).ToList(); }
        catch (DirectoryNotFoundException) { return Array.Empty<string>(); }
    }

    public static LogFile Describe(string path, string group)
    {
        long bytes = 0;
        try { bytes = new FileInfo(path).Length; }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return new LogFile { Name = group + "/" + Path.GetFileName(path), Source = path, Bytes = bytes };
    }
}
