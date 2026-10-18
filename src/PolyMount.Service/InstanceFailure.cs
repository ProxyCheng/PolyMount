using System.Text.Json;
using PolyMount.Core;

namespace PolyMount.Service;

/// <summary>
/// Why a worker could not start, handed from the worker to the supervisor.
///
/// A worker that cannot build its instance exits before it serves its pipe, so the only thing the
/// supervisor would otherwise know is the exit code. That is how every startup failure used to
/// reach a reader as "the worker exited with code 2" — including the one that means "this source
/// needs a password". The worker leaves a small file behind instead, and the supervisor reads it
/// when the process goes away.
/// </summary>
internal static class InstanceFailure
{
    internal sealed record Detail(string Kind, string Message);

    /// <summary>The source refused to serve without credentials, or refused the ones it was given.</summary>
    public const string AuthRequired = "auth";

    /// <summary>Anything else, with the message as the only description.</summary>
    public const string Startup = "startup";

    public static string KindOf(Exception ex) => ex is AuthRequiredException ? AuthRequired : Startup;

    private static string PathOf(string instanceId) => ServiceLogs.PathOf("startup-" + instanceId + ".json");

    public static void Record(string instanceId, string kind, string message)
    {
        try { File.WriteAllText(PathOf(instanceId), JsonSerializer.Serialize(new Detail(kind, message))); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>Reads and removes the record. A stale one from an earlier failure must not describe
    /// this one, so it is taken rather than peeked at.</summary>
    public static Detail? Take(string instanceId)
    {
        string path = PathOf(instanceId);
        try
        {
            if (!File.Exists(path)) return null;
            var detail = JsonSerializer.Deserialize<Detail>(File.ReadAllText(path));
            File.Delete(path);
            return detail;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        catch (JsonException) { return null; }
    }
}
