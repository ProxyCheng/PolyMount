using Microsoft.VisualStudio.TestTools.UnitTesting;
using PolyMount.Core;

namespace PolyMount.Core.Tests;

/// <summary>Asking a source whether it will serve with these settings, pinned as behaviour: the
/// check runs the source's own factory, reports what the source said under a kind a window can
/// act on, and gives back whatever the source took (a share session outlives the question).
///
/// Every source type registered here is removed again: the registry is process-wide, and a test
/// that left one behind would be offering it to whatever ran next.</summary>
[TestClass]
public class SourceCheckTests
{
    private readonly List<string> _registered = new();
    private string _scratch = "";

    [TestInitialize]
    public void FreshScratchDirectory()
    {
        _scratch = Path.Combine(Path.GetTempPath(), "polymount-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_scratch);
    }

    [TestCleanup]
    public void RemoveWhatWasRegistered()
    {
        foreach (string type in _registered) SourceRegistry.Unregister(type);
        _registered.Clear();
        try { Directory.Delete(_scratch, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private void Source(string type, Func<SourceConfig, string, ILayer> factory, Action<SourceConfig>? validate = null)
    {
        SourceRegistry.Register(type, factory, validate);
        _registered.Add(type);
    }

    [TestMethod]
    public void A_source_that_answers_is_reported_with_what_it_listed()
    {
        SourceConfig? asked = null;
        Source("check-ok", (config, state) =>
        {
            asked = config;
            Assert.IsTrue(Directory.Exists(state), "the source was built without a state directory");
            return new ListingSource("alpha", "beta", "gamma");
        });

        var settings = new SourceConfig { Type = "check-ok", Url = "https://a.example.invalid/repo" };
        var result = SourceCheck.Run(settings, _scratch);

        Assert.IsTrue(result.Ok, result.Message);
        Assert.AreEqual(SourceCheckKinds.Ok, result.Kind);
        Assert.AreEqual(3, result.Entries, "the answer reports what the listing held");
        Assert.IsFalse(settings.VerifyOnly, "the caller's own settings were changed");
        Assert.IsTrue(asked!.VerifyOnly, "the source was not told it was being asked rather than mounted");
        Assert.AreEqual("https://a.example.invalid/repo", asked.Url, "the settings did not reach the source");
    }

    [TestMethod]
    public void Whatever_the_source_took_is_given_back()
    {
        // A share that is opened for the question is opened for this process until it is closed;
        // one left behind makes the next layer that reaches that server as another account fail.
        var source = new ListingSource("one");
        Source("check-session", (_, _) => source);
        SourceCheck.Run(new SourceConfig { Type = "check-session" }, _scratch);
        Assert.IsTrue(source.Disposed, "the source was left open");
    }

    [TestMethod]
    public void A_refused_account_is_reported_as_the_account_rather_than_as_a_failed_check()
    {
        Source("check-auth", (_, _) => throw new AuthRequiredException("'\\\\server\\share' did not accept the account 'alice'"));
        var result = SourceCheck.Run(new SourceConfig { Type = "check-auth" }, _scratch);
        Assert.IsFalse(result.Ok);
        Assert.AreEqual(SourceCheckKinds.Auth, result.Kind);
        StringAssert.Contains(result.Message, "did not accept");
    }

    [TestMethod]
    public void An_address_the_credential_is_not_allowed_at_is_reported_on_its_own()
    {
        Source("check-origin", (_, _) => throw new CredentialOriginException("id", "Work",
            new[] { "https://a.example.invalid" }, "https://b.example.invalid"));
        var result = SourceCheck.Run(new SourceConfig { Type = "check-origin" }, _scratch);
        Assert.IsFalse(result.Ok);
        Assert.AreEqual(SourceCheckKinds.Origin, result.Kind);
        StringAssert.Contains(result.Message, "https://b.example.invalid");
    }

    [TestMethod]
    public void Settings_that_do_not_describe_a_source_are_not_reported_as_unreachable()
    {
        Source("check-shape", (_, _) => new ListingSource(), config =>
        {
            if (string.IsNullOrWhiteSpace(config.Url)) throw new ArgumentException("a repository needs an address");
        });
        var result = SourceCheck.Run(new SourceConfig { Type = "check-shape" }, _scratch);
        Assert.AreEqual(SourceCheckKinds.Invalid, result.Kind);
        StringAssert.Contains(result.Message, "needs an address");
    }

    [TestMethod]
    public void A_source_type_no_plugin_provides_is_refused_without_a_sentence_about_the_network()
    {
        var result = SourceCheck.Run(new SourceConfig { Type = "check-nobody-registered" }, _scratch);
        Assert.IsFalse(result.Ok);
        Assert.AreEqual(SourceCheckKinds.Invalid, result.Kind);
        StringAssert.Contains(result.Message, "check-nobody-registered");
    }

    [TestMethod]
    public void Anything_else_the_source_said_is_reported_as_it_said_it()
    {
        Source("check-io", (_, _) => throw new IOException("the remote folder '\\\\server\\share' cannot be read: offline"));
        var result = SourceCheck.Run(new SourceConfig { Type = "check-io" }, _scratch);
        Assert.AreEqual(SourceCheckKinds.Unreachable, result.Kind);
        StringAssert.Contains(result.Message, "cannot be read");
    }

    /// <summary>A source with a fixed root listing that records whether it was closed.</summary>
    private sealed class ListingSource(params string[] names) : ILayer, IDisposable
    {
        private readonly IReadOnlyList<VfsEntry> _root = names
            .Select(name => new VfsEntry { Name = name, IsDir = true }).ToArray();

        public bool Disposed { get; private set; }

        public string Name => "check";
        public string Generation => "generation-1";
        public bool Writable => false;

        public bool TryGetEntry(string rel, out VfsEntry entry)
        {
            entry = rel.Length == 0 ? new VfsEntry { Name = "", IsDir = true } : new VfsEntry { Name = rel, IsDir = true };
            return true;
        }

        public IReadOnlyList<VfsEntry> ListOwn(string rel) => rel.Length == 0 ? _root : Array.Empty<VfsEntry>();

        public bool TryOpenRead(string rel, out VfsEntry entry, out Stream stream)
        {
            entry = null!;
            stream = null!;
            return false;
        }

        public void Dispose() => Disposed = true;
    }
}
