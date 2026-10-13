using PolyMount.Core.Ipc;

namespace PolyMount.Core;

internal static class BuiltInLayerDescriptors
{
    public static LayerDescriptor Storage(string type, bool cache) => new()
    {
        Type = type,
        Icon = cache ? "disk-cache" : "edit-layer",
        Title = LayerTexts.Get(cache ? "cache.title" : "overlay.title"),
        Description = LayerTexts.Get(cache ? "cache.description" : "overlay.description"),
        Fields = new List<LayerFieldDescriptor>()
        {
            new() { Path = "Path", Title = LayerTexts.Get("storage.path"), Description = LayerTexts.Get("storage.path.description") },
            new() { Path = "Compression", Kind = LayerFieldKind.Boolean, Title = LayerTexts.Get("storage.compression") }
        }.Concat(cache ? new[] { new LayerFieldDescriptor { Path = "CacheCapacityBytes", Kind = LayerFieldKind.Integer,
            Title = LayerTexts.Get("storage.capacity"), Scale = 1048576, Minimum = 1, Maximum = long.MaxValue / 1048576 } } : Array.Empty<LayerFieldDescriptor>()).ToList(),
        Actions = cache ? new() : new() { new() { Command = ControlCommands.ClearLocalChanges,
            Title = LayerTexts.Get("overlay.discard"),
            Confirmation = LayerTexts.Get("overlay.discard.confirm") } }
    };

    public static LayerDescriptor Memory() => new()
    {
        Type = "memory-cache", Title = LayerTexts.Get("memory.title"), Icon = "memory-chip",
        Description = LayerTexts.Get("memory.description"),
        Fields = new()
        {
            new() { Path = "Parameters.CapacityBytes", Kind = LayerFieldKind.Integer, Scale = 1048576, Minimum = 1,
                Maximum = long.MaxValue / 1048576, DefaultValue = "16", Title = LayerTexts.Get("memory.capacity") },
            new() { Path = "Parameters.BlockSize", Kind = LayerFieldKind.Choice, DefaultValue = "65536",
                Title = LayerTexts.Get("memory.block"), Choices = new() {
                    new() { Value = "4096", Title = new("4 KiB", "4 KiB") },
                    new() { Value = "16384", Title = new("16 KiB", "16 KiB") },
                    new() { Value = "65536", Title = new("64 KiB", "64 KiB") },
                    new() { Value = "262144", Title = new("256 KiB", "256 KiB") },
                    new() { Value = "1048576", Title = new("1 MiB", "1 MiB") } } }
        }
    };

    public static LayerDescriptor Directory() => new()
    {
        Type = "dir", Title = LayerTexts.Get("directory.title"), Icon = "folder",
        Description = LayerTexts.Get("directory.description"),
        Fields = new()
        {
            new() { Path = "Source.Path", Title = LayerTexts.Get("directory.path"), Required = true },
            new() { Path = "Source.Writable", Kind = LayerFieldKind.Boolean, Title = LayerTexts.Get("directory.writable") }
        }
    };
}
