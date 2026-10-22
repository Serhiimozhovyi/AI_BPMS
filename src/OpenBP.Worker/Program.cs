using Microsoft.Extensions.Hosting;
using OpenBP.Worker;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddOpenBpWorker(builder.Configuration);
using var host = builder.Build();
await host.RunAsync();
