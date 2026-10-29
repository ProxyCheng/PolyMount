using Microsoft.VisualStudio.TestTools.UnitTesting;
using PolyMount.Core;

namespace PolyMount.Core.Tests;

/// <summary>Where a credential may be presented, pinned as behaviour: the list is written by
/// whoever holds the account, a spelling of an origin is one entry, an empty list is an answer
/// rather than "keep", and a refusal names the one action that repairs it.
///
/// The store's path is pointed at a temporary directory for the length of each test: it is the
/// only file the store touches, and a test that used the machine's own would be writing the
/// account it runs as into whoever runs it next.</summary>
[TestClass]
public class CredentialOriginTests
{
    private string _folder = "";
    private string _original = "";

    [TestInitialize]
    public void UseATemporaryStore()
    {
        _original = CredentialStore.Path;
        _folder = Path.Combine(Path.GetTempPath(), "polymount-credentials-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_folder);
        CredentialStore.Path = Path.Combine(_folder, "credentials.json");
    }

    [TestCleanup]
    public void RestoreTheStore()
    {
        CredentialStore.Path = _original;
        try { Directory.Delete(_folder, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [TestMethod]
    public void A_server_the_credential_does_not_allow_is_refused_with_the_one_action_that_repairs_it()
    {
        var saved = Save("Work", "https://a.example.invalid");

        Assert.AreEqual("alice", CredentialStore.Use(saved.Id, "https://a.example.invalid")?.Username);

        var refused = Assert.ThrowsException<CredentialOriginException>(
            () => CredentialStore.Use(saved.Id, "https://b.example.invalid"));
        Assert.AreEqual(saved.Id, refused.CredentialId);
        Assert.AreEqual("Work", refused.CredentialName);
        Assert.AreEqual("https://b.example.invalid", refused.Origin);
        CollectionAssert.AreEqual(new[] { "https://a.example.invalid" }, refused.Origins.ToArray());
        // Both addresses and the remedy, because this is the whole of what a reader is shown: the
        // message reaches the instance's error, and there is no second place to look.
        StringAssert.Contains(refused.Message, "https://a.example.invalid");
        StringAssert.Contains(refused.Message, "https://b.example.invalid");
        StringAssert.Contains(refused.Message, "Work");
        StringAssert.Contains(refused.Message, "credential manager");
    }

    [TestMethod]
    public void Two_spellings_of_one_server_are_one_entry()
    {
        var saved = Save("Work", "HTTPS://A.Example.Invalid:443/", "https://a.example.invalid");
        CollectionAssert.AreEqual(new[] { "https://a.example.invalid" }, Record(saved.Id).Origins!.ToArray());
    }

    [TestMethod]
    public void A_saved_list_replaces_the_records_own_and_keeps_its_secret()
    {
        var saved = Save("Work", "https://a.example.invalid");
        // An edit that resends no password keeps the stored one; the list is what changes.
        CredentialStore.Save(new CredentialInput(saved.Id, "Work", CredentialTypes.Password, "alice", null, null, null,
            new[] { "https://a.example.invalid", "https://b.example.invalid" }));

        Assert.AreEqual("alice", CredentialStore.Use(saved.Id, "https://b.example.invalid")?.Username);
        Assert.AreEqual(2, Record(saved.Id).Origins!.Count);
    }

    [TestMethod]
    public void An_empty_list_is_an_answer_rather_than_keep_what_is_stored()
    {
        var saved = Save("Work", "https://a.example.invalid");
        CredentialStore.Save(new CredentialInput(saved.Id, "Work", CredentialTypes.Password, "alice", null, null, null,
            Array.Empty<string>()));

        // Nothing is bound any more, so the next use writes its own origin down instead of being
        // refused. A null list is the one that means "keep", and the form never sends null.
        Assert.IsNotNull(CredentialStore.Use(saved.Id, "https://c.example.invalid"));
        CollectionAssert.AreEqual(new[] { "https://c.example.invalid" }, Record(saved.Id).Origins!.ToArray());
    }

    [TestMethod]
    public void Something_that_is_not_an_origin_is_refused()
    {
        foreach (string bad in new[] { "https://a.example.invalid/repo", "ftp://a.example.invalid", "a.example.invalid", "https://a.example.invalid?x=1" })
        {
            try
            {
                Save("Work", bad);
                Assert.Fail("stored as an origin: " + bad);
            }
            catch (ArgumentException ex) { StringAssert.Contains(ex.Message, "not an origin"); }
        }
    }

    [TestMethod]
    public void Asking_whether_an_account_works_does_not_bind_it_to_the_address_that_was_asked()
    {
        // The editor's verify button builds the source to ask its question. That is not the act of
        // pointing a layer at an address — the reader may still be editing it — so the answer must
        // not be the thing that decides where the credential may be presented from then on.
        var saved = Save("Work");
        var question = new SourceConfig
        {
            Type = "probe", Url = "https://a.example.invalid/repo", CredentialId = saved.Id, VerifyOnly = true
        };
        Assert.AreEqual("alice", new ProbeSource(question).Resolve()?.Username);
        Assert.AreEqual(0, Record(saved.Id).Origins!.Count, "asking wrote the origin down");

        // A layer being built for real is the other act, and still writes it.
        var mount = new SourceConfig { Type = "probe", Url = "https://a.example.invalid/repo", CredentialId = saved.Id };
        Assert.AreEqual("alice", new ProbeSource(mount).Resolve()?.Username);
        CollectionAssert.AreEqual(new[] { "https://a.example.invalid" }, Record(saved.Id).Origins!.ToArray());
    }

    [TestMethod]
    public void A_check_still_refuses_the_servers_the_credential_was_not_bound_for()
    {
        var saved = Save("Work", "https://a.example.invalid");
        var question = new SourceConfig
        {
            Type = "probe", Url = "https://b.example.invalid/repo", CredentialId = saved.Id, VerifyOnly = true
        };
        // Refusing to ask is the same refusal a mount would give; what differs is only that the
        // check leaves the record exactly as it found it.
        Assert.ThrowsException<CredentialOriginException>(() => new ProbeSource(question).Resolve());
        CollectionAssert.AreEqual(new[] { "https://a.example.invalid" }, Record(saved.Id).Origins!.ToArray());
    }

    /// <summary>A source that resolves a credential and does nothing else, so the resolution is
    /// what a test is looking at.</summary>
    private sealed class ProbeSource(SourceConfig source) : CredentialedSource(source.Url, source);

    private static CredentialRecord Save(string name, params string[] origins) =>
        CredentialStore.Save(new CredentialInput(null, name, CredentialTypes.Password, "alice", "secret", null, null, origins));

    private static CredentialRecord Record(string id) =>
        CredentialStore.List().Single(item => item.Id == id);
}
