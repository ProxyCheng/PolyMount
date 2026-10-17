using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Xml.Linq;
using PolyMount.Core;

namespace PolyMount.Plugins.Svn;

/// <summary>
/// Repository access by invoking the svn command line client. Each request spawns a
/// process, so this backend is reserved for URL schemes the HTTP transport cannot
/// serve and as the fallback when that transport is refused.
/// </summary>
internal sealed class SvnClient : ISvnBackend
{
    private const string Exe = "svn.exe";

    private readonly string _url;
    private readonly string _trustFailures;
    private readonly string? _username;
    private readonly string? _password;

    public SvnClient(string url, string trustFailures, string? username = null, string? password = null)
    {
        _url = url.TrimEnd('/');
        _trustFailures = trustFailures;
        _username = username;
        _password = password;
    }

    public string Url => _url;

    // Each request owns its process; nothing is held between calls.
    public void Dispose() { }

    private string UrlFor(string rel, long rev)
    {
        var baseUrl = rel.Length == 0 ? _url : _url + "/" + rel;
        return baseUrl + "@" + rev.ToString(CultureInfo.InvariantCulture);
    }

    public long GetHeadRevision(RequestDiagnostics? diagnostics = null)
    {
        var (code, stdout, stderr) = RunText(new[] { "info", _url, "--show-item", "revision" }, diagnostics);
        if (code != 0) throw new InvalidOperationException("svn info failed: " + stderr.Trim());
        return long.Parse(stdout.Trim(), CultureInfo.InvariantCulture);
    }

    public IReadOnlyList<SvnEntry> List(long rev, string rel, RequestDiagnostics? diagnostics = null)
    {
        var (code, stdout, stderr) = RunText(new[] { "list", "--xml", UrlFor(rel, rev) }, diagnostics);
        if (code != 0)
        {
            var msg = stderr.Trim();
            if (IsNotFound(msg)) throw new SvnNotFoundException(msg);
            throw new InvalidOperationException("svn list failed: " + msg);
        }

        var result = new List<SvnEntry>();
        foreach (var entry in XDocument.Parse(stdout).Descendants("entry"))
        {
            var kind = (string?)entry.Attribute("kind") ?? "file";
            var name = (string?)entry.Element("name") ?? string.Empty;
            if (name.Length == 0) continue;

            long size = 0;
            var sizeEl = entry.Element("size");
            if (sizeEl != null) long.TryParse(sizeEl.Value, out size);

            var timeUtc = DateTime.UnixEpoch;
            string contentVersion = string.Empty;
            var commitEl = entry.Element("commit");
            var dateEl = commitEl?.Element("date");
            if (dateEl != null && DateTime.TryParse(dateEl.Value, null,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed))
                timeUtc = parsed;
            // svn list names the revision the entry last changed in. It identifies the content
            // at this path independently of the revision being listed, so a cache can keep the
            // bytes that did not move when the mount does.
            if ((string?)commitEl?.Attribute("revision") is { Length: > 0 } revision) contentVersion = revision;

            result.Add(new SvnEntry { Name = name, IsDir = kind == "dir", Size = size, TimeUtc = timeUtc, ContentVersion = contentVersion });
        }
        return result;
    }

    public SvnEntry? Info(long rev, string rel, RequestDiagnostics? diagnostics = null)
    {
        // The URL stays at args[1], where RunText reads the traced path from.
        var (code, stdout, stderr) = RunText(new[] { "info", UrlFor(rel, rev), "--xml" }, diagnostics);
        if (code != 0)
        {
            var msg = stderr.Trim();
            if (IsNotFound(msg)) return null;
            throw new InvalidOperationException("svn info failed: " + msg);
        }

        // svn info names the target in its attributes, not in a child element, so the name to
        // report is the one that was asked for. Only files carry a size.
        var entry = XDocument.Parse(stdout).Descendants("entry").FirstOrDefault();
        if (entry == null) return null;
        long size = 0;
        var sizeEl = entry.Element("size");
        if (sizeEl != null) long.TryParse(sizeEl.Value, out size);

        var timeUtc = DateTime.UnixEpoch;
        string contentVersion = string.Empty;
        var commitEl = entry.Element("commit");
        var dateEl = commitEl?.Element("date");
        if (dateEl != null && DateTime.TryParse(dateEl.Value, null,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed))
            timeUtc = parsed;
        // The revision this path last changed in, not the one it was asked at: see List.
        if ((string?)commitEl?.Attribute("revision") is { Length: > 0 } revision) contentVersion = revision;

        // The name to report is the repository's own spelling, not the one that was asked for:
        // svn reports the canonical URL, and callers rely on seeing the advertised case.
        var url = (string?)entry.Element("url");
        var name = url == null ? VfsPath.Name(rel) : Uri.UnescapeDataString(url[(url.LastIndexOf('/') + 1)..]);
        return new SvnEntry
        {
            Name = name.Length == 0 ? VfsPath.Name(rel) : name,
            IsDir = (string?)entry.Attribute("kind") == "dir", Size = size, TimeUtc = timeUtc,
            ContentVersion = contentVersion
        };
    }

    /// <summary>A path absent at this revision is an answer, not a transport failure: it is
    /// reported as null rather than as an exception, so callers that probe many paths stay cheap.</summary>
    private static bool IsNotFound(string stderr) =>
        stderr.Contains("E160013") || stderr.Contains("E200009") || stderr.Contains("E170000") ||
        stderr.Contains("not found") || stderr.Contains("does not exist");


    public void CatToFile(long rev, string rel, string dest, RequestDiagnostics? diagnostics = null)
    {
        using var request = RequestDiagnostics.Begin("SVN.cat", UrlFor(rel, rev), diagnostics);
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        var tmp = dest + ".part-" + Guid.NewGuid().ToString("N");

        var psi = new ProcessStartInfo(Exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        AddCommon(psi);
        psi.ArgumentList.Add("cat");
        psi.ArgumentList.Add(UrlFor(rel, rev));

        using var proc = Process.Start(psi)!;
        request?.Stage($"waiting subprocess pid={proc.Id} timeoutMs=120000");
        var stderrTask = proc.StandardError.ReadToEndAsync();
        try
        {
            using var fs = File.Create(tmp);
            var copy = proc.StandardOutput.BaseStream.CopyToAsync(fs);
            if (!Task.WhenAll(copy, proc.WaitForExitAsync()).Wait(TimeSpan.FromSeconds(120)))
            { request?.Stage("timeout"); proc.Kill(entireProcessTree: true); throw new TimeoutException("svn cat timed out after 120 seconds"); }
        }
        catch { if (!proc.HasExited) proc.Kill(entireProcessTree: true); TryDelete(tmp); throw; }
        var stderr = stderrTask.GetAwaiter().GetResult();
        request?.Stage($"exitCode={proc.ExitCode}");

        if (proc.ExitCode != 0)
        {
            TryDelete(tmp);
            throw new InvalidOperationException("svn cat failed: " + stderr.Trim());
        }

        if (File.Exists(dest)) File.Delete(dest);
        File.Move(tmp, dest);
    }

    private (int Code, string Stdout, string Stderr) RunText(string[] args, RequestDiagnostics? diagnostics = null)
    {
        // Only operation arguments are traced, never AddCommon credentials.
        using var request = RequestDiagnostics.Begin("SVN." + args[0], args[0] == "list" ? args[2] : args[1], diagnostics);
        var psi = new ProcessStartInfo(Exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        AddCommon(psi);
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi)!;
        request?.Stage($"waiting subprocess pid={proc.Id} timeoutMs=30000");
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();
        if (!Task.WhenAll(stdoutTask, stderrTask, proc.WaitForExitAsync()).Wait(TimeSpan.FromSeconds(30)))
        { request?.Stage("timeout"); proc.Kill(entireProcessTree: true); throw new TimeoutException("svn metadata request timed out after 30 seconds"); }
        var stdout = stdoutTask.GetAwaiter().GetResult();
        var stderr = stderrTask.GetAwaiter().GetResult();
        request?.Stage($"exitCode={proc.ExitCode}");
        return (proc.ExitCode, stdout, stderr);
    }

    private void AddCommon(ProcessStartInfo psi)
    {
        psi.ArgumentList.Add("--non-interactive");
        psi.ArgumentList.Add("--trust-server-cert-failures=" + _trustFailures);
        if (!string.IsNullOrEmpty(_username))
        {
            psi.ArgumentList.Add("--username");
            psi.ArgumentList.Add(_username!);
        }
        if (!string.IsNullOrEmpty(_password))
        {
            psi.ArgumentList.Add("--password");
            psi.ArgumentList.Add(_password!);
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
