using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PolyMount.Service;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options => options.ServiceName = "PolyMount");
int configIndex = Array.IndexOf(args, "--config");
string? configPath = configIndex >= 0 && configIndex + 1 < args.Length ? Path.GetFullPath(args[configIndex + 1]) : null;
builder.Services.AddSingleton<MountManager>(services => new MountManager(services.GetRequiredService<IHostApplicationLifetime>(), configPath));
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
