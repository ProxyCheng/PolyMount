using Microsoft.VisualStudio.TestTools.UnitTesting;
using PolyMount.Core;

namespace PolyMount.Core.Tests;

/// <summary>
/// Who has used the mount lately, kept apart from the counters a reader turns on.
///
/// The question this answers has to be answerable when nobody is watching — the plug-in that
/// advises about programs is asked by a window that may never have opened the performance page —
/// so what is pinned here is that the table fills without a watch, that it says how long ago, and
/// that "nobody" (pid 0, the service's own read-ahead) is not a program.
/// </summary>
[TestClass]
public class CallerWatchTests
{
    [TestMethod]
    public void A_caller_is_remembered_while_nothing_is_watched()
    {
        long slot = 10_000;
        var collector = new MetricsCollector(() => slot);
        Assert.IsFalse(collector.Watched, "the counter gate should be shut");

        collector.RecordProcessAccess(4321, write: false);

        var callers = collector.RecentCallers(TimeSpan.FromMinutes(2));
        Assert.AreEqual(1, callers.Count);
        Assert.AreEqual(4321, callers[0].Pid);
        Assert.AreEqual(slot, callers[0].Slot);
    }

    [TestMethod]
    public void A_caller_older_than_the_window_is_not_reported()
    {
        long slot = 10_000;
        var collector = new MetricsCollector(() => slot);
        collector.RecordProcessAccess(4321, write: false);

        slot += (long)TimeSpan.FromMinutes(3).TotalMilliseconds / 10;
        Assert.AreEqual(0, collector.RecentCallers(TimeSpan.FromMinutes(2)).Count,
            "a program that stopped using the mount three minutes ago is not one to advise about");
    }

    [TestMethod]
    public void The_newest_caller_is_first()
    {
        long slot = 10_000;
        var collector = new MetricsCollector(() => slot);
        collector.RecordProcessAccess(1111, write: false);
        slot += 5;
        collector.RecordProcessAccess(2222, write: false);

        var callers = collector.RecentCallers(TimeSpan.FromMinutes(2));
        Assert.AreEqual(2222, callers[0].Pid);
        Assert.AreEqual(1111, callers[1].Pid);
    }

    [TestMethod]
    public void Nobody_is_not_a_caller()
    {
        var collector = new MetricsCollector(() => 10_000);
        collector.RecordProcessAccess(0, write: false);
        collector.RecordProcessAccess(-1, write: false);
        Assert.AreEqual(0, collector.RecentCallers(TimeSpan.FromMinutes(2)).Count);
    }
}
