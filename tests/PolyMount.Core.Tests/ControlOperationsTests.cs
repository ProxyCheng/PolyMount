using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PolyMount.Core.Ipc;

namespace PolyMount.Core.Tests;

/// <summary>
/// The external operation surface: what each operation is called, what it promises, and which
/// process it reaches. These are the facts a front-end is allowed to rely on, so they are pinned
/// here rather than being discovered by whichever window calls them first.
/// </summary>
[TestClass]
public sealed class ControlOperationsTests
{
    [TestMethod]
    public void Every_operation_has_a_unique_snake_case_name()
    {
        var names = ControlOperations.All.Select(operation => operation.Name).ToList();

        Assert.AreEqual(names.Count, names.Distinct(StringComparer.Ordinal).Count(), "two operations share a name");
        foreach (string name in names)
            Assert.IsTrue(name.All(c => char.IsLower(c) || char.IsDigit(c) || c == '_'),
                $"'{name}' is not snake_case");
    }

    [TestMethod]
    public void Every_name_is_verb_first_from_the_same_small_set_of_verbs()
    {
        // The names are the external surface; a second way of saying "start a recording" would be
        // a second name for a client to learn, and a model guessing between two equally plausible
        // ones. One verb per kind of action, always first.
        var verbs = new HashSet<string>(StringComparer.Ordinal)
        {
            "list", "get", "probe",
            "mount", "unmount", "set", "reload", "save", "refresh", "start", "stop",
            "clear", "apply", "release", "shutdown"
        };

        foreach (var operation in ControlOperations.All)
        {
            int cut = operation.Name.IndexOf('_');
            Assert.IsTrue(cut > 0, operation.Name + " is not <verb>_<object>");
            string verb = operation.Name[..cut];
            Assert.IsTrue(verbs.Contains(verb), operation.Name + " starts with an unknown verb '" + verb + "'");
        }
    }

    [TestMethod]
    public void Every_operation_says_what_it_does()
    {
        foreach (var operation in ControlOperations.All)
        {
            Assert.IsFalse(string.IsNullOrWhiteSpace(operation.Title), operation.Name + " has no title");
            Assert.IsFalse(string.IsNullOrWhiteSpace(operation.Description), operation.Name + " has no description");
            // Long enough to tell a caller what it is about to change, not just what it is called.
            Assert.IsTrue(operation.Description.Length > 40, operation.Name + " describes itself in a sentence fragment");
        }
    }

    [TestMethod]
    public void Every_input_schema_is_an_object_schema()
    {
        foreach (var operation in ControlOperations.All)
        {
            using var document = JsonDocument.Parse(operation.InputSchema);
            Assert.AreEqual(JsonValueKind.Object, document.RootElement.ValueKind, operation.Name);
            Assert.AreEqual("object", document.RootElement.GetProperty("type").GetString(), operation.Name);
        }
    }

    [TestMethod]
    public void Operations_reach_the_process_that_answers_them()
    {
        var link = new RecordingLink();

        Call(link, "list_instances", "{}");
        CollectionAssert.Contains(link.Calls, "status");

        link = new RecordingLink();
        Call(link, "mount_instance", """{"instance":"demo"}""");
        Assert.AreEqual("supervisor", link.Calls.Single());
        Assert.AreEqual(ControlCommands.Mount, link.Requests.Single().Cmd);

        link = new RecordingLink();
        Call(link, "refresh_head", """{"instance":"demo"}""");
        Assert.AreEqual("instance", link.Calls.Single());
        Assert.AreEqual(ControlCommands.RefreshHead, link.Requests.Single().Cmd);

        link = new RecordingLink();
        Call(link, "get_metrics", """{"instance":"demo"}""");
        Assert.AreEqual("metrics", link.Calls.Single());
    }

    [TestMethod]
    public void Path_operations_go_to_the_instance_that_owns_the_path()
    {
        foreach (var (name, json) in new[]
        {
            ("probe_path", """{"path":"D:\\demo\\a"}"""),
            ("apply_layer_action", """{"path":"D:\\demo\\a","layer":"overlay","action":"dropLocal"}"""),
            ("release_above", """{"path":"D:\\demo\\a","boundary":1,"expected_layers":["overlay","svn"]}""")
        })
        {
            var link = new RecordingLink { Owner = "demo" };
            Call(link, name, json);

            CollectionAssert.AreEqual(new[] { "owner", "path" }, link.Calls.ToArray(), name);
            Assert.AreEqual("D:\\demo\\a", link.Requests.Single().Path, name);
        }
    }

    [TestMethod]
    public void A_path_no_mount_point_contains_is_refused_before_it_is_sent()
    {
        var link = new RecordingLink { Owner = null };

        var response = Call(link, "probe_path", """{"path":"C:\\elsewhere\\file"}""");

        Assert.IsFalse(response.Ok);
        StringAssert.Contains(response.Error, "no mount point contains");
        CollectionAssert.DoesNotContain(link.Calls, "path");
    }

    [TestMethod]
    public void Read_only_operations_send_no_mutating_command()
    {
        var mutating = new HashSet<string>(StringComparer.Ordinal)
        {
            ControlCommands.Mount, ControlCommands.Unmount, ControlCommands.SetEnabled,
            ControlCommands.Reload, ControlCommands.SaveConfig, ControlCommands.SetPin,
            ControlCommands.RefreshHead, ControlCommands.ClearCache, ControlCommands.ClearLocalChanges,
            ControlCommands.LayerAction, ControlCommands.ReleaseAbove, ControlCommands.RecordingStart,
            ControlCommands.RecordingStop, ControlCommands.Shutdown
        };

        foreach (var operation in ControlOperations.All.Where(o => o.Effect == ControlEffect.Read))
        {
            var link = new RecordingLink { Owner = "demo" };
            operation.Invoke(link, Args(SampleArguments(operation.Name)));

            foreach (var request in link.Requests)
                Assert.IsFalse(mutating.Contains(request.Cmd), operation.Name + " sent " + request.Cmd);
        }
    }

    [TestMethod]
    public void Arguments_that_would_default_to_something_else_are_refused()
    {
        var link = new RecordingLink { Owner = "demo" };

        // A missing revision must not become revision zero.
        Assert.ThrowsException<ArgumentException>(() => Call(link, "set_pin", """{"instance":"demo"}"""));
        Assert.ThrowsException<ArgumentException>(() => Call(link, "set_pin", """{"instance":"demo","revision":0}"""));
        // A missing "enabled" must not become "disabled".
        Assert.ThrowsException<ArgumentException>(() => Call(link, "set_instance_enabled", """{"instance":"demo"}"""));
        Assert.ThrowsException<ArgumentException>(() => Call(link, "save_config", "{}"));
        Assert.ThrowsException<ArgumentException>(() => Call(link, "get_metrics", """{"window_seconds":0}"""));
        Assert.ThrowsException<ArgumentException>(() =>
            Call(link, "apply_layer_action", """{"path":"D:\\demo\\a","layer":"cache","action":"delete"}"""));
    }

    [TestMethod]
    public void A_worker_command_that_falls_through_to_the_supervisor_says_the_instance_is_not_running()
    {
        string explained = ControlOperations.Explain(
            new ControlResponse { Ok = false, Error = "unknown command: metrics" }, "demo");

        StringAssert.Contains(explained, "instance 'demo' is not running");
        StringAssert.Contains(explained, "mount_instance");
    }

    [TestMethod]
    public void A_refusal_that_is_already_useful_is_passed_through()
    {
        Assert.AreEqual("no such mount: demo",
            ControlOperations.Explain(new ControlResponse { Ok = false, Error = "no such mount: demo" }, "demo"));
    }

    [TestMethod]
    public void An_unreachable_service_says_which_pipe_and_what_to_check()
    {
        StringAssert.Contains(ControlOperations.Explain(new TimeoutException(), "PolyMount.Control"), "did not answer");
        StringAssert.Contains(ControlOperations.Explain(new IOException("pipe not found"), "PolyMount.Control"), "not reachable");
    }

    [TestMethod]
    public void The_config_is_presented_as_a_document_not_as_escaped_text()
    {
        var operation = ControlOperations.Find("get_config")!;
        var response = new ControlResponse { Config = """{"Mounts":[{"Name":"demo"}]}""" };

        Assert.IsInstanceOfType(operation.Present(response), typeof(JsonObject));
    }

    [TestMethod]
    public void Listed_instances_carry_the_fields_a_caller_decides_with()
    {
        var operation = ControlOperations.Find("list_instances")!;
        var response = new ControlResponse
        {
            Mounts =
            {
                new MountStatus
                {
                    Name = "demo", Enabled = true, Mounted = true, MountPoint = "D:\\demo",
                    SourceType = "svn", Revision = 123456, CacheBytes = 1024
                }
            }
        };

        string json = JsonSerializer.Serialize(operation.Present(response), Json);

        StringAssert.Contains(json, "\"name\":\"demo\"");
        StringAssert.Contains(json, "\"mountPoint\":\"D:\\\\demo\"");
        StringAssert.Contains(json, "\"revision\":123456");
        // The per-layer detail is what get_instance is for; a list of instances must stay a list.
        Assert.IsFalse(json.Contains("providers", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void A_path_given_as_relative_is_refused()
    {
        var link = new RecordingLink { Owner = "demo" };

        Assert.ThrowsException<ArgumentException>(() => Call(link, "probe_path", """{"path":"some\\file.txt"}"""));
    }

    [TestMethod]
    public void Stored_passwords_are_redacted_on_the_way_out()
    {
        var operation = ControlOperations.Find("get_config")!;
        var response = new ControlResponse
        {
            Config = """{"Mounts":[{"Id":"a","Name":"demo","Layers":[{"Id":"svn","Source":{"Type":"svn","Password":"hunter2"}}]}]}"""
        };

        string json = JsonSerializer.Serialize(operation.Present(response), Json);

        StringAssert.Contains(json, "[redacted]");
        Assert.IsFalse(json.Contains("hunter2", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Saving_a_redacted_configuration_puts_the_stored_password_back()
    {
        var link = new RecordingLink
        {
            Reply = new ControlResponse
            {
                Config = """{"Mounts":[{"Id":"a","Name":"demo","Layers":[{"Id":"svn","Source":{"Type":"svn","Password":"hunter2"}}]}]}"""
            }
        };
        var operation = ControlOperations.Find("save_config")!;
        var incoming = """{"Mounts":[{"Id":"a","Name":"demo","Layers":[{"Id":"svn","Source":{"Type":"svn","Password":"[redacted]"}}]}]}""";

        operation.Invoke(link, Args("{\"config\":" + incoming + "}"));

        Assert.AreEqual(ControlCommands.GetConfig, link.Requests[0].Cmd);
        Assert.AreEqual(ControlCommands.SaveConfig, link.Requests[1].Cmd);
        StringAssert.Contains(link.Requests[1].Config!, "\"Password\":\"hunter2\"");
        Assert.IsFalse(link.Requests[1].Config!.Contains("[redacted]", StringComparison.Ordinal));
    }

    [TestMethod]
    public void A_password_typed_into_a_tool_call_is_refused()
    {
        var link = new RecordingLink { Reply = new ControlResponse { Config = """{"Mounts":[]}""" } };
        var operation = ControlOperations.Find("save_config")!;
        var incoming = """{"Mounts":[{"Id":"a","Name":"New","Layers":[{"Id":"svn","Source":{"Type":"svn","Password":"hunter2"}}]}]}""";

        Assert.ThrowsException<ArgumentException>(() => operation.Invoke(link, Args("{\"config\":" + incoming + "}")));
    }

    [TestMethod]
    public void A_redacted_password_with_nothing_to_restore_is_refused()
    {
        var link = new RecordingLink
        {
            Reply = new ControlResponse { Config = """{"Mounts":[{"Id":"a","Name":"demo","Layers":[{"Id":"svn","Source":{"Type":"svn"}}]}]}""" }
        };
        var operation = ControlOperations.Find("save_config")!;
        var incoming = """{"Mounts":[{"Id":"a","Name":"demo","Layers":[{"Id":"svn","Source":{"Type":"svn","Password":"[redacted]"}}]}]}""";

        Assert.ThrowsException<ArgumentException>(() => operation.Invoke(link, Args("{\"config\":" + incoming + "}")));
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static ControlResponse Call(RecordingLink link, string name, string json) =>
        ControlOperations.Find(name)!.Invoke(link, Args(json));

    private static ToolArguments Args(string json) => new(JsonDocument.Parse(json).RootElement);

    private static string SampleArguments(string name) => name switch
    {
        "get_instance" or "get_recording" => """{"instance":"demo"}""",
        "probe_path" => """{"path":"D:\\demo\\a"}""",
        _ => "{}"
    };

    /// <summary>A control plane that answers nothing and remembers which door was knocked on.</summary>
    private sealed class RecordingLink : IControlLink
    {
        public List<string> Calls { get; } = new();
        public List<ControlRequest> Requests { get; } = new();
        public string? Owner { get; set; }

        public ControlResponse Reply { get; set; } = new();

        public ControlResponse Supervisor(ControlRequest request) => Remember("supervisor", request);

        public ControlResponse Instance(string? name, ControlRequest request) => Remember("instance", request);

        public ControlResponse ForPath(string fullPath, ControlRequest request) => Remember("path", request);

        public string? OwnerOf(string fullPath)
        {
            Calls.Add("owner");
            return Owner;
        }

        public ControlResponse Status()
        {
            Calls.Add("status");
            return Reply;
        }

        public List<MountMetrics> Metrics(double windowSeconds, string? name)
        {
            Calls.Add("metrics");
            return Reply.Metrics;
        }

        private ControlResponse Remember(string call, ControlRequest request)
        {
            Calls.Add(call);
            Requests.Add(request);
            return Reply;
        }
    }
}
