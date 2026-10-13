using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PolyMount.Service;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options => options.ServiceName = "PolyMount");
builder.Services.AddSingleton<MountManager>();
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
