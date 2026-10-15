namespace PolyMount.Tray;

/// <summary>A stretch of a recording that is known to be missing, and why.</summary>
internal readonly record struct TailGap(string FromUtc, string ToUtc, string Reason, long? MissingBytes);

/// <summary>What one source contributed to the session's local copy.</summary>
internal sealed record TailedSource(string Name, string Source, string Part, long Bytes, long Lines);

/// <summary>
/// Reads a growing log while it is being written and keeps a copy of its own, so a recording
/// survives whatever the service does to its files: Serilog rolls them at a size, a restart starts
/// a new one, and a copy taken at the end would hold only the last of them.
///
/// Only whole lines are ever committed — the writer may be inside the last one — and a roll
/// becomes a file boundary rather than a synthetic line inside the log.
/// </summary>
internal sealed class RecordingTail
{
    /// <summary>The window one backwards scan or one copy reads at a time.</summary>
    private const int BlockBytes = 64 * 1024;

    private sealed class Part(string source, string target)
    {
        public readonly string Source = source;
        public readonly string Target = target;
        public long Offset;
        public long Bytes, Lines;
        /// <summary>Skip whatever this source already holds, once, when it is first seen.</summary>
        public bool SkipToEnd;
    }

    private readonly Dictionary<string, Part> _parts = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<TailGap> _gaps = new();
    private int _next;
    private DateTime _lastDrainUtc;

    /// <summary>Where the session's own copies go.</summary>
    public RecordingTail(string directory) => Directory = directory;

    public string Directory { get; }
    /// <summary>The group this tail's copies belong to, which is one folder inside the bundle's
    /// logs directory (<c>TX4Package</c>, <c>service</c>).</summary>
    public string Group { get; init; } = "mount";
    /// <summary>What the local copies are named. A bundle groups its logs by the instance, and a
    /// reader's <c>requests*</c> habit should find a recording the same way.</summary>
    public string Prefix { get; init; } = "requests";
    public long Bytes { get; private set; }
    public long Lines { get; private set; }

    /// <summary>Set when the copy reached its own ceiling: the recording is no longer complete,
    /// and that has to be said rather than left to be discovered later.</summary>
    public bool Truncated { get; private set; }

    public IReadOnlyList<TailGap> Gaps => _gaps;

    /// <summary>What the local copy may reach before it stops taking more.</summary>
    public long MaxBytes { get; init; } = 2L * 1024 * 1024 * 1024;

    /// <summary>Start each source that already exists at its current end, for a log that holds
    /// history the recording is not about: a service log running for days is context, and only
    /// what it writes from here on belongs to this recording. Sources that appear later — a
    /// rolled segment, or a channel's own file — are read from their beginning, because whatever
    /// is in them was written while the recording was running.</summary>
    public bool StartAtEnd { get; init; }

    private bool _firstDrain = true;

    /// <summary>The sources that actually contributed, in the order they appeared — the order a
    /// bundle presents them in. A source nothing was ever written to is not listed: it produced no
    /// file, and naming it would have a reader look for one.</summary>
    public IReadOnlyList<TailedSource> Sources => _parts.Values
        .Where(part => part.Bytes > 0)
        .OrderBy(part => part.Target, StringComparer.Ordinal)
        .Select(part => new TailedSource(Path.GetFileName(part.Source), part.Source,
            Group + "/" + Path.GetFileName(part.Target), part.Bytes, part.Lines))
        .ToList();

    /// <summary>Whether this session has already taken a copy of that source.</summary>
    public bool Knows(string source) => _parts.ContainsKey(source);

    /// <summary>Records a stretch the copy does not have, for a break the reader should be able to
    /// see rather than infer: the recording was attached again, so what ran in between is missing.</summary>
    public void NoteBreak(DateTime utcNow, string reason) => NoteGap(utcNow, reason, null);

    /// <summary>Copies whatever is new in each source, oldest first. A source seen for the first
    /// time becomes the next part file, so a roll boundary stays a file boundary.</summary>
    public long Drain(IReadOnlyList<string> sources, DateTime utcNow)
    {
        if (Truncated) return 0;
        long copied = 0;
        foreach (string source in sources)
        {
            copied += DrainOne(source, utcNow);
            if (Truncated) break;
        }
        _lastDrainUtc = utcNow;
        _firstDrain = false;
        return copied;
    }

    private long DrainOne(string source, DateTime utcNow)
    {
        if (!_parts.TryGetValue(source, out var part))
        {
            _parts[source] = part = new Part(source, Path.Combine(Directory,
                Prefix + "-" + _next++.ToString("000", System.Globalization.CultureInfo.InvariantCulture) + ".log"));
            part.SkipToEnd = StartAtEnd && _firstDrain;
        }
        long length;
        try { length = new FileInfo(source).Length; }
        catch (FileNotFoundException) { return 0; }
        catch (IOException) { return 0; }
        if (part.SkipToEnd)
        {
            part.SkipToEnd = false;
            part.Offset = length;
            return 0;
        }
        if (length < part.Offset)
        {
            // Replaced under us, not rolled: everything past the last committed line is gone.
            NoteGap(utcNow, "source replaced", null);
            part.Offset = 0;
        }
        if (length == part.Offset) return 0;
        try
        {
            using var from = new FileStream(source, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, BlockBytes, FileOptions.SequentialScan);
            long commit = LastCompleteLine(from, part.Offset, length);
            if (commit <= part.Offset) return 0;
            from.Seek(part.Offset, SeekOrigin.Begin);
            var (written, lines) = Append(from, part.Target, commit - part.Offset);
            part.Offset += written;
            part.Bytes += written;
            part.Lines += lines;
            Bytes += written;
            Lines += lines;
            if (Bytes >= MaxBytes) Truncated = true;
            return written;
        }
        catch (IOException) { return 0; }
        catch (UnauthorizedAccessException) { return 0; }
    }

    /// <summary>The end of the last complete line in <c>[from, to)</c>, or <paramref name="from"/>
    /// when the range holds no newline yet. Scans backwards in blocks, so the tail being written
    /// right now costs one block rather than the whole file.</summary>
    private static long LastCompleteLine(FileStream stream, long from, long to)
    {
        var block = new byte[BlockBytes];
        long scan = to;
        while (scan > from)
        {
            long start = Math.Max(from, scan - block.Length);
            stream.Seek(start, SeekOrigin.Begin);
            int read = stream.Read(block, 0, (int)(scan - start));
            for (int i = read - 1; i >= 0; i--)
                if (block[i] == (byte)'\n') return start + i + 1;
            scan = start;
        }
        return from;
    }

    /// <summary>Appends the range to the part file, counting the lines as it goes: a UTF-8
    /// continuation byte is never 0x0A, so counting newline bytes counts lines.</summary>
    private static (long Bytes, long Lines) Append(FileStream from, string target, long count)
    {
        using var to = new FileStream(target, FileMode.Append, FileAccess.Write, FileShare.Read);
        var buffer = new byte[BlockBytes];
        long written = 0, lines = 0;
        while (written < count)
        {
            int read = from.Read(buffer, 0, (int)Math.Min(buffer.Length, count - written));
            if (read <= 0) break;
            for (int i = 0; i < read; i++)
                if (buffer[i] == (byte)'\n') lines++;
            to.Write(buffer, 0, read);
            written += read;
        }
        return (written, lines);
    }

    /// <summary>Records a stretch that is missing from the copy. The window is from the last
    /// drain to this one: the reader is running every second, so that is how much could have been
    /// lost, and it is honest to say so rather than to pretend the log is continuous.</summary>
    private void NoteGap(DateTime utcNow, string reason, long? missingBytes)
    {
        DateTime from = _lastDrainUtc == default ? utcNow : _lastDrainUtc;
        _gaps.Add(new TailGap(from.ToString("O"), utcNow.ToString("O"), reason, missingBytes));
    }
}
