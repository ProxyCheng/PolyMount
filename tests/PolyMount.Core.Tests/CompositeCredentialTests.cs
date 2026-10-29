using Microsoft.VisualStudio.TestTools.UnitTesting;
using PolyMount.Core;

namespace PolyMount.Core.Tests;

/// <summary>A composite credential, pinned as behaviour: one record on disk, several credentials
/// to present, each one still bound to the origins it was bound to before somebody put it here.
///
/// The point of the kind is that a server can want two proofs at once — a certificate in the TLS
/// handshake and an account in the Authorization header — and that the pairing is somebody's to
/// see, name and reuse rather than a field one kind happens to have.
///
/// The store's path is pointed at a temporary directory for each test, the way the kind tests do
/// it: everything below writes real records, and a test that used the machine's own store would be
/// writing to the account it runs as.</summary>
[TestClass]
public class CompositeCredentialTests
{
    private string _folder = "";
    private string _original = "";

    [TestInitialize]
    public void UseATemporaryStore()
    {
        _original = CredentialStore.Path;
        _folder = Path.Combine(Path.GetTempPath(), "polymount-composite-" + Guid.NewGuid().ToString("N"));
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

    /// <summary>A certificate with a real file behind it: the store opens the PKCS#12 when it
    /// saves, so a fixture that is not a certificate is refused before any of this is exercised.
    /// Made here rather than checked in — a self-signed one costs a handful of milliseconds.</summary>
    private static byte[] Certificate()
    {
        using var key = System.Security.Cryptography.RSA.Create(2048);
        var request = new System.Security.Cryptography.X509Certificates.CertificateRequest(
            "CN=polymount-test", key, System.Security.Cryptography.HashAlgorithmName.SHA256,
            System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddDays(1));
        return certificate.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Pkcs12, "pfx");
    }

    private static CredentialRecord SaveCertificate(string name = "Client cert", params string[] origins)
    {
        return CredentialStore.Save(new CredentialInput(null, name, CredentialTypes.Certificate, "",
            null, Certificate(), "pfx", origins));
    }

    private static CredentialRecord SaveAccount(string name = "Work", string user = "alice",
        string password = "secret", params string[] origins)
    {
        return CredentialStore.Save(new CredentialInput(null, name, CredentialTypes.Password, user,
            password, null, null, origins));
    }

    private static CredentialRecord SaveComposite(string name, params CredentialRecord[] children)
    {
        return CredentialStore.Save(new CredentialInput(null, name, CredentialTypes.Composite, "",
            null, null, null, null, null, children.Select(item => item.Id).ToArray()));
    }

    [TestMethod]
    public void A_composite_is_one_record_and_the_credentials_it_was_made_of()
    {
        var certificate = SaveCertificate();
        var account = SaveAccount();

        var composite = SaveComposite("demo login", certificate, account);
        Assert.AreEqual(CredentialTypes.Composite, composite.Kind);
        CollectionAssert.AreEqual(new[] { certificate.Id, account.Id }, composite.Children.ToArray(),
            "the children come back in the order they were put in");

        var used = CredentialStore.Use(composite.Id, "https://a.example.invalid");
        Assert.IsInstanceOfType(used, typeof(CompositeCredential));
        // What a source asks is answered by the parts: the certificate is off the record that holds
        // it, and the account off the one somebody paired with it — neither is a field of this one.
        Assert.IsTrue(CredentialParts.Certificate(used)?.Certificate.Length > 0,
            "the certificate did not come back as part of the composite");
        Assert.AreEqual("alice", CredentialParts.Password(used)?.UserName);
        Assert.IsNull(used as ICertificateAuthentication,
            "the composite itself answers nothing: it is a list, not a proof");
    }

    [TestMethod]
    public void A_composite_that_was_emptied_is_refused()
    {
        var refused = Assert.ThrowsException<ArgumentException>(() =>
            CredentialStore.Save(new CredentialInput(null, "Empty", CredentialTypes.Composite, "",
                null, null, null, null, null, Array.Empty<string>())));
        StringAssert.Contains(refused.Message, "at least one");
    }

    [TestMethod]
    public void A_composite_holds_one_of_each_kind_of_proof()
    {
        var certificate = SaveCertificate();
        var first = SaveAccount("Work", "alice", "a");
        var second = SaveAccount("Personal", "bob", "b");

        // Two accounts would leave the order of the list deciding which one the server is shown and
        // the other silently never presented, so the pair is refused where the form can say which.
        var refused = Assert.ThrowsException<ArgumentException>(() =>
            SaveComposite("Two accounts", certificate, first, second));
        StringAssert.Contains(refused.Message, "both provide an account");
    }

    [TestMethod]
    public void A_composite_cannot_contain_another_composite()
    {
        var certificate = SaveCertificate();
        var inner = SaveComposite("Inner", certificate);
        var refused = Assert.ThrowsException<ArgumentException>(() => SaveComposite("Outer", inner));
        StringAssert.Contains(refused.Message, "cannot be put inside another one");
    }

    [TestMethod]
    public void A_composite_cannot_contain_itself()
    {
        var certificate = SaveCertificate();
        var composite = SaveComposite("One", certificate);
        var refused = Assert.ThrowsException<ArgumentException>(() => CredentialStore.Save(
            new CredentialInput(composite.Id, composite.Name, CredentialTypes.Composite, "",
                null, null, null, null, null, new[] { certificate.Id, composite.Id })));
        StringAssert.Contains(refused.Message, "contain itself");
    }

    [TestMethod]
    public void Every_child_keeps_the_origins_it_was_bound_to()
    {
        // The pairing must not be a way round the binding: a password bound to one host, put under
        // a certificate, is still that password. It is checked at the address the layer talks to,
        // and the child — not the composite — is what says no.
        // The certificate is at home on both hosts, so the refusal can only be the account that
        // is not: one child is bound and the other is not, and it is the bound one that says no.
        var certificate = SaveCertificate("Client cert", "https://a.example.invalid", "https://b.example.invalid");
        var account = SaveAccount("Work", "alice", "secret", "https://a.example.invalid");
        var composite = SaveComposite("Pair", certificate, account);

        Assert.IsNotNull(CredentialStore.Use(composite.Id, "https://a.example.invalid"));
        var refused = Assert.ThrowsException<CredentialOriginException>(
            () => CredentialStore.Use(composite.Id, "https://b.example.invalid"));
        Assert.AreEqual(account.Id, refused.CredentialId, "the refusal names the child that is bound, not the composite");

        // A composite holds no secret of its own, so using it wrote no origin onto it: the list a
        // reader sees on that record is not a decision anybody made.
        Assert.AreEqual(0, CredentialStore.List().Single(item => item.Id == composite.Id).Origins!.Count);
    }

    [TestMethod]
    public void A_child_that_still_has_no_origin_is_bound_by_the_first_use_of_the_composite()
    {
        var certificate = SaveCertificate();
        var account = SaveAccount();
        var composite = SaveComposite("Pair", certificate, account);

        CredentialStore.Use(composite.Id, "https://a.example.invalid");

        var stored = CredentialStore.List().Single(item => item.Id == account.Id);
        CollectionAssert.AreEqual(new[] { "https://a.example.invalid" }, stored.Origins!.ToArray(),
            "the first use of the composite is the first use of what it is made of");
    }

    [TestMethod]
    public void A_composite_whose_child_was_deleted_cannot_be_used()
    {
        var certificate = SaveCertificate();
        var account = SaveAccount();
        var composite = SaveComposite("Pair", certificate, account);
        CredentialStore.RemoveCredential(account.Id);

        var listed = CredentialStore.List().Single(item => item.Id == composite.Id);
        Assert.IsFalse(listed.Usable, "half of a pair is exactly what reads as a 401 at a mount");
        CollectionAssert.AreEqual(new[] { certificate.Id, account.Id }, listed.Children.ToArray(),
            "the record still says what it was made of, so the reader can see what is missing");

        var refused = Assert.ThrowsException<InvalidOperationException>(
            () => CredentialStore.Use(composite.Id, "https://a.example.invalid"));
        StringAssert.Contains(refused.Message, "no longer exists");
    }

    [TestMethod]
    public void A_composite_says_which_layers_would_present_what_it_holds()
    {
        // The pickers ask the record, and the answer has to come from the parts: a composite of a
        // certificate and an account is presentable to a source that takes either, which is what
        // lets one record open a repository behind mutual TLS with an account behind it.
        CredentialConsumers.Register("svn", credential =>
            CredentialParts.Certificate(credential) != null || CredentialParts.Password(credential) != null);
        CredentialConsumers.Register("sso", credential => CredentialParts.Bearer(credential) != null);

        var certificate = SaveCertificate();
        var account = SaveAccount();
        var composite = SaveComposite("Pair", certificate, account);

        CollectionAssert.AreEquivalent(new[] { "svn" },
            CredentialStore.List().Single(item => item.Id == composite.Id).UseFor.ToArray());
    }

    [TestMethod]
    public void A_composite_switch_back_to_an_account_keeps_no_children()
    {
        // A form that changes the kind of a record sends both shapes; what it sent for the kind it
        // left behind must not be stored, or a password record would carry a list nothing reads.
        var certificate = SaveCertificate();
        var composite = SaveComposite("One", certificate);

        CredentialStore.Save(new CredentialInput(composite.Id, composite.Name, CredentialTypes.Password,
            "alice", "secret", null, null, null, null, new[] { certificate.Id }));

        Assert.AreEqual(0, CredentialStore.List().Single(item => item.Id == composite.Id).Children.Count);
    }
}
