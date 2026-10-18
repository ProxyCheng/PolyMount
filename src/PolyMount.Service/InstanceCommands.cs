using PolyMount.Core;
using PolyMount.Core.Ipc;

namespace PolyMount.Service;

/// <summary>
/// The commands an instance answers for itself: what its layers make of a path, what its source is
/// pinned to, what its recording has counted.
///
/// They are asked of an instance rather than of a process — the same questions whether the instance
/// lives in the supervisor's process or in a worker of its own — so they are written once, here,
/// and both callers go through them. What is *not* here is everything that is about the service
/// rather than about one instance: the configuration, the instance list, the lifecycle. Those
/// belong to the supervisor, and a worker refuses them.
/// </summary>
internal static class InstanceCommands
{
    /// <summary>Whether this is a command one instance answers for itself.</summary>
    public static bool Handles(string command) => ByPath(command) || PluginCommands.Handles(command) || command is
        ControlCommands.SetPin or ControlCommands.RefreshHead or ControlCommands.ClearCache
        or ControlCommands.ClearLocalChanges or ControlCommands.RecordingStart
        or ControlCommands.RecordingStatus or ControlCommands.RecordingStop;

    /// <summary>Whether the command addresses the instance by a local path rather than by name.
    /// A path is what a window opened from Explorer has, and which instance it belongs to is read
    /// off the mount points rather than given.</summary>
    public static bool ByPath(string command) => command is
        ControlCommands.Probe or ControlCommands.LayerAction or ControlCommands.ReleaseAbove;

    /// <summary>Applies one instance command. <paramref name="rel"/> is the path relative to the
    /// instance's mount point, for the commands that take one.</summary>
    public static void Apply(MountInstance instance, string rel, ControlRequest request, string winFspBin, ControlResponse response)
    {
        switch (request.Cmd)
        {
            case ControlCommands.SetPin:
                instance.SetPin(request.Rev, request.Layer);
                break;
            case ControlCommands.RefreshHead:
                response.Revision = instance.RefreshHead(request.Layer);
                break;
            case ControlCommands.ClearCache:
                instance.ClearCache(request.Layer);
                break;
            case ControlCommands.ClearLocalChanges:
                instance.ClearLocalChanges(request.Layer ?? "", winFspBin);
                break;
            case ControlCommands.Probe:
                response.Probe = instance.Probe(rel);
                break;
            case ControlCommands.LayerAction:
                response.Probe = instance.ApplyLayerAction(request.Layer ?? "", rel, request.Action ?? "");
                break;
            case ControlCommands.ReleaseAbove:
                response.Probe = instance.ReleaseAbove(rel, request.Boundary, request.ExpectedLayers);
                break;
            case ControlCommands.RecordingStart:
                response.Recording = instance.StartRecording(request.Label);
                break;
            case ControlCommands.RecordingStatus:
                response.Recording = instance.RecordingInfo();
                break;
            case ControlCommands.RecordingStop:
                response.Recording = instance.StopRecording(request.Reason ?? "stopped");
                break;
            default:
                if (PluginCommands.TryApply(new PluginCommand(instance, rel, request, response))) break;
                throw new InvalidOperationException($"{request.Cmd} is not a command an instance answers");
        }
    }

    /// <summary>The path relative to an instance's mount point, or null when the path is not under
    /// it at all — which is how a caller holding several instances finds the one that owns a path.
    /// Nothing here reads the filesystem: a mount point is a string, and a path that does not exist
    /// yet is still under the instance that would hold it.</summary>
    public static string? Under(MountInstance instance, string fullPath)
    {
        string full = fullPath.TrimEnd('\\');
        string root = Path.GetFullPath(instance.MountPoint).TrimEnd('\\');
        if (full.Equals(root, StringComparison.OrdinalIgnoreCase)) return string.Empty;

        string prefix = root + "\\";
        return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? VfsPath.Normalize(full[prefix.Length..]) : null;
    }

    /// <summary>The same, for a caller that has only one instance and no other to try: a path that
    /// is not under it is an error rather than an answer about a different one.</summary>
    public static string RelativeTo(MountInstance instance, string? fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath))
            throw new InvalidOperationException("a path is required");

        string full = Path.GetFullPath(fullPath).TrimEnd('\\');
        return Under(instance, full)
            ?? throw new InvalidOperationException("path is not under " + instance.Name + "'s mount point: " + full);
    }
}
