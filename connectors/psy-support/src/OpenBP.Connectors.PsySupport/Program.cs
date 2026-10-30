using OpenBP.Connectors.PsySupport.Hosting;
using OpenBP.Connectors.PsySupport.Import;

// `import --dir <папка>` — одноразовий імпорт CSV з Google Sheets (§9); `--http <url>` — /mcp, /api/v1 і адмінка;
// без аргументів — stdio-MCP (так AI_BPMS запускає конектор як процес).
if (args is ["import", "--dir", var dir, ..])
{
    using var importHost = PsyHost.BuildStdio([]);
    await PsyHost.InitializeAsync(importHost.Services);
    var report = await SheetImporter.RunAsync(importHost.Services, dir);
    Console.WriteLine(report.ToJson());
    return report.Failed ? 1 : 0;
}

var http = Array.IndexOf(args, "--http");
if (http >= 0 && http + 1 < args.Length)
{
    await using var app = PsyHost.BuildHttp(args, args[http + 1]);
    await PsyHost.InitializeAsync(app.Services);
    await app.RunAsync();
    return 0;
}

using var host = PsyHost.BuildStdio(args);
await PsyHost.InitializeAsync(host.Services);
await host.RunAsync();
return 0;
