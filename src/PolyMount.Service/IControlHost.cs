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
    /// <summary>An answer with nothing in it yet. What a reply carries beyond the command's own
    /// result is attached deliberately, because these replies are polled: the performance window
    /// asks for a window up to ten times a second, and a dashboard once a second. The layer
    /// descriptors and presets are a couple of dozen kilobytes of text in two languages, only ever
    /// read by the configuration editor, and attaching them to every answer made a one-window poll
    /// cost what a whole configuration load costs.</summary>
    public static ControlResponse New() => new();

    /// <summary>The registries a front end needs in order to edit a configuration: which layer
    /// types exist, what each one's fields are, and the presets it can offer. Attached to the
    /// configuration answers, which are asked for once when the editor opens.</summary>
    public static void WithRegistry(ControlResponse response)
    {
        response.SourceTypes = SourceRegistry.Types.ToList();
        response.LayerDescriptors = LayerRegistry.Descriptors.ToList();
        response.Presets = LayerPresetRegistry.All.ToList();
    }
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

    /// <summary>Describes one file. <paramref name="name"/> renames it inside a bundle, which is
    /// how a worker's log stops carrying its instance's identity in its own file name — the folder
    /// it lands in already says whose it is.</summary>
    public static LogFile Describe(string path, string group, string? name = null)
    {
        long bytes = 0;
        try { bytes = new FileInfo(path).Length; }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return new LogFile { Name = group + "/" + (name ?? Path.GetFileName(path)), Source = path, Bytes = bytes };
    }
}
