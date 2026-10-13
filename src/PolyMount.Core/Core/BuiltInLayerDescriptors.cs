using PolyMount.Core.Ipc;

namespace PolyMount.Core;

internal static class BuiltInLayerDescriptors
{
    public static LayerDescriptor Storage(string type, bool cache) => new()
    {
        Type = type,
        Icon = cache ? "disk-cache" : "edit-layer",
        Title = cache ? new("Read cache", "读取缓存") : new("Local changes", "本地修改"),
        Description = cache ? new("Caches reads from lower layers.", "缓存下层读取的对象。") : new("Stores local file changes.", "持久保存本地文件修改。"),
        Fields = new List<LayerFieldDescriptor>()
        {
            new() { Path = "Path", Title = new("Storage directory", "存储目录"), Description = new("Leave empty to use the instance's default storage directory.", "留空使用实例默认存储目录。") },
            new() { Path = "Compression", Kind = LayerFieldKind.Boolean, Title = new("Brotli compression", "Brotli 压缩") }
        }.Concat(cache ? new[] { new LayerFieldDescriptor { Path = "CacheCapacityBytes", Kind = LayerFieldKind.Integer,
            Title = new("Cache capacity (MiB)", "缓存容量（MiB）"), Scale = 1048576, Minimum = 1, Maximum = long.MaxValue / 1048576 } } : Array.Empty<LayerFieldDescriptor>()).ToList(),
        Actions = new() { new() { Command = cache ? ControlCommands.ClearCache : ControlCommands.ClearLocalChanges,
            Title = cache ? new("Clear cache", "清除此层缓存") : new("Discard local changes", "丢弃本层修改"),
            Confirmation = cache ? new("Clear cached objects in this layer?", "清除此层缓存对象？") : new("Discard all local changes in this layer?", "丢弃此层的全部本地修改？") } }
    };

    public static LayerDescriptor Memory() => new()
    {
        Type = "memory-cache", Title = new("Memory cache", "内存缓存"), Icon = "memory-chip",
        Description = new("Caches directory listings and repeated file range reads and writes. Place above the writable layer. Directory snapshots track committed writes; flush/close writes through to durable storage.",
            "缓存目录清单并加速文件片段的重复读写。请放在可写层上方；目录清单随已提交修改更新，Flush/关闭时提交到持久层。"),
        Fields = new()
        {
            new() { Path = "Parameters.CapacityBytes", Kind = LayerFieldKind.Integer, Scale = 1048576, Minimum = 1,
                Maximum = long.MaxValue / 1048576, DefaultValue = "16", Title = new("Memory capacity (MiB)", "内存容量（MiB）") },
            new() { Path = "Parameters.BlockSize", Kind = LayerFieldKind.Choice, DefaultValue = "65536",
                Title = new("Block size", "块大小"), Choices = new() {
                    new() { Value = "4096", Title = new("4 KiB", "4 KiB") },
                    new() { Value = "16384", Title = new("16 KiB", "16 KiB") },
                    new() { Value = "65536", Title = new("64 KiB", "64 KiB") },
                    new() { Value = "262144", Title = new("256 KiB", "256 KiB") },
                    new() { Value = "1048576", Title = new("1 MiB", "1 MiB") } } }
        },
        Actions = new() { new() { Command = ControlCommands.ClearCache, Title = new("Clear memory cache", "清空内存缓存"),
            Confirmation = new("Write dirty blocks back to staging and clear the memory cache?", "将脏块写回暂存句柄并清空内存缓存？") } }
    };

    public static LayerDescriptor Directory() => new()
    {
        Type = "dir", Title = new("Local directory", "本地目录"), Icon = "folder",
        Description = new("Maps an existing local directory.", "映射已有本地目录。"),
        Fields = new()
        {
            new() { Path = "Source.Path", Title = new("Directory path", "目录路径"), Required = true },
            new() { Path = "Source.Writable", Kind = LayerFieldKind.Boolean, Title = new("Write directly to source", "直接写入源目录") }
        }
    };
}
