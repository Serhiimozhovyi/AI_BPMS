using OpenBP.Worker;

await using var app = WorkerHost.Build(args);
await app.RunAsync();
