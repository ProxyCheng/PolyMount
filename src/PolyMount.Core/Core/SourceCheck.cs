using System.Diagnostics;
using PolyMount.Core.Ipc;

namespace PolyMount.Core;

/// <summary>
/// Asks a source whether it will serve with these settings, without mounting anything.
///
/// It is the question the layer editor and the new-instance page put beside a credential, because
/// the other way to find out was to save an instance and watch it fail — one round trip through a
/// worker, a mount point and a restart, for an answer that is one request. What it runs is the
/// same path a mount runs: the source type's own factory, its own validation, its own credential
/// resolution. So "the check passed" is not a promise about a similar code path, it is the same
/// one, and a check that fails fails for the reason the mount would.
///
/// The one thing it does differently is the credential's origin binding: it enforces it and does
/// not record it, so asking whether an account works never binds that account to an address that
/// is still being typed.
/// </summary>
public static class SourceCheck
{
    /// <summary>Builds the source described by <paramref name="source"/> in a scratch directory and
    /// reads its root listing. The config is not modified and nothing is written to the store —
    /// the pin file a source keeps is created under the scratch directory and removed again.</summary>
    public static SourceCheckResult Run(SourceConfig source, string? scratchDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        var clock = Stopwatch.StartNew();
        bool own = string.IsNullOrWhiteSpace(scratchDirectory);
        string scratch = own
            ? Path.Combine(Path.GetTempPath(), "polymount-check-" + Guid.NewGuid().ToString("N"))
            : scratchDirectory!;
        // The caller's object is not mutated: it came from a window's draft and may be the very
        // object the same window will compose the profile from.
        var config = new SourceConfig
        {
            Type = source.Type, Url = source.Url, Path = source.Path, PinnedRevision = source.PinnedRevision,
            TrustServerCertFailures = source.TrustServerCertFailures, AllowInsecureTransport = source.AllowInsecureTransport,
            Username = source.Username, Password = source.Password, Writable = source.Writable,
            CredentialId = source.CredentialId, Parameters = source.Parameters,
            VerifyOnly = true
        };
        ILayer? layer = null;
        try
        {
            if (string.IsNullOrWhiteSpace(config.Type) || !SourceRegistry.Contains(config.Type))
                return Answer(SourceCheckKinds.Invalid,
                    "no plugin provides a source of type '" + config.Type + "'", 0, clock);
            SourceRegistry.Validate(config.Type, config);
            Directory.CreateDirectory(scratch);
            layer = SourceRegistry.Create(config, scratch, config.Type);
            // The listing is the proof. Building a layer already proves a local folder and a share
            // (both say so in their constructors, where a mount would fail), and for a repository
            // it is this call that carries the account.
            int entries = layer.ListOwn("").Count;
            return Answer(SourceCheckKinds.Ok, "", entries, clock);
        }
        catch (CredentialOriginException ex) { return Answer(SourceCheckKinds.Origin, ex.Message, 0, clock); }
        catch (AuthRequiredException ex) { return Answer(SourceCheckKinds.Auth, ex.Message, 0, clock); }
        catch (ArgumentException ex) { return Answer(SourceCheckKinds.Invalid, ex.Message, 0, clock); }
        catch (NotSupportedException ex) { return Answer(SourceCheckKinds.Invalid, ex.Message, 0, clock); }
        catch (Exception ex) { return Answer(SourceCheckKinds.Unreachable, ex.Message, 0, clock); }
        finally
        {
            // A source that holds a session holds it for the life of the process: the share the
            // check opened has to be given back, or the next layer to reach that server as another
            // account would find this process already using it.
            (layer as IDisposable)?.Dispose();
            if (own)
                try { Directory.Delete(scratch, recursive: true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
        }
    }

    private static SourceCheckResult Answer(string kind, string message, int entries, Stopwatch clock) =>
        new(kind == SourceCheckKinds.Ok, kind, message, entries, clock.Elapsed.TotalMilliseconds);
}
