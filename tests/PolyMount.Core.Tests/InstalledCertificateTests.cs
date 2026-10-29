using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PolyMount.Core;

namespace PolyMount.Core.Tests;

/// <summary>A certificate the machine already has, pinned as behaviour: the record holds where it
/// lives and which one it is, the account that will present it is the one that checks it, and a
/// thumbprint that is not there is refused where a form can still say so.
///
/// The fixture installs a real self-signed certificate into this account's own store and takes it
/// out afterwards: the rule being tested is "can this account use that key", and a fake would
/// answer a different question.</summary>
[TestClass]
public class InstalledCertificateTests
{
    private string _folder = "";
    private string _original = "";
    private X509Certificate2? _installed;

    [TestInitialize]
    public void UseATemporaryStore()
    {
        _original = CredentialStore.Path;
        _folder = Path.Combine(Path.GetTempPath(), "polymount-installed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_folder);
        CredentialStore.Path = Path.Combine(_folder, "credentials.json");
        CredentialKinds.Clear();
        CredentialConsumers.Clear();
        _installed = InstallCertificate();
    }

    [TestCleanup]
    public void RestoreTheStore()
    {
        if (_installed != null) Remove(_installed);
        CredentialKinds.Clear();
        CredentialConsumers.Clear();
        CredentialStore.Path = _original;
        try { Directory.Delete(_folder, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>Puts a certificate with a private key into this account's store, which is the
    /// shape of a certificate somebody installed for PolyMount to use.</summary>
    private static X509Certificate2 InstallCertificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=polymount-installed-test", key, HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        using var made = request.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddDays(1));
        // Re-imported with a persistable key: a certificate written into a store needs its key to
        // live with it, and the one CreateSelfSigned hands back is ephemeral.
        var persisted = X509CertificateLoader.LoadPkcs12(
            made.Export(X509ContentType.Pkcs12, "test"), "test", X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.UserKeySet);
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);
        store.Add(persisted);
        return persisted;
    }

    private static void Remove(X509Certificate2 certificate)
    {
        try
        {
            using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadWrite);
            store.Remove(certificate);
        }
        catch (CryptographicException) { }
    }

    private CredentialRecord Save(string? thumbprint, string store = "CurrentUser", string? id = null)
    {
        return CredentialStore.Save(new CredentialInput(id, "Machine certificate",
            CredentialTypes.InstalledCertificate, "", null, null, null, null, null, null,
            store, thumbprint));
    }

    [TestMethod]
    public void A_record_names_an_installed_certificate_and_holds_no_secret()
    {
        var saved = Save(_installed!.Thumbprint);

        Assert.AreEqual(CredentialTypes.InstalledCertificate, saved.Kind);
        Assert.AreEqual("CurrentUser", saved.CertificateStore);
        Assert.AreEqual(_installed.Thumbprint, saved.CertificateThumbprint);
        Assert.IsTrue(saved.Usable, "a certificate this account can use is a record this account can use");

        var used = CredentialStore.Use(saved.Id, "https://svn.example.invalid");
        Assert.IsInstanceOfType(used, typeof(InstalledCertificateCredential));
        Assert.AreEqual(_installed.Thumbprint, CredentialParts.Installed(used)?.Thumbprint);
        Assert.AreEqual("CurrentUser", CredentialParts.Installed(used)?.Store);
        // It proves itself with the certificate and nothing else: no account, no token, no file.
        Assert.IsNull(CredentialParts.Password(used));
        Assert.IsNull(CredentialParts.Bearer(used));
        Assert.IsNull(used as ICertificateAuthentication);
    }

    [TestMethod]
    public void The_thumbprint_is_stored_the_way_a_store_names_it()
    {
        // Lower case with spaces is how one comes out of a certificate dialog; a store compares
        // hexadecimal without them.
        string spaced = string.Join(" ", _installed!.Thumbprint.ToLowerInvariant().Chunk(2).Select(pair => new string(pair)));
        var saved = Save(spaced);
        Assert.AreEqual(_installed.Thumbprint, saved.CertificateThumbprint);
    }

    [TestMethod]
    public void A_thumbprint_that_is_not_in_the_store_is_refused()
    {
        var refused = Assert.ThrowsException<ArgumentException>(() => Save(new string('A', 40)));
        StringAssert.Contains(refused.Message, "no certificate with thumbprint");
    }

    [TestMethod]
    public void A_store_that_is_not_one_of_the_two_is_refused()
    {
        var refused = Assert.ThrowsException<ArgumentException>(() => Save(_installed!.Thumbprint, "Elsewhere"));
        StringAssert.Contains(refused.Message, "LocalMachine or CurrentUser");
    }

    [TestMethod]
    public void A_record_with_no_thumbprint_is_refused()
    {
        var refused = Assert.ThrowsException<ArgumentException>(() => Save(""));
        StringAssert.Contains(refused.Message, "thumbprint");
    }

    [TestMethod]
    public void An_edit_that_sends_nothing_keeps_the_certificate_it_names()
    {
        var saved = Save(_installed!.Thumbprint);
        // A form that only changed the name sends no pair; null means "keep", the same rule a
        // password follows.
        var edited = CredentialStore.Save(new CredentialInput(saved.Id, "Renamed",
            CredentialTypes.InstalledCertificate, "", null, null, null, null, null, null, null, null));
        Assert.AreEqual(_installed.Thumbprint, edited.CertificateThumbprint);
        Assert.IsNotNull(CredentialStore.Use(saved.Id, "https://svn.example.invalid") as InstalledCertificateCredential);
    }

    [TestMethod]
    public void The_service_lists_what_this_account_could_present()
    {
        // The list is what a form picks from, so it has to be the same set the check accepts: an
        // installed certificate appears, and one with no private key does not.
        var listed = CredentialStore.Certificates();
        Assert.IsTrue(listed.Any(item => string.Equals(item.Thumbprint, _installed!.Thumbprint,
                StringComparison.OrdinalIgnoreCase)),
            "the certificate just installed is not listed for the account that installed it");
        Assert.IsTrue(listed.All(item => item.Store is "LocalMachine" or "CurrentUser"));
    }

    [TestMethod]
    public void A_source_that_presents_certificates_is_offered_the_record()
    {
        CredentialConsumers.Register("svn", credential => CredentialParts.Installed(credential) != null);
        var saved = Save(_installed!.Thumbprint);
        CollectionAssert.AreEquivalent(new[] { "svn" },
            CredentialStore.List().Single(item => item.Id == saved.Id).UseFor.ToArray());
    }
}
