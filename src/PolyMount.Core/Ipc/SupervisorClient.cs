namespace PolyMount.Core.Ipc;

/// <summary>
/// The supervisor: the one process a front-end finds without being told where it is, and the only
/// one that knows what exists. It owns the configuration, decides which instances are running and
/// answers for all of them, which is why the calls below — the instance list, the configuration,
/// the shutdown — have no other home.
/// </summary>
public sealed class SupervisorClient : ControlClient
{
    public SupervisorClient(string? pipeName = null) : base(pipeName) { }

    /// <summary>The supervisor's client, for everything that is about the service rather than
    /// about one instance. A front-end told a different pipe name — a renamed control pipe, a
    /// test — sets it here, and what asks the directory for instances follows.</summary>
    public static SupervisorClient Default { get; set; } = new();

    /// <summary>Where every instance is served. Asked of the supervisor because a worker knows one
    /// instance — its own — and this is the only process that knows the rest.</summary>
    public ControlResponse Instances()
        => Send(new ControlRequest { Cmd = ControlCommands.Instances });

    /// <summary>The service's configuration, which the supervisor holds the only copy of.</summary>
    public ControlResponse GetConfig()
        => Send(new ControlRequest { Cmd = ControlCommands.GetConfig });

    /// <summary>Validates and stores a configuration, then applies it: instances whose settings
    /// changed are rebuilt, ones that were added are started, ones that were removed are stopped.
    /// A rejected configuration is not written.</summary>
    public ControlResponse SaveConfig(string json)
        => Send(new ControlRequest { Cmd = ControlCommands.SaveConfig, Config = json });

    /// <summary>The named accounts and the ones remembered by origin, without secrets. The
    /// supervisor owns that file and is the only process that can open it.</summary>
    public ControlResponse Credentials()
        => Send(new ControlRequest { Cmd = ControlCommands.Credentials });

    public ControlResponse SaveCredential(CredentialPayload credential)
        => Send(new ControlRequest { Cmd = ControlCommands.SaveCredential, Credential = credential });

    public ControlResponse DeleteCredential(string id)
        => Send(new ControlRequest { Cmd = ControlCommands.DeleteCredential, CredentialId = id });

    /// <summary>Asks the service whether a source will serve with these settings — an address and
    /// the credential a draft names — and gets back what it said. Nothing is mounted and nothing is
    /// saved; a source that needs a network round trip takes as long as one does, so a window runs
    /// this off its own thread rather than in a click handler.</summary>
    public ControlResponse CheckSource(SourceConfig source)
        => Send(new ControlRequest { Cmd = ControlCommands.CheckSource, Source = source });

    /// <summary>Reloads the configuration from disk and applies it.</summary>
    public ControlResponse Reload()
        => Send(new ControlRequest { Cmd = ControlCommands.Reload });

    /// <summary>Starts the named instance — which is to say the process that runs it.</summary>
    public ControlResponse Mount(string? name)
        => Send(new ControlRequest { Cmd = ControlCommands.Mount, Name = name });

    /// <summary>Stops the named instance and takes its mount down.</summary>
    public ControlResponse Unmount(string? name)
        => Send(new ControlRequest { Cmd = ControlCommands.Unmount, Name = name });

    /// <summary>Turns the named instance on or off in the configuration.
    ///
    /// On means it is part of what the service runs and gets a process of its own; off means it
    /// stops and stays stopped across a restart of the service, which is what tells this apart
    /// from <see cref="Unmount"/> — that one stops the instance now, and it comes back the next
    /// time the service starts, because nothing was written down about it.</summary>
    public ControlResponse SetEnabled(string name, bool enabled)
        => Send(new ControlRequest { Cmd = ControlCommands.SetEnabled, Name = name, Enabled = enabled });

    /// <summary>Stops the service: every instance is unmounted and the process exits.</summary>
    public ControlResponse Shutdown()
        => Send(new ControlRequest { Cmd = ControlCommands.Shutdown });
}
