using PolyMount.Core;

namespace PolyMount.Plugins.Svn;

/// <summary>Standalone sample plugin. Only the Core contracts are required.</summary>
public sealed class SvnLayerPlugin : ILayerPlugin
{
    public void Register()
    {
        LayerPresetRegistry.Register(new()
        {
            Id = "svn-workspace",
            Titles = new() { ["zh-CN"] = "SVN + 读取缓存 + 预测预载 + 本地修改 + 内存缓存", ["en"] = "SVN + read cache + predictive preload + overlay + memory cache" },
            Layers = new()
            {
                new() { Id = "memory-cache", Type = "memory-cache" },
                new() { Id = "overlay", Type = "overlay" },
                new() { Id = "predict", Type = "prediction-cache", Parameters = new()
                    { ["CapacityBytes"] = System.Text.Json.JsonSerializer.SerializeToElement(16L * 1048576) } },
                new() { Id = "cache", Type = "cache" },
                new() { Id = "svn", Type = "svn", Source = new() { Type = "svn" } }
            }
        });
        SourceRegistry.Register("svn", (config, state) =>
            SvnLayer.Create(config.Url, config.TrustServerCertFailures, Path.Combine(state, "pin.txt"), config.Username, config.Password),
            config =>
            {
                if (!Uri.TryCreate(config.Url, UriKind.Absolute, out _) || config.PinnedRevision < 0)
                    throw new ArgumentException("SVN requires an absolute repository URL and nonnegative revision");
            }, new LayerDescriptor
            {
                Type = "svn", Title = new("SVN repository", "SVN 仓库"), Icon = "drive",
                Description = new("Reads repository objects at a pinned revision without checkout.", "无需 checkout，读取指定版本的仓库对象。"),
                Fields = new()
                {
                    new() { Path = "Source.Url", Title = new("Repository URL", "仓库地址"), Required = true },
                    new() { Path = "Source.PinnedRevision", Kind = LayerFieldKind.Integer, Minimum = 0,
                        Title = new("Pinned revision (0 = HEAD)", "固定版本（0 = HEAD）") },
                    new() { Path = "Source.TrustServerCertFailures", Title = new("Trusted certificate failures", "信任的证书错误") },
                    new() { Path = "Source.Username", Title = new("Username", "用户名"), EmptyIsNull = true },
                    new() { Path = "Source.Password", Kind = LayerFieldKind.Secret, Title = new("Password", "密码"), Trim = false, EmptyIsNull = true }
                },
                Actions = new() { new() { Command = PolyMount.Core.Ipc.ControlCommands.RefreshHead,
                    Title = new("Refresh HEAD", "更新到 HEAD"), Confirmation = new("Refresh this layer to HEAD?", "将此层更新到 HEAD？") } }
            });
    }
}
