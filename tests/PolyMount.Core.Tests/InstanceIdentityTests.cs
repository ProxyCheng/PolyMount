using Microsoft.VisualStudio.TestTools.UnitTesting;
using PolyMount;

namespace PolyMount.Core.Tests;

/// <summary>An instance's store folder is named after an identity made when the instance is
/// created, not after its name: a name is something a user changes, and one that has been
/// renamed away comes free for the next instance to take.</summary>
[TestClass]
public sealed class InstanceIdentityTests
{
    private static MountProfile Profile(string name, string? store = null) =>
        new() { Name = name, MountPoint = @"D:\" + name, StoreRoot = store ?? "", Layers = new() };

    [TestMethod]
    public void AStoreFolderComesFromTheIdentityAndNotTheName()
    {
        var first = Profile("Mount");
        string store = first.GetStoreRoot();
        Assert.IsFalse(first.EnsureId(), "reading the store root should already have made the identity");

        first.Name = "Package";
        Assert.AreEqual(store, first.GetStoreRoot(), "renaming a mount moved its store");

        var second = Profile("Mount");
        Assert.AreNotEqual(store, second.GetStoreRoot(), "a new instance took the store of the instance renamed away");
    }

    [TestMethod]
    public void AnIdentitySurvivesBeingWrittenAndRead()
    {
        using var temp = new TempDir();
        string path = temp.Sub("config.json");
        var config = new AppConfig { Mounts = { Profile("A"), Profile("B", temp.Sub("store")) } };
        AppConfig.Save(path, config);

        var reloaded = AppConfig.LoadOrCreate(path);
        Assert.AreEqual(2, reloaded.Mounts.Count);
        for (int i = 0; i < 2; i++)
        {
            Assert.IsFalse(string.IsNullOrWhiteSpace(reloaded.Mounts[i].Id), "an identity was not written down");
            Assert.AreEqual(config.Mounts[i].Id, reloaded.Mounts[i].Id, "the identity changed across a restart");
            Assert.AreEqual(config.Mounts[i].GetStoreRoot(), reloaded.Mounts[i].GetStoreRoot());
        }
    }

    [TestMethod]
    public void AConfigFromBeforeIdentitiesIsGivenOneAndKeepsItsStore()
    {
        using var temp = new TempDir();
        string path = temp.Sub("config.json");
        string legacy = temp.Sub("legacy-store");
        // What a write before identities produced: no Id at all, and a store root that had been
        // materialised from a name. The store it names is kept; the identity only fills the gap.
        File.WriteAllText(path, "{\"Mounts\":[{\"Name\":\"old\",\"MountPoint\":\"D:\\\\old\",\"StoreRoot\":" +
            System.Text.Json.JsonSerializer.Serialize(legacy) + ",\"Layers\":[]}]}");

        var loaded = AppConfig.LoadOrCreate(path);
        string id = loaded.Mounts[0].Id;
        Assert.IsFalse(string.IsNullOrWhiteSpace(id), "the older config was not given an identity");
        Assert.AreEqual(legacy, loaded.Mounts[0].GetStoreRoot(), "an existing store root was not kept");

        // Written back, so the next start resolves the same store.
        Assert.AreEqual(id, AppConfig.LoadOrCreate(path).Mounts[0].Id, "the identity was not kept");
    }

    [TestMethod]
    public void TwoInstancesOnOneStoreAreRefused()
    {
        using var temp = new TempDir();
        string shared = temp.Sub("shared");
        var config = new AppConfig { Mounts = { Profile("A", shared), Profile("B", shared) } };
        StringAssert.Contains(Assert.ThrowsException<ArgumentException>(config.Validate).Message, "share the store");

        // A trailing separator names the same folder without being the same string.
        config.Mounts[1].StoreRoot = shared + Path.DirectorySeparatorChar;
        Assert.ThrowsException<ArgumentException>(config.Validate);

        // Apart, the same two are fine: the second falls back to the folder its identity names.
        config.Mounts[1].StoreRoot = "";
        config.Validate();
        Assert.AreNotEqual(config.Mounts[0].GetStoreRoot(), config.Mounts[1].GetStoreRoot());
    }
}
