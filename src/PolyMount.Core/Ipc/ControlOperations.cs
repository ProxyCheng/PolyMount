using System.Text.Json;
using System.Text.Json.Nodes;

namespace PolyMount.Core.Ipc;

/// <summary>What calling an operation does to the running system. It decides the MCP annotation a
/// client shows and how readily an agent may call it without being asked: a read is always safe,
/// a write can be undone by writing again, and a destructive operation throws data away.</summary>
public enum ControlEffect
{
    Read,
    Write,
    Destructive
}

/// <summary>
/// One operation of the control plane, described once and offered to every front-end.
///
/// The pipe protocol is a bag of fields keyed by a command string, which is right for a transport
/// and wrong for an API: it says nothing about which arguments a command needs, which arguments
/// are meaningful together, or what the caller will find on the other side. This record is the
/// answer to those questions — a stable name, a description written for someone who has never
/// read the service, a JSON Schema for its arguments, what kind of change it makes, and the
/// projection of the reply (a <see cref="ControlResponse"/> is a union of every command's result;
/// an operation narrows it to the one field it produced).
///
/// <see cref="ControlOperations"/> is the catalog. A front-end that offers all of it (the MCP
/// server does) needs to know nothing about the commands themselves.
/// </summary>
public sealed record ControlOperation(
    string Name,
    string Title,
    string Description,
    ControlEffect Effect,
    bool Idempotent,
    string InputSchema,
    Func<IControlLink, ToolArguments, ControlResponse> Invoke,
    Func<ControlResponse, object?> Present);

/// <summary>
/// The operations PolyMount offers to anything outside its own windows.
///
/// Every operation is one intent — list the instances, probe a path, clear a cache — not one
/// command string. Some are answered by the supervisor and some by an instance's own process, and
/// callers never have to know which: <see cref="IControlLink"/> resolves that. The names and
/// arguments here are the stable external surface; the command names underneath are an internal
/// detail that may change without breaking a caller.
/// </summary>
public static class ControlOperations
{
    /// <summary>Every operation, read-only and otherwise, in the order a reader should meet them.</summary>
    public static IReadOnlyList<ControlOperation> All { get; } = Build();

    /// <summary>The operation with this external name, or null when there is none.</summary>
    public static ControlOperation? Find(string name) =>
        All.FirstOrDefault(operation => string.Equals(operation.Name, name, StringComparison.Ordinal));

    /// <summary>Why a call failed, in terms the caller can act on.
    ///
    /// A pipe that is not there is the most common failure by far — the service is stopped, or it
    /// is still starting — and the raw message ("Cannot connect to all outgoing addresses") does
    /// not say what to do. Neither does the supervisor's "unknown command: metrics", which is what
    /// an instance command looks like when no worker is running to answer it: the caller asked
    /// about an instance that is not up, and that is what it should be told.</summary>
    public static string Explain(Exception exception, string pipeName) => exception switch
    {
        TimeoutException => $"the PolyMount service did not answer on pipe '{pipeName}' in time; it may be busy or still starting",
        IOException or UnauthorizedAccessException =>
            $"the PolyMount service is not reachable on pipe '{pipeName}': {exception.Message}",
        _ => exception.Message
    };

    /// <summary>Why the service refused a call, with the two refusals that mean "that instance is
    /// not running" translated into the step that fixes it.</summary>
    public static string Explain(ControlResponse response, string? instance)
    {
        string error = string.IsNullOrWhiteSpace(response.Error)
            ? "the service refused the request without saying why"
            : response.Error;

        if (instance is not null && IsNotRunning(error))
            return NotRunning(instance);

        return error;
    }

    private static bool IsNotRunning(string error) =>
        error.StartsWith("unknown command:", StringComparison.Ordinal) ||
        error.Contains("is answered by the instance's own process", StringComparison.Ordinal);

    private static string NotRunning(string instance) =>
        $"instance '{instance}' is not running; call mount_instance first (or set_instance_enabled with enabled=true to run it from now on)";

    // ---------------------------------------------------------------- schemas

    private const string EmptySchema = """{"type":"object","properties":{}}""";

    private const string InstanceSchema = """
        {"type":"object","properties":{"instance":{"type":"string","description":"Instance name, as list_instances reports it."}},"required":["instance"]}
        """;

    private const string InstanceLayerSchema = """
        {"type":"object","properties":{"instance":{"type":"string","description":"Instance name, as list_instances reports it."},"layer":{"type":"string","description":"Layer id, as get_instance lists it under providers[].name. Omit for every layer that accepts the action."}},"required":["instance"]}
        """;

    private const string InstanceLayerRequiredSchema = """
        {"type":"object","properties":{"instance":{"type":"string","description":"Instance name, as list_instances reports it."},"layer":{"type":"string","description":"Overlay layer id, as get_instance lists it under providers[].name."}},"required":["instance","layer"]}
        """;

    private const string ProbePathSchema = """
        {"type":"object","properties":{"path":{"type":"string","description":"Full local path under a mount point, for example D:\\\\L10\\\\some\\\\file."}},"required":["path"]}
        """;

    private const string MetricsSchema = """
        {"type":"object","properties":{"instance":{"type":"string","description":"Instance name. Omit for every instance."},"window_seconds":{"type":"number","description":"How far back to look, in seconds. Defaults to 60. The service answers over what it has measured, which can be shorter."}}}
        """;

    private const string SetPinSchema = """
        {"type":"object","properties":{"instance":{"type":"string","description":"Instance name."},"revision":{"type":"integer","description":"The revision to pin to; must be positive. Use refresh_head to learn the current one."},"layer":{"type":"string","description":"Revision-source layer id. Omit to pin every revision source."}},"required":["instance","revision"]}
        """;

    private const string RefreshHeadSchema = """
        {"type":"object","properties":{"instance":{"type":"string","description":"Instance name."},"layer":{"type":"string","description":"Revision-source layer id. Omit to refresh every revision source."}},"required":["instance"]}
        """;

    private const string SaveConfigSchema = """
        {"type":"object","properties":{"config":{"type":"object","description":"The complete configuration document, as get_config returned it, with your changes applied."}},"required":["config"]}
        """;

    private const string RecordingStartSchema = """
        {"type":"object","properties":{"instance":{"type":"string","description":"Instance name."},"label":{"type":"string","description":"A label kept with the session, for telling recordings apart."}},"required":["instance"]}
        """;

    private const string RecordingStopSchema = """
        {"type":"object","properties":{"instance":{"type":"string","description":"Instance name."},"reason":{"type":"string","description":"Why it is stopping: 'stopped', 'saved', 'discarded'. Defaults to 'stopped'."}},"required":["instance"]}
        """;

    private const string LayerActionSchema = """
        {"type":"object","properties":{"path":{"type":"string","description":"Full local path under a mount point."},"layer":{"type":"string","description":"Layer id, as probe_path reports it."},"action":{"type":"string","enum":["dropLocal","evict"],"description":"dropLocal: discard the overlay's local copy of the path. evict: make the cache forget the path."}},"required":["path","layer","action"]}
        """;

    private const string ReleaseAboveSchema = """
        {"type":"object","properties":{"path":{"type":"string","description":"Full local path under a mount point."},"boundary":{"type":"integer","description":"Every manageable layer above this index releases the path. 0 releases all of them."},"expected_layers":{"type":"array","items":{"type":"string"},"description":"The layer names probe_path reported, in the same order. The service refuses the call if the chain has changed since."}},"required":["path","boundary","expected_layers"]}
        """;

    // ---------------------------------------------------------------- catalog

    private static ControlOperation[] Build() =>
    [
        ReadOnly("list_instances", "List instances",
            "Every configured instance: name, whether it is enabled, whether it is mounted, its mount point, source and revision. "
            + "Start here — the other tools take an instance name or a path under one of these mount points. "
            + "Files under a mount point are read and written with ordinary file tools; these tools control the mounts themselves.",
            EmptySchema,
            (link, _) => link.Status(),
            response => new { instances = Compact(response.Mounts) }),

        ReadOnly("get_instance", "Get one instance",
            "One instance in full: its layers in order, what each holds, its cache usage, and the error it last reported. "
            + "Use list_instances to discover names.",
            InstanceSchema,
            (link, args) =>
            {
                string name = args.String("instance");
                var status = link.Status();
                if (!status.Ok) return status;
                var row = status.Mounts.FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));
                return row is null
                    ? new ControlResponse { Ok = false, Error = $"no instance named '{name}'" }
                    : new ControlResponse { Mounts = { row } };
            },
            response => response.Mounts[0]),

        ReadOnly("get_metrics", "Get read and write analytics",
            "Read and write analytics over a recent window: per-layer hits, bytes and latency, and which processes made the requests. "
            + "Omit 'instance' for all instances. The window is what the service has measured, which may be shorter than requested.",
            MetricsSchema,
            (link, args) =>
            {
                string? instance = args.OptionalString("instance");
                double window = args.Has("window_seconds") ? args.Double("window_seconds") : 60;
                if (window is < 1 or > 3600)
                    throw new ArgumentException("argument 'window_seconds' must be between 1 and 3600");
                return new ControlResponse { Metrics = link.Metrics(window, instance) };
            },
            response => new { metrics = response.Metrics }),

        ReadOnly("get_config", "Get the configuration",
            "The service's configuration as one JSON object: instances, layers, plugins and settings. "
            + "This is the document save_config accepts; read it first and send it back with your changes. "
            + "Stored passwords are shown as [redacted] and are never sent to a caller. "
            + "Layer types and their parameters are in list_layer_types.",
            EmptySchema,
            (link, _) => link.Supervisor(new ControlRequest { Cmd = ControlCommands.GetConfig }),
            response => string.IsNullOrWhiteSpace(response.Config) ? null : Redact(JsonNode.Parse(response.Config))),

        ReadOnly("list_layer_types", "List the layer types",
            "The layer types this service can build, their editable fields, and the presets it offers. "
            + "Use it before writing a configuration, to know which types and parameter names are valid.",
            EmptySchema,
            (link, _) => link.Supervisor(new ControlRequest { Cmd = ControlCommands.GetConfig }),
            response => new
            {
                sourceTypes = response.SourceTypes,
                layerDescriptors = response.LayerDescriptors,
                presets = response.Presets
            }),

        ReadOnly("list_log_files", "List diagnostics files",
            "The diagnostics files the service holds, with their sizes and absolute paths. "
            + "Request logs are sampled: they record slow requests and root requests, not every request. "
            + "This returns where the files are, not their contents.",
            EmptySchema,
            (link, _) => link.Supervisor(new ControlRequest { Cmd = ControlCommands.Logs }),
            response => new { logs = response.Logs }),

        ReadOnly("probe_path", "Inspect one path",
            "What each layer makes of one path: which layer holds it, which one wins, and which are shadowed. "
            + "Use it before layer actions, which take the layer names it reports.",
            ProbePathSchema,
            (link, args) => PathRequest(link, args, new ControlRequest { Cmd = ControlCommands.Probe }),
            response => response.Probe),

        ReadOnly("get_recording", "Get the recording state",
            "Whether the instance is recording and what it has counted. A recording counts every request exactly; it does not write a log.",
            InstanceSchema,
            (link, args) => link.Instance(args.String("instance"), new ControlRequest { Cmd = ControlCommands.RecordingStatus }),
            response => response.Recording),

        Mutation("mount_instance", "Mount an instance",
            "Starts the instance's process and mounts it. A disabled instance cannot be mounted — enable it with set_instance_enabled first. "
            + "This does not change the configuration, so the instance comes back on its own next time.",
            InstanceSchema,
            idempotent: true,
            (link, args) => link.Supervisor(new ControlRequest { Cmd = ControlCommands.Mount, Name = args.String("instance") }),
            MountsPresent),

        Mutation("unmount_instance", "Unmount an instance",
            "Stops the instance and takes its mount down. Not durable: the configuration still says it runs, so it mounts again after the "
            + "service restarts or its configuration changes. Use set_instance_enabled to keep it off.",
            InstanceSchema,
            idempotent: true,
            (link, args) => link.Supervisor(new ControlRequest { Cmd = ControlCommands.Unmount, Name = args.String("instance") }),
            MountsPresent),

        Mutation("set_instance_enabled", "Enable or disable an instance",
            "Turns an instance on or off in the configuration. Off stops it and keeps it stopped across restarts; on starts it. "
            + "A value that already matches changes nothing — enabling an instance that is enabled but stopped does not start it; "
            + "use mount_instance for that. Prefer this over rewriting the whole configuration when this is the only change.",
            """
            {"type":"object","properties":{"instance":{"type":"string","description":"Instance name."},"enabled":{"type":"boolean","description":"Whether the instance should run."}},"required":["instance","enabled"]}
            """,
            idempotent: true,
            (link, args) => link.Supervisor(new ControlRequest
            {
                Cmd = ControlCommands.SetEnabled,
                Name = args.String("instance"),
                Enabled = args.OptionalBool("enabled")
                    ?? throw new ArgumentException("missing required argument 'enabled'")
            }),
            MountsPresent),

        Mutation("reload_config", "Reload the configuration",
            "Re-reads the configuration file from disk and applies it. Use after editing the file outside these tools; save_config already reloads.",
            EmptySchema,
            idempotent: true,
            (link, _) => link.Supervisor(new ControlRequest { Cmd = ControlCommands.Reload }),
            MountsPresent),

        Mutation("save_config", "Save the configuration",
            "Replaces the entire service configuration and applies it. This rewrites the whole file — read get_config first and send the "
            + "complete document back with your changes. Instances that changed are restarted; removed instances are stopped and their on-disk stores are kept. "
            + "Passwords are not accepted through this tool: leave [redacted] where get_config showed one to keep the stored password, "
            + "and sign in from the Tray to set a new one.",
            SaveConfigSchema,
            idempotent: true,
            (link, args) => SaveConfig(link, args),
            MountsPresent),

        Mutation("set_pin", "Pin the source revision",
            "Pins the source to one revision. The revision must be positive; use refresh_head to learn the current head revision. "
            + "Pinning is runtime state and does not change the configuration.",
            SetPinSchema,
            idempotent: true,
            (link, args) =>
            {
                long revision = args.Long("revision");
                if (revision <= 0) throw new ArgumentException("argument 'revision' must be a positive revision number");
                return link.Instance(args.String("instance"), new ControlRequest
                {
                    Cmd = ControlCommands.SetPin,
                    Rev = revision,
                    Layer = args.OptionalString("layer")
                });
            },
            MountsPresent),

        Mutation("refresh_head", "Move the source to head",
            "Moves the source to its current head revision and returns it. Use the returned revision with set_pin to keep it.",
            RefreshHeadSchema,
            idempotent: false,
            (link, args) => link.Instance(args.String("instance"), new ControlRequest
            {
                Cmd = ControlCommands.RefreshHead,
                Layer = args.OptionalString("layer")
            }),
            response => new { revision = response.Revision }),

        Mutation("start_recording", "Start recording",
            "Starts counting every request the instance serves. Idempotent: an instance already recording keeps its session. "
            + "Takes no measurable slowdown.",
            RecordingStartSchema,
            idempotent: true,
            (link, args) => link.Instance(args.String("instance"), new ControlRequest
            {
                Cmd = ControlCommands.RecordingStart,
                Label = args.OptionalString("label")
            }),
            response => response.Recording),

        Mutation("stop_recording", "Stop recording",
            "Stops the recording, keeping what it counted. Read the counts with get_recording.",
            RecordingStopSchema,
            idempotent: true,
            (link, args) => link.Instance(args.String("instance"), new ControlRequest
            {
                Cmd = ControlCommands.RecordingStop,
                Reason = args.OptionalString("reason")
            }),
            response => response.Recording),

        DestructiveOp("clear_cache", "Clear the cache",
            "Drops what the cache layers hold for the instance, or for one named layer. The next reads go back to the source. "
            + "Destructive: cached content has to be fetched again.",
            InstanceLayerSchema,
            idempotent: true,
            (link, args) => link.Instance(args.String("instance"), new ControlRequest
            {
                Cmd = ControlCommands.ClearCache,
                Layer = args.OptionalString("layer")
            }),
            MountsPresent),

        DestructiveOp("clear_local_changes", "Discard local changes",
            "Discards every record an overlay layer holds for the instance: all files written through the mount, and all copies of source files. "
            + "This is the whole layer, not one path — apply_layer_action drops a single path instead. "
            + "The mount is taken down and brought back. Destructive and unrecoverable.",
            InstanceLayerRequiredSchema,
            idempotent: false,
            (link, args) => link.Instance(args.String("instance"), new ControlRequest
            {
                Cmd = ControlCommands.ClearLocalChanges,
                Layer = args.String("layer")
            }),
            MountsPresent),

        DestructiveOp("apply_layer_action", "Change one layer's answer for a path",
            "Changes what one layer says about one path. Overlay: 'dropLocal' discards the local copy and reveals the layer below. "
            + "Cache: 'evict' forgets the cached path. Destructive: the discarded data has to be fetched or written again.",
            LayerActionSchema,
            idempotent: true,
            (link, args) =>
            {
                string action = args.String("action");
                if (action is not (LayerActions.DropLocal or LayerActions.EvictCache))
                    throw new ArgumentException($"argument 'action' must be '{LayerActions.DropLocal}' or '{LayerActions.EvictCache}'");
                return PathRequest(link, args, new ControlRequest
                {
                    Cmd = ControlCommands.LayerAction,
                    Layer = args.String("layer"),
                    Action = action
                });
            },
            response => response.Probe),

        DestructiveOp("release_above", "Release a path from the layers above",
            "Lets every manageable layer above a boundary stop answering for one path, so the layers below serve it again. "
            + "expected_layers must be exactly the layer names probe_path reported, in the same order; a chain that changed since is refused. "
            + "The mount is taken down and brought back around the change.",
            ReleaseAboveSchema,
            idempotent: false,
            (link, args) =>
            {
                int boundary = (int)args.Long("boundary");
                if (boundary < 0) throw new ArgumentException("argument 'boundary' must not be negative");
                return PathRequest(link, args, new ControlRequest
                {
                    Cmd = ControlCommands.ReleaseAbove,
                    Boundary = boundary,
                    ExpectedLayers = args.StringList("expected_layers").ToList()
                });
            },
            response => response.Probe),

        DestructiveOp("shutdown_service", "Stop the service",
            "Stops the service: every mount is taken down and the service process exits. The configuration is unchanged, so the next start "
            + "brings everything back — but nothing restarts it automatically, so whatever is reading the mounts will fail until it is started again.",
            EmptySchema,
            idempotent: true,
            (link, _) => link.Supervisor(new ControlRequest { Cmd = ControlCommands.Shutdown }),
            _ => new { ok = true })
    ];

    // ---------------------------------------------------------------- helpers

    private static ControlOperation ReadOnly(string name, string title, string description, string schema,
        Func<IControlLink, ToolArguments, ControlResponse> invoke, Func<ControlResponse, object?> present) =>
        new(name, title, description, ControlEffect.Read, true, schema, invoke, present);

    private static ControlOperation Mutation(string name, string title, string description, string schema, bool idempotent,
        Func<IControlLink, ToolArguments, ControlResponse> invoke, Func<ControlResponse, object?> present) =>
        new(name, title, description, ControlEffect.Write, idempotent, schema, invoke, present);

    private static ControlOperation DestructiveOp(string name, string title, string description, string schema, bool idempotent,
        Func<IControlLink, ToolArguments, ControlResponse> invoke, Func<ControlResponse, object?> present) =>
        new(name, title, description, ControlEffect.Destructive, idempotent, schema, invoke, present);

    /// <summary>A path-based command, refused before it is sent when no mount point contains the
    /// path: the supervisor's answer to a command it does not own says which process does, which
    /// is not what a caller wants to hear.</summary>
    private static ControlResponse PathRequest(IControlLink link, ToolArguments args, ControlRequest request)
    {
        string path = args.String("path");
        // Resolving a relative path here would resolve it against this process's working
        // directory, which is not what the caller meant and not something they can see.
        if (!Path.IsPathFullyQualified(path))
            throw new ArgumentException("argument 'path' must be an absolute path");

        string? owner = link.OwnerOf(path);
        if (owner is null)
            return new ControlResponse { Ok = false, Error = $"no mount point contains '{path}'; call list_instances to see them" };

        request.Path = path;
        var response = link.ForPath(path, request);
        if (!response.Ok && response.Error is { } error && IsNotRunning(error))
            response.Error = NotRunning(owner);
        return response;
    }

    private static object MountsPresent(ControlResponse response) => new { ok = true, instances = Compact(response.Mounts) };

    /// <summary>The sentinel a stored password is replaced with on the way out. It is not a
    /// password: an incoming one that says exactly this keeps the stored secret, and any other
    /// non-empty value is refused, because a secret typed into a conversation is a secret in a
    /// transcript.</summary>
    private const string Redacted = "[redacted]";

    /// <summary>Validates the document, puts back the passwords get_config took out, and saves.
    ///
    /// The round trip matters: a caller that reads the configuration, changes one field and sends
    /// it back must not thereby erase every credential the service holds. Restoring them by the
    /// instance and layer identities is what makes the redacted document safe to hand out.</summary>
    private static ControlResponse SaveConfig(IControlLink link, ToolArguments args)
    {
        JsonNode? incoming;
        try
        {
            incoming = JsonNode.Parse(args.Json("config"));
        }
        catch (JsonException ex)
        {
            throw new ArgumentException("argument 'config' is not valid JSON: " + ex.Message);
        }
        if (incoming is not JsonObject) throw new ArgumentException("argument 'config' must be a JSON object");

        var current = link.Supervisor(new ControlRequest { Cmd = ControlCommands.GetConfig });
        if (!current.Ok) return current;
        var existing = string.IsNullOrWhiteSpace(current.Config) ? null : JsonNode.Parse(current.Config);
        RestoreSecrets(incoming, existing);

        return link.Supervisor(new ControlRequest { Cmd = ControlCommands.SaveConfig, Config = incoming.ToJsonString() });
    }

    private static JsonNode? Redact(JsonNode? config)
    {
        foreach (var mount in Mounts(config))
        {
            Redact(mount["Source"] as JsonObject);
            foreach (var layer in Layers(mount))
                Redact(layer["Source"] as JsonObject);
        }
        return config;
    }

    private static void Redact(JsonObject? source)
    {
        if (source?["Password"] is JsonValue value &&
            value.TryGetValue<string>(out var password) &&
            !string.IsNullOrEmpty(password))
            source["Password"] = Redacted;
    }

    private static void RestoreSecrets(JsonNode incoming, JsonNode? current)
    {
        var priorMounts = Mounts(current).ToList();
        foreach (var mount in Mounts(incoming))
        {
            var prior = MatchMount(mount, priorMounts);
            RestoreSecret(mount["Source"] as JsonObject, prior?["Source"] as JsonObject);

            foreach (var layer in Layers(mount))
                RestoreSecret(layer["Source"] as JsonObject, MatchLayer(layer, prior)?["Source"] as JsonObject);
        }
    }

    private static void RestoreSecret(JsonObject? source, JsonObject? prior)
    {
        if (source?["Password"] is not JsonValue value || !value.TryGetValue<string>(out var password)) return;

        if (password == Redacted)
        {
            if (prior?["Password"] is JsonValue stored &&
                stored.TryGetValue<string>(out var original) &&
                !string.IsNullOrEmpty(original))
                source["Password"] = original;
            else
                throw new ArgumentException(
                    "the configuration marks a password as [redacted], but the service holds no password to keep; "
                    + "remove the field, or sign in from the Tray");

            return;
        }

        if (!string.IsNullOrEmpty(password))
            throw new ArgumentException(
                "passwords are not accepted through MCP; leave [redacted] to keep the stored one, "
                + "or sign in from the Tray to set a new one");
    }

    private static IEnumerable<JsonObject> Mounts(JsonNode? config)
    {
        if (config is not JsonObject root || root["Mounts"] is not JsonArray mounts) yield break;
        foreach (var mount in mounts)
            if (mount is JsonObject obj) yield return obj;
    }

    private static IEnumerable<JsonObject> Layers(JsonObject mount)
    {
        if (mount["Layers"] is not JsonArray layers) yield break;
        foreach (var layer in layers)
            if (layer is JsonObject obj) yield return obj;
    }

    private static JsonObject? MatchMount(JsonObject mount, IReadOnlyList<JsonObject> prior)
    {
        string? id = StringOf(mount["Id"]);
        if (!string.IsNullOrEmpty(id))
            return prior.FirstOrDefault(candidate =>
                string.Equals(StringOf(candidate["Id"]), id, StringComparison.OrdinalIgnoreCase));

        string? name = StringOf(mount["Name"]);
        return name is null
            ? null
            : prior.FirstOrDefault(candidate =>
                string.Equals(StringOf(candidate["Name"]), name, StringComparison.OrdinalIgnoreCase));
    }

    private static JsonObject? MatchLayer(JsonObject layer, JsonObject? mount)
    {
        string? id = StringOf(layer["Id"]);
        if (mount is null || string.IsNullOrEmpty(id)) return null;
        return Layers(mount).FirstOrDefault(candidate =>
            string.Equals(StringOf(candidate["Id"]), id, StringComparison.OrdinalIgnoreCase));
    }

    private static string? StringOf(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    /// <summary>The fields a caller needs to decide what to do next, without the per-layer detail
    /// that makes a full status worth reading only when one instance is the subject.</summary>
    private static IEnumerable<InstanceRow> Compact(IEnumerable<MountStatus> mounts) =>
        mounts.Select(m => new InstanceRow(
            m.Name, m.Enabled, m.Mounted, m.MountPoint, m.SourceType, m.Url, m.Revision,
            m.CacheBytes, m.CacheCapacity, m.WorkerPid, m.Restarts, m.Recording, m.Error));

    private sealed record InstanceRow(
        string Name,
        bool Enabled,
        bool Mounted,
        string MountPoint,
        string SourceType,
        string Url,
        long Revision,
        long CacheBytes,
        long CacheCapacity,
        int WorkerPid,
        int Restarts,
        bool Recording,
        string? Error);
}
