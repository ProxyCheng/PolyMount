using System.Text.Json;
using System.Text.Json.Serialization;

namespace PolyMount;

public sealed class AppConfig
{
    public string WinFspBin { get; set; } = @"C:\Program Files (x86)\WinFsp\bin";
    public string ControlPipe { get; set; } = "PolyMount.Control";
    public List<MountProfile> Mounts { get; set; } = new();

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    public static AppConfig LoadOrCreate(string path)
    {
        if (File.Exists(path))
            return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path)) ?? Default();

        var fresh = Default();
        Save(path, fresh);
        return fresh;
    }

    public static void Save(string path, AppConfig config)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(config, Options));
    }

    public static AppConfig Default() => new()
    {
        Mounts = new List<MountProfile>
        {
            new()
            {
                Name = "L10",
                MountPoint = @"D:\L10",
                StoreRoot = @"D:\L10.store",
                Source = new SourceConfig
                {
                    Type = "svn",
                    Url = "https://svn-l10.leihuo.netease.com/svn/L10/branches/L10-Messiah/demo1"
                }
            }
        }
    };
}

public sealed class MountProfile
{
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public string MountPoint { get; set; } = "";
    public string StoreRoot { get; set; } = "";
    public long CacheCapacityBytes { get; set; } = 4L * 1024 * 1024 * 1024;
    public SourceConfig Source { get; set; } = new();
}

public sealed class SourceConfig
{
    public string Type { get; set; } = "svn";
    public string Url { get; set; } = "";
    public long PinnedRevision { get; set; } = 0;
    public string TrustServerCertFailures { get; set; } = "unknown-ca,cn-mismatch,expired,not-yet-valid,other";
    public string? Username { get; set; }
    public string? Password { get; set; }

    // Used by the "dir" source: the local directory this layer maps.
    public string Path { get; set; } = "";

    // Used by the "dir" source: allow writing straight through to the backing directory.
    public bool Writable { get; set; } = false;
}
