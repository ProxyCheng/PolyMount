using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PolyMount.Service;
using Microsoft.Extensions.Logging;
using PolyMount.Core;
using Serilog;

var options = ServiceOptions.Parse(args);
// A development host carries the two facts that let it run a plugin built outside the
// installation — and nothing else about the host changes. It is set before anything reads the
// configuration, because loading is where it matters.
PluginLoader.AllowExternalPlugins = options.Dev;
ControlEnvelope.Dev = options.Dev;
PluginLoader.ExtraPaths = options.PluginPaths;
if (options.Dev && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("POLYMOUNT_LOG_DIR")))
    Environment.SetEnvironmentVariable("POLYMOUNT_LOG_DIR",
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PolyMount", "dev", "logs"));

int configIndex = Array.IndexOf(args, "--config");
string configPath = configIndex >= 0 && configIndex + 1 < args.Length
    ? Path.GetFullPath(args[configIndex + 1])
    : Path.Combine(AppContext.BaseDirectory, "config.json");

// One instance, in a process of its own: --instance <id>. No host and no manager — the supervisor
// owns the configuration and the instance list, and this process owns one mount and answers for it
// on the pipe named for that id.
int instanceIndex = Array.IndexOf(args, "--instance");
if (instanceIndex >= 0 && instanceIndex + 1 < args.Length)
{
    string instanceId = args[instanceIndex + 1];
    // Its own log beside the service's, named for the instance so two workers never write into
    // one file, and matched by the same family the log inventory reports.
    using var workerLog = AsyncFileLog.Create(ServiceLogs.PathOf(MountManager.WorkerLogName(instanceId)));
    using var workerLogger = LoggerFactory.Create(builder => builder.AddSerilog(workerLog, dispose: false));
    return await InstanceHost.Run(configPath, instanceId, workerLogger.CreateLogger<InstanceHost>());
}

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
builder.Services.AddSerilog(AsyncFileLog.Create(ServiceLogs.PathOf(MountManager.ServiceLogName)), dispose: true);
builder.Services.AddWindowsService(options => options.ServiceName = "PolyMount");
builder.Services.AddSingleton<MountManager>(services => new MountManager(services.GetRequiredService<IHostApplicationLifetime>(), configPath,
    services.GetRequiredService<ILogger<MountManager>>(), options));
builder.Services.AddHostedService<SupervisorLoop>();

var host = builder.Build();
host.Run();
return 0;
