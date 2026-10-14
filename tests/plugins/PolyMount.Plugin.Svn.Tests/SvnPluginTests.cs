using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Xml.Linq;
using PolyMount.Core;
using PolyMount.Plugin.Testing;

namespace PolyMount.Plugins.Svn;

/// <summary>Tests exported by a separate test DLL; never shipped with the production plugin.</summary>
public sealed class SvnPluginTests : IPluginTestProvider
{
    /// <summary>Trimmed from a real Depth:1 PROPFIND response for a pinned revision.</summary>
    private const string Listing = """
        <?xml version="1.0" encoding="utf-8"?>
        <D:multistatus xmlns:D="DAV:" xmlns:ns0="DAV:">
        <D:response xmlns:lp1="DAV:" xmlns:g0="DAV:">
        <D:href>/svn/!svn/rvr/307311/trunk/src/TX4/Package/Repository/Shapes.local/</D:href>
        <D:propstat>
        <D:prop>
        <lp1:resourcetype><D:collection/></lp1:resourcetype>
        <lp1:getlastmodified>Thu, 08 Jan 2026 13:02:56 GMT</lp1:getlastmodified>
        </D:prop>
        <D:status>HTTP/1.1 200 OK</D:status>
        </D:propstat>
        <D:propstat>
        <D:prop>
        <g0:getcontentlength/>
        </D:prop>
        <D:status>HTTP/1.1 404 Not Found</D:status>
        </D:propstat>
        </D:response>
        <D:response xmlns:lp1="DAV:" xmlns:g0="DAV:">
        <D:href>/svn/!svn/rvr/307311/trunk/src/TX4/Package/Repository/Shapes.local/Character/</D:href>
        <D:propstat>
        <D:prop>
        <lp1:resourcetype><D:collection/></lp1:resourcetype>
        <lp1:getlastmodified>Tue, 19 Sep 2023 08:33:32 GMT</lp1:getlastmodified>
        </D:prop>
        <D:status>HTTP/1.1 200 OK</D:status>
        </D:propstat>
        <D:propstat>
        <D:prop>
        <g0:getcontentlength/>
        </D:prop>
        <D:status>HTTP/1.1 404 Not Found</D:status>
        </D:propstat>
        </D:response>
        <D:response xmlns:lp1="DAV:">
        <D:href>/svn/!svn/rvr/307311/trunk/src/TX4/Package/Repository/Shapes.local/resource.repository</D:href>
        <D:propstat>
        <D:prop>
        <lp1:resourcetype/>
        <lp1:getcontentlength>18009</lp1:getcontentlength>
        <lp1:getlastmodified>Mon, 13 Oct 2025 01:02:03 GMT</lp1:getlastmodified>
        </D:prop>
        <D:status>HTTP/1.1 200 OK</D:status>
        </D:propstat>
        </D:response>
        <D:response xmlns:lp1="DAV:">
        <D:href>/svn/!svn/rvr/307311/trunk/src/TX4/Package/Repository/Shapes.local/%E6%B5%8B%E8%AF%95%20file.bin</D:href>
        <D:propstat>
        <D:prop>
        <lp1:resourcetype/>
        <lp1:getcontentlength>12</lp1:getcontentlength>
        </D:prop>
        <D:status>HTTP/1.1 200 OK</D:status>
        </D:propstat>
        </D:response>
        </D:multistatus>
        """;

    /// <summary>Stands in for a transport, recording calls and optionally refusing to serve.</summary>
    private sealed class FakeBackend(bool transport) : ISvnBackend
    {
        public int Calls;
        public bool Disposed;
        private void Enter() { Calls++; if (transport) throw new SvnTransportException("refused"); }
        public long GetHeadRevision() { Enter(); return 42; }
        public IReadOnlyList<SvnEntry> List(long rev, string rel) { Enter(); return Array.Empty<SvnEntry>(); }
        public void CatToFile(long rev, string rel, string dest) => Enter();
        public void Dispose() => Disposed = true;
    }

    public string PluginName => "svn";
    public Task<IReadOnlyList<PluginTestResult>> RunAsync(PluginTestContext context)
    {
        var results = new List<PluginTestResult>();
        void Check(string name, Action test)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            try { test(); results.Add(new(PluginName, name, PluginTestOutcome.Passed)); }
            catch (Exception ex) { results.Add(new(PluginName, name, PluginTestOutcome.Failed, ex.Message)); }
        }
        Check("configured-loading-and-preset", () =>
        {
            var path = typeof(SvnLayerPlugin).Assembly.Location;
            var errors = PluginLoader.LoadConfigured(new[] { new PluginConfig { AssemblyPath = path } }, AppContext.BaseDirectory);
            if (errors.Count > 0) throw new Exception(string.Join("; ", errors));
            if (!LayerPresetRegistry.All.Any(p => p.Id == "svn-workspace")) throw new Exception("Missing plugin preset");
            var preset = LayerPresetRegistry.All.Single(p => p.Id == "svn-workspace");
            if (!preset.Layers.Select(l => l.Type).SequenceEqual(new[] { "memory-cache", "overlay", "prediction-cache", "cache", "svn" }))
                throw new Exception("Unexpected SVN workspace layer order");
            var descriptor = LayerRegistry.Describe("svn");
            if (!descriptor.Fields.Any(f => f.Path == "Source.Password" && f.Kind == LayerFieldKind.Secret) ||
                !descriptor.Fields.Any(f => f.Path == "Source.Url" && f.Required)) throw new Exception("Missing SVN property metadata");
            if (typeof(SvnLayer).Assembly == typeof(ILayer).Assembly) throw new Exception("Plugin is not independent");
            PluginLoader.LoadConfigured(Array.Empty<PluginConfig>(), AppContext.BaseDirectory);
            if (SourceRegistry.Contains("svn") || LayerPresetRegistry.All.Any(p => p.Id == "svn-workspace")) throw new Exception("Disabled plugin registrations remain active");
            if (LayerRegistry.Descriptors.Any(d => d.Type == "svn")) throw new Exception("Disabled plugin metadata remains active");
            PluginLoader.LoadConfigured(new[] { new PluginConfig { AssemblyPath = path } }, AppContext.BaseDirectory);
        });
        Check("registration", () => { if (!SourceRegistry.Contains("svn")) throw new Exception("SVN was not registered"); });
        Check("http-transport-selection", () =>
        {
            var http = SvnLayer.Create("https://example.invalid/repo", "", Path.Combine(context.WorkingDirectory, "pin.txt"));
            if (http.Backend is not FallbackSvnBackend) throw new Exception("http repository did not use the WebDAV transport");
            if (((FallbackSvnBackend)http.Backend).Current is not SvnHttpClient) throw new Exception("WebDAV transport is not preferred");
            var other = SvnLayer.Create("svn://example.invalid/repo", "", Path.Combine(context.WorkingDirectory, "pin.txt"));
            if (other.Backend is not SvnClient) throw new Exception("non-http repository did not use the command line client");
        });
        Check("revision-pinned-url", () =>
        {
            var url = SvnHttpClient.ResourceUrl("https://host/svn", "trunk/src/TX4/Package", 307311,
                "Repository/Shapes.local/resource.repository");
            if (url != "https://host/svn/!svn/rvr/307311/trunk/src/TX4/Package/Repository/Shapes.local/resource.repository")
                throw new Exception("Unexpected revision-pinned URL: " + url);
            if (SvnHttpClient.ResourceUrl("https://host/svn", "", 7, "") != "https://host/svn/!svn/rvr/7")
                throw new Exception("Unexpected repository root URL");
            var escaped = SvnHttpClient.ResourceUrl("https://host/svn", "", 7, "a b/测试.bin");
            if (escaped != "https://host/svn/!svn/rvr/7/a%20b/%E6%B5%8B%E8%AF%95.bin")
                throw new Exception("Path was not escaped: " + escaped);
            if (Uri.UnescapeDataString(escaped.Split("/!svn/rvr/7/")[1]) != "a b/测试.bin")
                throw new Exception("Escaped path does not round-trip");
        });
        Check("listing-parse", () =>
        {
            var entries = SvnHttpClient.ParseListing(XDocument.Parse(Listing),
                "/svn/!svn/rvr/307311/trunk/src/TX4/Package/Repository/Shapes.local", "Shapes.local");
            var names = entries.Select(e => e.Name).ToArray();
            if (!names.SequenceEqual(new[] { "Character", "resource.repository", "测试 file.bin" }))
                throw new Exception("Unexpected names: " + string.Join(", ", names));
            if (!entries[0].IsDir || entries[0].Size != 0) throw new Exception("Collection was not reported as a directory");
            if (entries[1].IsDir || entries[1].Size != 18009) throw new Exception("File size was not read from getcontentlength");
            if (entries[1].TimeUtc != new DateTime(2025, 10, 13, 1, 2, 3, DateTimeKind.Utc))
                throw new Exception("File timestamp was not read from getlastmodified: " + entries[1].TimeUtc);
            if (entries[2].Size != 12) throw new Exception("Percent-escaped entry lost its size");
        });
        Check("listing-parse-without-rvr-hrefs", () =>
        {
            // Some servers echo the requested URL rather than the baseline one; the
            // collection must still be dropped rather than listing itself as a child.
            var entries = SvnHttpClient.ParseListing(
                XDocument.Parse(Listing.Replace("/!svn/rvr/307311/trunk/src/TX4/Package", "/trunk-representation")),
                "/svn/!svn/rvr/307311/trunk/src/TX4/Package/Repository/Shapes.local", "Shapes.local");
            if (entries.Any(e => e.Name == "Shapes.local")) throw new Exception("The collection listed itself");
            if (entries.Count != 3) throw new Exception("Unexpected entry count: " + entries.Count);
        });
        Check("certificate-trust-mapping", () =>
        {
            // A self-signed certificate against an empty root store fails with
            // UntrustedRoot, which is exactly what --trust-server-cert-failures names.
            using var key = System.Security.Cryptography.RSA.Create(2048);
            var request = new System.Security.Cryptography.X509Certificates.CertificateRequest("CN=polymount.test",
                key, System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1);
            using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
            using var chain = new X509Chain();
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            if (chain.Build(certificate)) throw new Exception("A self-signed certificate was trusted by an empty root store");

            if (!SvnHttpClient.TrustsCertificateFailures("unknown-ca", certificate, chain, SslPolicyErrors.RemoteCertificateChainErrors))
                throw new Exception("unknown-ca did not trust an untrusted root");
            if (SvnHttpClient.TrustsCertificateFailures("cn-mismatch", certificate, chain, SslPolicyErrors.RemoteCertificateChainErrors))
                throw new Exception("A chain error was trusted that was not listed");
            if (!SvnHttpClient.TrustsCertificateFailures("cn-mismatch", certificate, chain, SslPolicyErrors.RemoteCertificateNameMismatch))
                throw new Exception("cn-mismatch was not trusted when listed");
            if (SvnHttpClient.TrustsCertificateFailures("cn-mismatch", certificate, chain, SslPolicyErrors.RemoteCertificateNotAvailable))
                throw new Exception("A missing certificate was trusted");
            if (!SvnHttpClient.TrustsCertificateFailures("other", certificate, chain, SslPolicyErrors.RemoteCertificateNotAvailable))
                throw new Exception("The catch-all failure list did not trust every error");
            if (!SvnHttpClient.TrustsCertificateFailures("unknown-ca,cn-mismatch,expired,not-yet-valid,other", certificate, chain, SslPolicyErrors.None))
                throw new Exception("A valid certificate was rejected");
        });
        Check("transport-fallback", () =>
        {
            var preferred = new FakeBackend(transport: true);
            var backup = new FakeBackend(transport: false);
            var backend = new FallbackSvnBackend(preferred, () => backup);
            if (backend.GetHeadRevision() != 42) throw new Exception("Fallback did not serve the request");
            if (!ReferenceEquals(backend.Current, backup)) throw new Exception("Fallback was not adopted");
            if (!preferred.Disposed) throw new Exception("The refused backend was not released");
            if (backup.Calls != 1) throw new Exception("Fallback call count was " + backup.Calls);
        });
        Check("transport-fallback-is-terminal", () =>
        {
            // Once the fallback is in use its failures are real and must surface.
            var refused = new FakeBackend(transport: true);
            var alsoRefused = new FakeBackend(transport: true);
            var backend = new FallbackSvnBackend(refused, () => alsoRefused);
            try { backend.GetHeadRevision(); throw new Exception("Transport error was swallowed"); }
            catch (SvnTransportException) { }
            if (!ReferenceEquals(backend.Current, alsoRefused)) throw new Exception("Fallback was not adopted");
            if (alsoRefused.Calls != 1) throw new Exception("Fallback was not retried exactly once, calls=" + alsoRefused.Calls);
            try { backend.GetHeadRevision(); throw new Exception("Transport error was swallowed"); }
            catch (SvnTransportException) { }
            if (alsoRefused.Calls != 2) throw new Exception("A terminal failure was retried elsewhere, calls=" + alsoRefused.Calls);
        });
        Check("readonly-contract", () =>
        {
            var layer = SvnLayer.Create("https://example.invalid/repo", "", Path.Combine(context.WorkingDirectory, "pin.txt"));
            if (layer.Writable || ((IObjectLayer)layer).TryApply(Array.Empty<ObjectChange>())) throw new Exception("Read-only SVN accepted a write");
            if (layer is not IRevisionSource) throw new Exception("Missing revision capability");
        });
        Check("invalid-parameters", () =>
        {
            try { SourceRegistry.Validate("svn", new SourceConfig { Url = "not-a-url" }); }
            catch (ArgumentException) { return; }
            throw new Exception("Invalid URL was accepted");
        });
        if (context.Parameters.TryGetValue("svn.repository", out var repository))
            Check("repository-integration", () =>
            {
                var layer = SourceRegistry.Create(new SourceConfig { Type = "svn", Url = repository,
                    Username = context.Parameters.GetValueOrDefault("svn.username"), Password = context.Parameters.GetValueOrDefault("svn.password") }, context.WorkingDirectory);
                if (!((IObjectLayer)layer).TryReadObject("", out var root) || !root.Header.IsDir || root.Children == null)
                    throw new Exception("Repository root did not produce a directory object");
                foreach (var child in root.Children)
                    if (!layer.TryGetEntry(child.Name.ToUpperInvariant(), out var header) || header.Name != child.Name)
                        throw new Exception("Case-insensitive child header lookup failed");
                var directory = root.Children.FirstOrDefault(c => c.IsDir);
                if (directory != null)
                {
                    var canonical = layer.ListOwn(directory.Name);
                    var alternate = layer.ListOwn(directory.Name.ToUpperInvariant());
                    if (!canonical.Select(c => c.Name).SequenceEqual(alternate.Select(c => c.Name)))
                        throw new Exception("Case-insensitive directory listing failed");
                }
            });
        else results.Add(new(PluginName, "repository-integration", PluginTestOutcome.Skipped, "Set svn.repository to enable network testing"));
        return Task.FromResult<IReadOnlyList<PluginTestResult>>(results);
    }
}
