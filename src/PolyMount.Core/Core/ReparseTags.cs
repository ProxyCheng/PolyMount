namespace PolyMount.Core;

/// <summary>Reparse tags whose target is another name. Windows resolves these; every other tag
/// describes data attached to a file (a cloud placeholder, a deduplicated extent) that this
/// mount can hand back but has no way to follow.</summary>
internal static class ReparseTags
{
    public const uint MountPoint = 0xA0000003; // a junction
    public const uint Symlink = 0xA000000C;

    public static bool IsNameSurrogate(uint tag) => tag is MountPoint or Symlink;
}
