using System.Security.Principal;

namespace PolyMount.Core;

/// <summary>Whether this process holds an elevated (administrator) token. It is process-wide
/// and does not change, so it is answered once.</summary>
internal static class Elevation
{
    public static bool IsElevated { get; } = Compute();

    private static bool Compute()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception)
        {
            // A token that cannot be inspected is treated as unprivileged.
            return false;
        }
    }
}
