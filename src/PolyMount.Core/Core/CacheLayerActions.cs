using PolyMount.Core.Ipc;

namespace PolyMount.Core;

/// <summary>The management actions every cache layer gets, wherever it was registered from.
///
/// It lives here rather than on <see cref="CacheLayer"/> because it is the host's own vocabulary:
/// the command name the control pipe answers and the translated titles the editor shows. A plugin
/// that registers a cache layer gets these without having to know either — <c>RegisterCacheLayer</c>
/// adds them to the descriptor it was given.
/// </summary>
internal static class CacheLayerActions
{
    public static void Add(LayerDescriptor descriptor)
    {
        if (descriptor.Actions.Any(a => a.Command == ControlCommands.ClearCache)) return;
        descriptor.Actions.Add(new()
        {
            Command = ControlCommands.ClearCache,
            Title = LayerTexts.Get("cache.clear"),
            Confirmation = LayerTexts.Get("cache.clear.confirm")
        });
    }
}
