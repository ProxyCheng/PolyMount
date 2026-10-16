using Microsoft.VisualStudio.TestTools.UnitTesting;
using PolyMount;

namespace PolyMount.Core.Tests;

/// <summary>Turning one instance on or off is the narrowest write the configuration takes: it is
/// what a dashboard's switch comes to, and it has to be answerable about one instance without the
/// caller holding the rest of the file.
///
/// What it must not do is guess. A name that matches nothing is a stale view rather than a
/// no-op, and a value that was never given is not "off" — the command that carries it refuses
/// both, and this is the half of that behaviour that lives in the configuration.</summary>
[TestClass]
public sealed class InstanceEnabledTests
{
    private static MountProfile Profile(string name, bool enabled = true) =>
        new() { Name = name, MountPoint = @"D:\" + name, Enabled = enabled, Layers = new() };

    private static AppConfig TwoInstances() => new() { Mounts = { Profile("A"), Profile("B") } };

    [TestMethod]
    public void SwitchingOneInstanceOffLeavesTheOtherAlone()
    {
        var config = TwoInstances();

        Assert.IsTrue(config.SetEnabled("A", false), "the change was not reported as one");
        Assert.IsFalse(config.Mounts[0].Enabled);
        Assert.IsTrue(config.Mounts[1].Enabled, "the instance that was not named was switched off too");
    }

    [TestMethod]
    public void AskingForWhatIsAlreadySoChangesNothing()
    {
        // The caller writes the whole file and reloads it either way, so this is the difference
        // between answering a request and restarting a worker to arrive where it started.
        var config = TwoInstances();

        Assert.IsFalse(config.SetEnabled("A", true), "asking for the current value was reported as a change");
        Assert.IsTrue(config.Mounts[0].Enabled);

        config.SetEnabled("B", false);
        Assert.IsFalse(config.SetEnabled("B", false), "switching off twice was reported as a change");
        Assert.IsFalse(config.Mounts[1].Enabled);
    }

    [TestMethod]
    public void TheNameIsMatchedTheWayValidationComparesNames()
    {
        // Two names differing only in case are refused as duplicates, so they are one name here.
        var config = TwoInstances();

        Assert.IsTrue(config.SetEnabled("a", false));
        Assert.IsFalse(config.Mounts[0].Enabled, "the lookup was case sensitive where the rest is not");
    }

    [TestMethod]
    public void ANameThatMatchesNothingIsRefusedRatherThanIgnored()
    {
        var config = TwoInstances();

        StringAssert.Contains(
            Assert.ThrowsException<ArgumentException>(() => config.SetEnabled("gone", false)).Message,
            "no such instance");
        Assert.IsTrue(config.Mounts[0].Enabled, "a refused request still changed the configuration");
        Assert.IsTrue(config.Mounts[1].Enabled);
    }

    [TestMethod]
    public void TheSwitchSurvivesBeingWrittenAndRead()
    {
        using var temp = new TempDir();
        string path = temp.Sub("config.json");
        var config = TwoInstances();
        config.Mounts[0].StoreRoot = temp.Sub("a");
        config.Mounts[1].StoreRoot = temp.Sub("b");

        config.SetEnabled("B", false);
        AppConfig.Save(path, config);

        // What the supervisor does next: the following start reads the file back, and an instance
        // that was switched off is one it does not run.
        var reloaded = AppConfig.LoadOrCreate(path);
        Assert.IsTrue(reloaded.Mounts[0].Enabled);
        Assert.IsFalse(reloaded.Mounts[1].Enabled, "the instance came back on across a restart");
    }
}
