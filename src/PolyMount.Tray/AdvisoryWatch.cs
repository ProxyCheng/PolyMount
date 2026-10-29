using PolyMount.Core;
using PolyMount.Core.Ipc;

namespace PolyMount.Tray;

/// <summary>One plugin notice and the instance that offered it: the command it carries has to go
/// back to the same process that answered.</summary>
internal sealed record Advisory(string Instance, PluginNotice Notice);

/// <summary>
/// What the tray keeps in front of the reader: which notices stand right now, and what a corner
/// notification says about one.
///
/// It is separated from the window because both halves are rules rather than plumbing — a notice
/// that stops being reported has to leave the menu, or the menu offers an action that can only
/// fail; a reader's answer has to outlive the pass that keeps re-reporting it — and a rule that can
/// be asserted is worth more than one that can only be looked at.
/// </summary>
internal static class AdvisoryQueue
{
    /// <summary>The notices that stand: reported by this pass, not dismissed for good, not
    /// answered already. One entry per id, in the order the instances gave them.</summary>
    internal static List<Advisory> Current(IEnumerable<Advisory> reported, Func<string, bool> dismissed, ISet<string> answered) =>
        reported
            .Where(item => !dismissed(item.Notice.Id) && !answered.Contains(item.Notice.Id))
            .GroupBy(item => item.Notice.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();

    /// <summary>The first paragraph of a notice body: what fits in a corner notification. The rest
    /// is for the dialog, where there is room for it.</summary>
    internal static string Summary(string body)
    {
        int stop = body.IndexOf("\n\n", StringComparison.Ordinal);
        return stop > 0 ? body[..stop] : body;
    }

    /// <summary>
    /// The notices whose action the plugin asked to have carried out without a click: automatic
    /// ones that are in front and have not been run since they last stood. The record is emptied
    /// of ids that are no longer in front, so a condition that clears and returns — a token that
    /// expires again tomorrow — is acted on again rather than remembered forever. A notice that
    /// asks for nothing is information even when it is marked automatic.
    /// </summary>
    internal static List<Advisory> DueForAutomatic(IReadOnlyList<Advisory> current, HashSet<string> ran)
    {
        ran.RemoveWhere(id => current.All(item =>
            !string.Equals(item.Notice.Id, id, StringComparison.OrdinalIgnoreCase)));
        var due = new List<Advisory>();
        foreach (var item in current)
        {
            if (!item.Notice.Automatic || item.Notice.Action == null) continue;
            if (ran.Add(item.Notice.Id)) due.Add(item);
        }
        return due;
    }

    /// <summary>Which notice to announce now, if any: one that has never been announced, or one
    /// that is still unanswered and has waited long enough for a second try. A corner notification
    /// is ten seconds long and can be swallowed whole, so saying it once is not the same as being
    /// told; saying it more than <paramref name="limit"/> times is nagging. The record is updated
    /// as the decision is made, and only one notice is announced per call so that a pass which
    /// finds several does not stack balloons.</summary>
    internal static Advisory? Due(IReadOnlyList<Advisory> current, Dictionary<string, (DateTime At, int Count)> announced,
        DateTime now, TimeSpan repeatAfter, int limit)
    {
        foreach (var item in current)
        {
            if (!announced.TryGetValue(item.Notice.Id, out var seen))
            {
                announced[item.Notice.Id] = (now, 1);
                return item;
            }
            if (seen.Count < limit && now - seen.At >= repeatAfter)
            {
                announced[item.Notice.Id] = (now, seen.Count + 1);
                return item;
            }
        }
        return null;
    }
}

/// <summary>
/// Asks every running instance what its plugins want said.
///
/// This exists because a plugin can know something a window cannot. The shim plugin is the first
/// case: whether a program is about to fail on a mount is decided by what the program's own image
/// can do, which is knowledge the plugin has and Core deliberately does not. So the question goes
/// to the instance, and the answer comes back as notices — title, body, an action label and the
/// command that action sends — and this file shows them without reading them.
///
/// A pass is a round trip per instance, and a plugin answering may have to look at files, so it
/// runs on its own thread and the timer only starts one and collects the last.
/// </summary>
internal sealed class AdvisoryWatch
{
    private Task<List<Advisory>>? _running;

    /// <summary>Starts a pass unless one is running or its answer has not been collected yet —
    /// starting over a finished-but-unread answer would throw that answer away.</summary>
    internal void Start()
    {
        if (_running != null) return;
        _running = Task.Run(() => Poll(null));
    }

    /// <summary>The finished pass, or null while it is still running. Called on a short beat while
    /// a pass is in flight, so what a pass finds is shown when it is found rather than at the next
    /// poll — the scanning is the only slow part, and waiting a second turn on top of it is a
    /// delay nothing asked for.</summary>
    internal IReadOnlyList<Advisory>? Take()
    {
        if (_running is not { IsCompleted: true }) return null;
        var finished = _running.Status == TaskStatus.RanToCompletion
            ? _running.Result
            : new List<Advisory>();
        _running = null;
        return finished;
    }

    /// <summary>One instance, asked now and answered now. Used after a reader accepted a notice:
    /// the plugin usually has a second thing to say about what happens next, and waiting a whole
    /// timer interval to say it reads as nothing having happened.</summary>
    internal IReadOnlyList<Advisory> PollNow(string instance) => Poll(new[] { instance });

    private static List<Advisory> Poll(IReadOnlyList<string>? only)
    {
        var found = new List<Advisory>();
        IReadOnlyList<string> instances;
        if (only != null) instances = only;
        else
        {
            try
            {
                var status = InstanceView.Status();
                if (!status.Ok) return found;
                instances = status.Mounts.Where(mount => mount.Mounted).Select(mount => mount.Name).ToArray();
            }
            catch (Exception) { return found; }
        }

        foreach (string instance in instances)
        {
            try
            {
                var response = InstanceDirectory.ClientFor(instance).Notices();
                if (response.Ok)
                    foreach (var notice in response.Notices)
                        found.Add(new Advisory(instance, notice));
            }
            catch (Exception) { /* an instance that cannot be asked has nothing to say */ }
        }
        return found;
    }
}
