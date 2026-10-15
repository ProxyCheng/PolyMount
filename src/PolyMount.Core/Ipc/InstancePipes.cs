namespace PolyMount.Core.Ipc;

/// <summary>How the pipe of one instance is named: the control pipe's name with the instance's
/// identity appended.
///
/// The identity is <c>MountProfile.Id</c> — the Guid the instance's store is named by — and not
/// its name, so a renamed instance keeps its pipe, and two instances cannot be handed the same one.
/// The supervisor's own pipe keeps the plain name from the configuration: it is the one address a
/// front-end knows without being told, and what has to be discovered cannot also be the thing that
/// does the discovering.</summary>
public static class InstancePipes
{
    public static string For(string controlPipe, string instanceId) => controlPipe + "." + instanceId;
}
