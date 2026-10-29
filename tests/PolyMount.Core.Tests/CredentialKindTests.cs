using Microsoft.VisualStudio.TestTools.UnitTesting;
using PolyMount.Core;

namespace PolyMount.Core.Tests;

/// <summary>A credential kind a plugin contributes, pinned as behaviour: its fields round-trip
/// through the store's one protected blob, the account it names is what the list shows, an empty
/// value on an edit keeps the stored one, and a kind whose plugin is gone makes its records
/// unusable rather than lost.
///
/// The store's path is pointed at a temporary directory for the length of each test, the same way
/// the origin tests do it, and the registry is emptied afterwards: kinds are process state, and a
/// test that left one behind would be describing the next test's world.</summary>
[TestClass]
public class CredentialKindTests
{
    private string _folder = "";
    private string _original = "";

    /// <summary>A credential as a plugin would define it: a token and the endpoint it is for.
    /// It says what it can do by implementing a capability — a consumer asks for that, never for
    /// this class.</summary>
    private sealed class SsoCredential : Credential, IBearerAuthentication
    {
        public override string Kind => "sso-test";
        public string Endpoint { get; init; } = "";
        public string Token { get; init; } = "";
    }

    /// <summary>A credential of a kind that stores no secret: it names an account, and what that
    /// account is worth is looked up somewhere else when it is used.</summary>
    private sealed class ReferenceCredential : Credential
    {
        public override string Kind => "cache-reference";
    }

    private static CredentialKindDescriptor Descriptor(bool tokenRequired = true) => new()
    {
        Kind = "sso-test",
        Title = new LocalizedText("Single sign-on", "单点登录"),
        AccountField = "account",
        Fields = new()
        {
            new() { Path = "account", Title = new LocalizedText("Account", "账号") },
            new() { Path = "endpoint", Title = new LocalizedText("Endpoint", "端点"),
                DefaultValue = "https://sso.example.invalid" },
            new() { Path = "token", Kind = CredentialFieldKind.Secret, Required = tokenRequired,
                Title = new LocalizedText("Token", "令牌") }
        },
        Create = values => new SsoCredential
        {
            Username = values.GetValueOrDefault("account", ""),
            Endpoint = values.GetValueOrDefault("endpoint", ""),
            Token = values.GetValueOrDefault("token", "")
        }
    };

    [TestInitialize]
    public void UseATemporaryStore()
    {
        _original = CredentialStore.Path;
        _folder = Path.Combine(Path.GetTempPath(), "polymount-kinds-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_folder);
        CredentialStore.Path = Path.Combine(_folder, "credentials.json");
        CredentialKinds.Clear();
        CredentialConsumers.Clear();
    }

    [TestCleanup]
    public void RestoreTheStore()
    {
        CredentialKinds.Clear();
        CredentialConsumers.Clear();
        CredentialStore.Path = _original;
        try { Directory.Delete(_folder, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static CredentialRecord Save(CredentialKindDescriptor descriptor, string name = "SSO",
        string account = "alice", string token = "t-1", string? endpoint = null, string? id = null)
    {
        var values = new Dictionary<string, string> { ["account"] = account, ["token"] = token };
        // Omitted rather than empty when the caller has no opinion: a key the form did not send
        // is what the kind's default fills, and a key sent empty is an answer.
        if (endpoint != null) values["endpoint"] = endpoint;
        return CredentialStore.Save(new CredentialInput(id, name, descriptor.Kind, "", null, null, null,
            new[] { "https://svn.example.invalid" }, values));
    }

    [TestMethod]
    public void A_plugin_kinds_fields_round_trip_and_its_account_is_what_the_list_shows()
    {
        CredentialKinds.Register(Descriptor());

        var saved = Save(Descriptor());

        Assert.AreEqual("sso-test", saved.Kind);
        Assert.AreEqual("alice", saved.Username, "the kind named its account field, and that value is the row");
        Assert.IsTrue(saved.Usable);

        var used = CredentialStore.Use(saved.Id, "https://svn.example.invalid");
        var credential = used as SsoCredential;
        Assert.IsNotNull(credential, "the store hands back the kind's own credential: " + used?.GetType().Name);
        Assert.AreEqual("alice", credential!.Username);
        Assert.AreEqual("t-1", credential.Token);
        Assert.AreEqual("https://sso.example.invalid", credential.Endpoint,
            "a field the form left empty starts at the kind's default");
    }

    [TestMethod]
    public void A_plugin_kind_says_what_it_can_do_and_consumers_ask_for_that_rather_than_a_type()
    {
        CredentialKinds.Register(Descriptor());
        var saved = Save(Descriptor());

        var used = CredentialStore.Use(saved.Id, "https://svn.example.invalid")!;
        Assert.IsTrue(used is IBearerAuthentication, "the kind's credential does not offer its token");
        Assert.AreEqual("t-1", ((IBearerAuthentication)used).Token);
        // The point of the capability: a token is not a password, and a consumer that needs an
        // account is told so by the shape of the object instead of by two empty strings.
        Assert.IsNull(used as IPasswordAuthentication, "a bearer token was reported as an account with a password");
    }

    [TestMethod]
    public void An_empty_value_on_an_edit_keeps_the_stored_one()
    {
        CredentialKinds.Register(Descriptor());
        var saved = Save(Descriptor(), endpoint: "https://a.example.invalid");

        // The form never shows a stored secret back, so it sends an empty token for an edit that
        // did not touch it. Reading that as "clear it" would make every edit drop the account.
        Save(Descriptor(), account: "bob", token: "", endpoint: "https://a.example.invalid", id: saved.Id);

        var credential = (SsoCredential)CredentialStore.Use(saved.Id, "https://svn.example.invalid")!;
        Assert.AreEqual("bob", credential.Username);
        Assert.AreEqual("t-1", credential.Token, "the token the form did not touch is still there");
    }

    [TestMethod]
    public void A_kind_no_plugin_describes_any_more_is_unusable_rather_than_lost()
    {
        CredentialKinds.Register(Descriptor());
        var saved = Save(Descriptor());
        CredentialKinds.Unregister("sso-test");

        var listed = CredentialStore.List().Single(item => item.Id == saved.Id);
        Assert.IsFalse(listed.Usable, "a record whose kind has no code cannot be used");
        Assert.AreEqual("sso-test", listed.Kind, "the record still says what it is");

        var refused = Assert.ThrowsException<InvalidOperationException>(
            () => CredentialStore.Use(saved.Id, "https://svn.example.invalid"));
        StringAssert.Contains(refused.Message, "sso-test");
        StringAssert.Contains(refused.Message, "plugin");
    }

    [TestMethod]
    public void A_default_that_names_the_person_filling_it_is_expanded()
    {
        // A kind asks "whose?" without knowing the answer: %USERNAME% is whoever fills the form.
        // The save that carries no value at all gets the same expansion, against the account it
        // runs as — which is the one it would have used anyway.
        CredentialKinds.Register(new CredentialKindDescriptor
        {
            Kind = "who-test",
            Title = new LocalizedText("Who", "谁"),
            AccountField = "account",
            Fields = new() { new() { Path = "account", DefaultValue = "whoever-%USERNAME%" } },
            Create = values => new ReferenceCredential { Username = values.GetValueOrDefault("account", "") }
        });

        var saved = CredentialStore.Save(new CredentialInput(null, "Who", "who-test", "",
            null, null, null, Array.Empty<string>(), new Dictionary<string, string>()));

        Assert.AreEqual("whoever-" + Environment.UserName, saved.Username);
    }

    [TestMethod]
    public void A_required_field_the_save_did_not_get_is_refused()
    {
        CredentialKinds.Register(Descriptor());
        var refused = Assert.ThrowsException<ArgumentException>(() => Save(Descriptor(), token: ""));
        StringAssert.Contains(refused.Message, "token");
    }

    [TestMethod]
    public void A_kind_nobody_describes_cannot_be_saved()
    {
        var refused = Assert.ThrowsException<ArgumentException>(
            () => CredentialStore.Save(new CredentialInput(null, "Ghost", "sso-unknown", "", null, null, null)));
        StringAssert.Contains(refused.Message, "sso-unknown");
    }

    [TestMethod]
    public void A_kind_that_stores_no_secret_is_not_bound_to_a_server()
    {
        // The binding protects a secret from being pointed at a server its holder never named. A
        // kind that holds none — "whatever this Windows account saved" — has nothing to protect,
        // and one record is meant for every repository that account can reach; binding it would
        // refuse the second one and write down a list that was never a decision.
        var marker = new CredentialKindDescriptor
        {
            Kind = "cache-reference",
            Title = new LocalizedText("Client cache", "客户端缓存"),
            AccountField = "account",
            Fields = new() { new() { Path = "account", Title = new LocalizedText("Account", "账号") } },
            Create = values => new ReferenceCredential { Username = values.GetValueOrDefault("account", "") }
        };
        CredentialKinds.Register(marker);
        var saved = CredentialStore.Save(new CredentialInput(null, "TortoiseSVN", marker.Kind, "",
            null, null, null, Array.Empty<string>(), new Dictionary<string, string> { ["account"] = "alice" }));

        Assert.IsNotNull(CredentialStore.Use(saved.Id, "https://a.example.invalid"));
        Assert.IsNotNull(CredentialStore.Use(saved.Id, "https://b.example.invalid"),
            "a record that holds no secret must serve a second server");
        Assert.AreEqual(0, CredentialStore.List().Single(item => item.Id == saved.Id).Origins!.Count,
            "using a reference wrote an origin down that its holder never chose");

        // A kind that does hold one keeps the rule: the first origin is written down, and the
        // second is refused by name.
        CredentialKinds.Register(Descriptor());
        var sso = CredentialStore.Save(new CredentialInput(null, "SSO", "sso-test", "", null, null, null,
            Array.Empty<string>(), new Dictionary<string, string> { ["account"] = "alice", ["token"] = "t-1" }));
        CredentialStore.Use(sso.Id, "https://a.example.invalid");
        Assert.ThrowsException<CredentialOriginException>(
            () => CredentialStore.Use(sso.Id, "https://b.example.invalid"));
    }

    [TestMethod]
    public void A_record_says_which_layers_would_present_it()
    {
        // The filter belongs to the layer and is asked with the credential itself; the answer
        // travels with each record, so a picker needs no list of kinds and a kind nobody can
        // present is empty rather than hidden for a reason nobody can read.
        CredentialConsumers.Register("svn", credential => credential is IPasswordAuthentication);
        CredentialConsumers.Register("sso", credential => credential is IBearerAuthentication);

        CredentialKinds.Register(Descriptor());
        var sso = Save(Descriptor());
        var account = CredentialStore.Save(new CredentialInput(null, "Work", CredentialTypes.Password,
            "alice", "secret", null, null));

        CollectionAssert.AreEquivalent(new[] { "sso" },
            CredentialStore.List().Single(item => item.Id == sso.Id).UseFor.ToArray());
        CollectionAssert.AreEquivalent(new[] { "svn" },
            CredentialStore.List().Single(item => item.Id == account.Id).UseFor.ToArray());
    }

    [TestMethod]
    public void A_plugin_cannot_replace_a_kind_the_product_ships()
    {
        Assert.ThrowsException<ArgumentException>(() =>
            CredentialKinds.Register(new CredentialKindDescriptor { Kind = CredentialTypes.Password }));
        Assert.ThrowsException<ArgumentException>(() =>
            CredentialKinds.Register(new CredentialKindDescriptor { Kind = CredentialTypes.Certificate }));
    }

    [TestMethod]
    public void Two_plugins_claiming_one_kind_is_refused()
    {
        CredentialKinds.Register(Descriptor());
        Assert.ThrowsException<ArgumentException>(() => CredentialKinds.Register(Descriptor()));
    }

    [TestMethod]
    public void The_built_in_kinds_are_unchanged_by_the_registry()
    {
        // A password record is written with an empty kind, which is what every file written
        // before kinds existed reads as — a plugin kind must not have moved that.
        var password = CredentialStore.Save(new CredentialInput(null, "Work", CredentialTypes.Password,
            "alice", "secret", null, null));
        Assert.AreEqual(CredentialTypes.Password, password.Kind);
        var used = CredentialStore.Use(password.Id, "https://svn.example.invalid");
        Assert.AreEqual("secret", (used as IPasswordAuthentication)?.Password);
    }
}
