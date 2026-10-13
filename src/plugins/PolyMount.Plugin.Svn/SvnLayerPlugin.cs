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
            Titles = new() { ["zh-CN"] = "SVN + 缓存 + 本地修改", ["en"] = "SVN + cache + overlay" },
            Layers = new()
            {
                new() { Id = "overlay", Type = "overlay" }, new() { Id = "cache", Type = "cache" },
                new() { Id = "svn", Type = "svn", Source = new() { Type = "svn" } }
            }
        });
        SourceRegistry.Register("svn", (config, state) =>
            SvnLayer.Create(config.Url, config.TrustServerCertFailures, Path.Combine(state, "pin.txt"), config.Username, config.Password),
            config =>
            {
                if (!Uri.TryCreate(config.Url, UriKind.Absolute, out _) || config.PinnedRevision < 0)
                    throw new ArgumentException("SVN requires an absolute repository URL and nonnegative revision");
            });
    }
}
