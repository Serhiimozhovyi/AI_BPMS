using OpenBP.Connectors.MsGraph;

// stdio — так OpenBP запускає конектор як процес; `--http <url>` — окремий сервіс зі Streamable HTTP на <url>/mcp.
var http = Array.IndexOf(args, "--http");
if (http >= 0 && http + 1 < args.Length)
{
    await using var app = ConnectorHost.BuildHttp(args, args[http + 1]);
    await app.RunAsync();
    return;
}

using var host = ConnectorHost.BuildStdio(args);
await host.RunAsync();
