namespace PolyMount.Core;

/// <summary>Asks the owning layer to fetch one path; the result says whether it actually did
/// (a path the layer already holds is not fetched again). <paramref name="group"/> names the
/// learned set the guess belongs to (a co-read trigger), so the layer can report the entry's
/// fate back to the strategy that issued it; strategies that judge guesses per caller stream
/// leave it null.</summary>
public delegate bool PreloadRequest(string path, long streamId, PreloadOrigin origin, string? group);

/// <summary>
/// What a prediction strategy learns and what it has issued. The layer owns the held cache,
/// eviction, per-stream quota, quality feedback and write invalidation; a model only observes
/// demands and asks the layer to preload a path. Swapping the strategy therefore swaps the
/// learning algorithm, not the machinery around it.
/// </summary>
public interface IPredictionModel : IDisposable
{
    /// <summary>Records one demand from one caller stream; <paramref name="listing"/> says the
    /// caller enumerated a directory rather than read a file. The returned scope, when not null,
    /// is held for the duration of the demand (the successor model uses it to gate read-ahead).</summary>
    IDisposable? Observe(string path, long streamId, bool listing);

    PredictionModelStats Stats { get; }

    /// <summary>Recent guess quality for one stream, 0..1. The owning layer lowers it when
    /// the model's guesses stop paying off.</summary>
    void SetThrottle(long streamId, double value);

    /// <summary>The same dial for a directory expansion, kept apart from the successor table.</summary>
    void SetSpatialThrottle(long streamId, double value);

    /// <summary>The same dial for a co-read/burst model's preloads.</summary>
    void SetBurstThrottle(long streamId, double value);

    /// <summary>Reports the fate of one entry a model nominated under <paramref name="group"/>:
    /// read before it was evicted (<paramref name="paid"/> true), or evicted unread. It is how a
    /// strategy can judge one learned set rather than the whole stream.</summary>
    void ReportGroup(string group, bool paid);

    /// <summary>Drops learned state and held candidates; does not abort already-started I/O.</summary>
    void Reset();
}

/// <summary>The counters a model reports, in the vocabulary the layer/UI already use. A model
/// that has no sibling/spatial/burst work reports zero for those fields.</summary>
public readonly record struct PredictionModelStats(
    long Learned,
    long Predictions,
    long Completed,
    long SiblingPredictions,
    long SiblingCompleted,
    long SpatialPredictions,
    long SpatialCompleted,
    long BurstPredictions,
    long BurstCompleted,
    int ActiveStreams,
    int TrackedDirectories,
    /// <summary>Learned groups currently eligible to preload (co-read triggers whose set has
    /// repeated). Zero for strategies that have no groups.</summary>
    int Groups = 0,
    /// <summary>Groups a strategy closed because their guesses kept going unread.</summary>
    int ClosedGroups = 0);

/// <summary>The few budgets the layer needs from a model to keep its cache and feedback
/// behaviour identical across strategies.</summary>
public sealed record PredictionTuning(long MaxFileBytes, int FeedbackWindowMs, int FeedbackSamples);

/// <summary>A set of independent strategies behind one model surface. Every strategy observes
/// the same demands and asks the same layer to preload, so the layer's cache, quota, throttle
/// and invalidation are shared; only the learned rules are separate.</summary>
public sealed class CompositePrediction : IPredictionModel
{
    private readonly IReadOnlyList<IPredictionModel> _models;

    public CompositePrediction(IReadOnlyList<IPredictionModel> models) => _models = models;

    /// <summary>The strategies behind this one, for a command that belongs to exactly one of
    /// them.</summary>
    public IReadOnlyList<IPredictionModel> Models => _models;

    public IDisposable? Observe(string path, long streamId, bool listing)
    {
        List<IDisposable>? scopes = null;
        foreach (var model in _models)
            if (model.Observe(path, streamId, listing) is { } scope) (scopes ??= new()).Add(scope);
        return scopes == null ? null : new Scopes(scopes);
    }

    public PredictionModelStats Stats
    {
        get
        {
            long learned = 0, predictions = 0, completed = 0, siblingP = 0, siblingC = 0, spatialP = 0, spatialC = 0, burstP = 0, burstC = 0;
            int streams = 0, directories = 0, groups = 0, closed = 0;
            foreach (var model in _models)
            {
                var s = model.Stats;
                learned += s.Learned; predictions += s.Predictions; completed += s.Completed;
                siblingP += s.SiblingPredictions; siblingC += s.SiblingCompleted;
                spatialP += s.SpatialPredictions; spatialC += s.SpatialCompleted;
                burstP += s.BurstPredictions; burstC += s.BurstCompleted;
                streams += s.ActiveStreams; directories += s.TrackedDirectories;
                groups += s.Groups; closed += s.ClosedGroups;
            }
            return new PredictionModelStats(learned, predictions, completed, siblingP, siblingC, spatialP, spatialC, burstP, burstC, streams, directories, groups, closed);
        }
    }

    public void SetThrottle(long streamId, double value) { foreach (var model in _models) model.SetThrottle(streamId, value); }
    public void SetSpatialThrottle(long streamId, double value) { foreach (var model in _models) model.SetSpatialThrottle(streamId, value); }
    public void SetBurstThrottle(long streamId, double value) { foreach (var model in _models) model.SetBurstThrottle(streamId, value); }
    public void ReportGroup(string group, bool paid) { foreach (var model in _models) model.ReportGroup(group, paid); }
    public void Reset() { foreach (var model in _models) model.Reset(); }
    public void Dispose() { foreach (var model in _models) model.Dispose(); }

    private sealed class Scopes(IReadOnlyList<IDisposable> scopes) : IDisposable
    {
        public void Dispose() { foreach (var scope in scopes) scope.Dispose(); }
    }
}
