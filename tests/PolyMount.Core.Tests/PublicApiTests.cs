using System.Reflection;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PolyMount.Core.Tests;

/// <summary>The contract's public surface, held still.
///
/// A plugin is compiled against this surface in one build and loaded by a host built in another,
/// so a member that quietly disappears or changes shape breaks plugins that nobody is compiling
/// right now. This test makes that impossible to do by accident: it formats every exported type and
/// member into <c>PublicApi.txt</c> and compares.
///
/// Changing the file is allowed — it is a decision, not an accident. What the decision has to
/// include: a breaking change raises <c>PluginApiVersion</c>'s major (so hosts refuse old plugins
/// with a reason), an additive one raises the minor. Run with
/// <c>POLYMOUNT_UPDATE_API_BASELINE=1</c> to rewrite the file.
/// </summary>
[TestClass]
public class PublicApiTests
{
    private static readonly Assembly Contract = typeof(PolyMount.Plugins.IPolyMountPlugin).Assembly;

    [TestMethod]
    public void The_contract_surface_is_what_the_baseline_says()
    {
        string actual = Render();
        string path = BaselinePath();
        if (Environment.GetEnvironmentVariable("POLYMOUNT_UPDATE_API_BASELINE") == "1")
        {
            File.WriteAllText(path, actual);
            Assert.Inconclusive("baseline rewritten: " + path);
        }

        Assert.IsTrue(File.Exists(path), "the baseline is missing: " + path);
        string expected = File.ReadAllText(path).Replace("\r\n", "\n");
        if (expected == actual.Replace("\r\n", "\n")) return;

        var missing = expected.Split('\n').Except(actual.Split('\n')).Where(l => l.Length > 0).Take(10).ToArray();
        var added = actual.Split('\n').Except(expected.Split('\n')).Where(l => l.Length > 0).Take(10).ToArray();
        var report = new StringBuilder();
        report.AppendLine("The plugin contract changed without the baseline.");
        report.AppendLine("Removed or changed (a plugin built against these stops loading):");
        foreach (var line in missing) report.AppendLine("  - " + line);
        report.AppendLine("Added:");
        foreach (var line in added) report.AppendLine("  + " + line);
        report.AppendLine("If the change is additive, raise PluginApiVersion's minor; if it is breaking, raise the major.");
        report.AppendLine("Then rewrite the baseline with POLYMOUNT_UPDATE_API_BASELINE=1.");
        Assert.Fail(report.ToString());
    }

    private static string BaselinePath()
    {
        // Beside the test source rather than the binary: it is a file a change has to be made in,
        // and it is committed.
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "PolyMount.slnx")))
            directory = directory.Parent;
        string root = directory?.FullName ?? AppContext.BaseDirectory;
        return Path.Combine(root, "tests", "PolyMount.Core.Tests", "PublicApi.txt");
    }

    private static string Render()
    {
        var lines = new List<string>();
        foreach (var type in Contract.GetExportedTypes().OrderBy(t => t.FullName, StringComparer.Ordinal))
        {
            lines.Add("type " + TypeName(type) + " : " + string.Join(", ", Bases(type)));
            var members = new List<string>();
            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                members.Add("  field " + field.FieldType.FullName + " " + field.Name + (field.IsStatic ? " static" : ""));
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                members.Add("  property " + TypeName(property.PropertyType) + " " + property.Name
                    + (property.GetMethod?.IsPublic == true ? " get" : "")
                    + (property.SetMethod?.IsPublic == true ? " set" : ""));
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                         .Where(m => !m.IsSpecialName))
                members.Add("  method " + TypeName(method.ReturnType) + " " + method.Name + "("
                    + string.Join(", ", method.GetParameters().Select(p => TypeName(p.ParameterType))) + ")"
                    + (method.IsStatic ? " static" : ""));
            lines.AddRange(members.OrderBy(m => m, StringComparer.Ordinal));
        }
        return string.Join("\n", lines) + "\n";
    }

    private static IEnumerable<string> Bases(Type type)
    {
        if (type.BaseType is { } baseType && baseType != typeof(object) && baseType != typeof(Attribute))
            yield return TypeName(baseType);
        foreach (var contract in type.GetInterfaces().OrderBy(i => i.FullName, StringComparer.Ordinal))
            yield return TypeName(contract);
    }

    private static string TypeName(Type type)
    {
        if (type.IsGenericType)
        {
            string name = type.GetGenericTypeDefinition().FullName ?? type.Name;
            int tick = name.IndexOf('`');
            if (tick >= 0) name = name[..tick];
            return name + "<" + string.Join(", ", type.GetGenericArguments().Select(TypeName)) + ">";
        }
        return type.FullName ?? type.Name;
    }
}
