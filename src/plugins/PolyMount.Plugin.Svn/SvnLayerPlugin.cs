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
            Titles = SvnTexts.All("preset.svn-workspace.title"),
            Layers = new()
            {
                // Below the memory cache: warming RAM with listings would spend the memory
                // cache's budget on records the first client enumeration fills on demand
                // anyway, and evict the file blocks that cache exists for. Above the overlay,
                // so the walk still sees local deletions. Here it fills the durable cache —
                // the one that answers a restart.
                new() { Id = "memory-cache", Type = "memory-cache" },
                new() { Id = "warmup", Type = "warmup" },
                new() { Id = "overlay", Type = "overlay" },
                new() { Id = "predict", Type = "prediction-cache", Parameters = new()
                    { ["CapacityBytes"] = System.Text.Json.JsonSerializer.SerializeToElement(16L * 1048576) } },
                new() { Id = "cache", Type = "cache" },
                new() { Id = "svn", Type = "svn", Source = new() { Type = "svn" } }
            }
        });
        SourceRegistry.Register("svn", (config, state) =>
            SvnLayer.Create(config.Url, config.TrustServerCertFailures, Path.Combine(state, "pin.txt"), config.Username,
                config.Password, SvnPrefetchOptions.From(config.Parameters)),
            config =>
            {
                if (!Uri.TryCreate(config.Url, UriKind.Absolute, out _) || config.PinnedRevision < 0)
                    throw new ArgumentException("SVN requires an absolute repository URL and nonnegative revision");
                SvnPrefetchOptions.From(config.Parameters);
            }, new LayerDescriptor
            {
                Type = "svn", Title = SvnTexts.Get("svn.title"), Icon = "repository",
                Description = SvnTexts.Get("svn.description"),
                Fields = new()
                {
                    new() { Path = "Source.Url", Title = SvnTexts.Get("svn.field.url"), Required = true },
                    new() { Path = "Source.PinnedRevision", Kind = LayerFieldKind.Integer, Minimum = 0,
                        Title = SvnTexts.Get("svn.field.revision") },
                    new() { Path = "Source.TrustServerCertFailures", Title = SvnTexts.Get("svn.field.trust") },
                    new() { Path = "Source.Username", Title = SvnTexts.Get("svn.field.username"), EmptyIsNull = true },
                    new() { Path = "Source.Password", Kind = LayerFieldKind.Secret, Title = SvnTexts.Get("svn.field.password"),
                        Trim = false, EmptyIsNull = true },
                    // Directory enumerations are one round trip each, so a walk pays them one
                    // after another unless the next level is fetched while the caller reads
                    // the current one. Bounded, and one level deep.
                    new() { Path = "Source.Parameters.PrefetchConcurrency", Kind = LayerFieldKind.Integer, Minimum = 0, Maximum = 8,
                        DefaultValue = "2", Title = SvnTexts.Get("svn.field.prefetch.concurrency"),
                        Description = SvnTexts.Get("svn.field.prefetch.concurrency.description") },
                    new() { Path = "Source.Parameters.PrefetchQueueDepth", Kind = LayerFieldKind.Integer, Minimum = 1, Maximum = 1024,
                        DefaultValue = "16", Title = SvnTexts.Get("svn.field.prefetch.queue"),
                        Description = SvnTexts.Get("svn.field.prefetch.queue.description") },
                    new() { Path = "Source.Parameters.PrefetchChildren", Kind = LayerFieldKind.Integer, Minimum = 0, Maximum = 256,
                        DefaultValue = "8", Title = SvnTexts.Get("svn.field.prefetch.children"),
                        Description = SvnTexts.Get("svn.field.prefetch.children.description") },
                    // Read-ahead is a burst of requests against a source the reader is using too.
                    // Serving both from one budget is what put a speculative download in front of
                    // a demanded one, so the source is shared out by class instead: the reader
                    // keeps nearly all of it, and read-ahead has a floor of one and this ceiling.
                    new() { Path = "Source.Parameters.SpeculativeConcurrency", Kind = LayerFieldKind.Integer, Minimum = 1, Maximum = 8,
                        DefaultValue = "2", Title = SvnTexts.Get("svn.field.lane"),
                        Description = SvnTexts.Get("svn.field.lane.description") }
                },
                Actions = new() { new() { Command = PolyMount.Core.Ipc.ControlCommands.RefreshHead,
                    Field = "Source.PinnedRevision",
                    Title = SvnTexts.Get("svn.action.refreshHead"),
                    Confirmation = SvnTexts.Get("svn.action.refreshHead.confirmation") } }
            });
    }
}
