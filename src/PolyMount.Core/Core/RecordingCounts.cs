namespace PolyMount.Core.Ipc;

/// <summary>What one operation did over a recording: how many times, and how long in total.
/// The counts here are exact — every request the instance served is in them, logged or not.</summary>
public sealed class OperationCount
{
    public string Operation { get; set; } = "";
    public long Count { get; set; }
    /// <summary>How many of them took at least a tenth of a second — the threshold at which the
    /// sampled log keeps a line of its own about a request.</summary>
    public long Over100 { get; set; }
    public double TotalMs { get; set; }
    public double MaxMs { get; set; }
}

/// <summary>What one path cost over a recording. The list is ranked by total time, so it is where
/// the answer to "which of these was expensive" is.</summary>
public sealed class PathCount
{
    public string Operation { get; set; } = "";
    public string Path { get; set; } = "";
    public long Count { get; set; }
    public double TotalMs { get; set; }
    public double MaxMs { get; set; }
}

/// <summary>Everything a recording counted. This block is what makes a recording worth taking
/// without writing a line about every request: the sampled log answers "what did this one request
/// do", and these answer "what did the whole window do" — exactly, for the cost of two increments.</summary>
public sealed class RecordingCounts
{
    public long Events { get; set; }
    public long Over100 { get; set; }
    public double TotalMs { get; set; }
    public double MaxMs { get; set; }
    /// <summary>Every operation, ranked by total time.</summary>
    public List<OperationCount> Operations { get; set; } = new();
    /// <summary>The paths that cost the most, ranked by total time.</summary>
    public List<PathCount> Paths { get; set; } = new();
    /// <summary>Distinct paths past the ones listed, and what they were worth together. A recording
    /// that touched tens of thousands of paths says so rather than quietly ranking the top few.</summary>
    public long PathsUnlisted { get; set; }
    public long EventsUnlisted { get; set; }
    public double MsUnlisted { get; set; }
}
