using System.IO.Compression;
using System.Text.Json;
using PolyMount.Core;

namespace PolyMount.Layers;

/// <summary>Path-keyed persistent KV store. Immutable blobs + atomic catalog publication.</summary>
internal sealed class ObjectDatabase
{
    internal sealed record Record(string Path, VirtualObject Value, string? Blob, bool Compressed, long StoredBytes, long Access);
    private readonly string _root, _catalog;
    private readonly object _gate = new();
    private Dictionary<string, Record> _records;
    private readonly bool _compress;
    public ObjectDatabase(string root, bool compress)
    {
        _root = Path.Combine(root, "objects-v1");
        _catalog = Path.Combine(_root, "catalog.json");
        _compress = compress;
        Directory.CreateDirectory(_root);
        _records = File.Exists(_catalog)
            ? (JsonSerializer.Deserialize<List<Record>>(File.ReadAllText(_catalog)) ?? new()).ToDictionary(r => r.Path, StringComparer.OrdinalIgnoreCase)
            : new(StringComparer.OrdinalIgnoreCase);
    }
    public IReadOnlyList<Record> Records { get { lock (_gate) return _records.Values.ToArray(); } }
    public bool TryGet(string key, out VirtualObject value)
    {
        lock (_gate)
        {
            value = null!;
            if (!_records.TryGetValue(key, out var record)) return false;
            _records[key] = record with { Access = DateTime.UtcNow.Ticks };
            value = record.Value;
            return true;
        }
    }
    public bool TryOpen(string key, out VirtualObject value, out Stream stream)
    {
        lock (_gate)
        {
            stream = null!;
            if (!TryGet(key, out value) || value.Header.IsDir) return false;
            var record = _records[key];
            if (record.Blob == null) throw new InvalidDataException("File object has no data: " + key);
            var file = new FileStream(Path.Combine(_root, record.Blob), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (!record.Compressed) { stream = file; return true; }
            // WinFsp requires seekable handles. Compressed objects are decoded to a temporary
            // disk-backed handle, not an unbounded in-memory byte array.
            var scratch = Scratch();
            try
            {
                using (file)
                using (var decoder = new BrotliStream(file, CompressionMode.Decompress)) decoder.CopyTo(scratch);
                scratch.Position = 0;
                stream = scratch;
                return true;
            }
            catch { scratch.Dispose(); throw; }
        }
    }
    internal static FileStream Scratch() => new(Path.Combine(Path.GetTempPath(), "PolyMount-object-" + Guid.NewGuid().ToString("N")),
        FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read | FileShare.Delete, 131072, FileOptions.DeleteOnClose);

    public void Apply(IReadOnlyList<ObjectChange> changes)
    {
        lock (_gate)
        {
            var next = new Dictionary<string, Record>(_records, StringComparer.OrdinalIgnoreCase);
            var created = new List<string>();
            try
            {
                foreach (var change in changes)
                {
                    if (change.Object == null) { next.Remove(change.Path); continue; }
                    var value = change.Object;
                    string? blob = null;
                    long stored = 0;
                    if (!value.Header.IsDir)
                    {
                        if (change.Content == null) throw new ArgumentException("File update requires content");
                        blob = Guid.NewGuid().ToString("N") + ".blob";
                        created.Add(blob);
                        using (var file = new FileStream(Path.Combine(_root, blob), FileMode.CreateNew, FileAccess.Write))
                        {
                            if (_compress)
                            {
                                using var encoder = new BrotliStream(file, CompressionLevel.Fastest, leaveOpen: true);
                                change.Content.CopyTo(encoder);
                            }
                            else change.Content.CopyTo(file);
                            file.Flush(true);
                        }
                        stored = new FileInfo(Path.Combine(_root, blob)).Length;
                    }
                    else if (value.Children == null) throw new ArgumentException("Directory update requires a complete child list");
                    stored += JsonSerializer.SerializeToUtf8Bytes(value).Length;
                    next[change.Path] = new(change.Path, value, blob, _compress && !value.Header.IsDir, stored, DateTime.UtcNow.Ticks);
                }
                string temp = _catalog + ".tmp-" + Guid.NewGuid().ToString("N");
                try
                {
                    using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write))
                    { JsonSerializer.Serialize(file, next.Values); file.Flush(true); }
                    File.Move(temp, _catalog, true);
                }
                finally { if (File.Exists(temp)) File.Delete(temp); }
                _records = next;
            }
            catch
            {
                foreach (var blob in created) { try { File.Delete(Path.Combine(_root, blob)); } catch { } }
                throw;
            }
            var live = _records.Values.Where(r => r.Blob != null).Select(r => r.Blob!).ToHashSet();
            foreach (var blob in Directory.EnumerateFiles(_root, "*.blob"))
                if (!live.Contains(Path.GetFileName(blob))) { try { File.Delete(blob); } catch { } }
        }
    }
    public void Clear() => Apply(Records.Select(r => new ObjectChange(r.Path, null)).ToArray());
}
