using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using MongoDB.Bson;
using MongoDB.Driver;

namespace tdtd_be.CanvasRoundtripTests;

internal sealed class CanvasMongo : IAsyncDisposable
{
    private readonly Process child;
    private readonly MongoClient direct;
    private readonly Task stdout;
    private readonly Task stderr;
    public string Root { get; }
    public string Connection { get; }
    public string Database { get; }
    public int Pid => child.Id;
    private CanvasMongo(string root, Lease lease, Process process, Task output, Task errors)
    {
        Root = root; Database = lease.Database; child = process; stdout = output; stderr = errors;
        Connection = $"mongodb://127.0.0.1:{lease.Port}/?replicaSet={lease.ReplicaSet}&serverSelectionTimeoutMS=5000";
        direct = new MongoClient($"mongodb://127.0.0.1:{lease.Port}/?directConnection=true&serverSelectionTimeoutMS=1000");
    }
    private sealed record Lease(int Port, string ReplicaSet, string Database);
    public static async Task<CanvasMongo> StartAsync(string rawRoot, string mongod)
    {
        var root = Path.GetFullPath(rawRoot);
        var parent = Directory.GetParent(root)!;
        if (parent.Name != "canvas-save-readback-20260916-artifacts" || !Path.GetFileName(root).StartsWith("run-", StringComparison.Ordinal))
            throw new ArgumentException("Use a new run-* directory inside the Canvas evidence directory.");
        Directory.CreateDirectory(root);
        var manifest = Path.Combine(root, "mongo-lease.json");
        Lease lease;
        var fresh = !File.Exists(manifest);
        if (fresh)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
            var suffix = Guid.NewGuid().ToString("N")[..16];
            lease = new(port, "canvas_rs_" + suffix, "canvas_test_" + suffix);
            await File.WriteAllTextAsync(manifest, JsonSerializer.Serialize(lease));
        }
        else lease = JsonSerializer.Deserialize<Lease>(await File.ReadAllTextAsync(manifest))!;
        if (lease.Port < 1024 || !lease.Database.StartsWith("canvas_test_", StringComparison.Ordinal)
            || !lease.ReplicaSet.StartsWith("canvas_rs_", StringComparison.Ordinal)) throw new InvalidOperationException("Unsafe lease.");
        var data = Path.Combine(root, "mongo-data"); Directory.CreateDirectory(data);
        var start = new ProcessStartInfo(Path.GetFullPath(mongod))
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = root
        };
        foreach (var arg in new[] { "--dbpath", data, "--port", lease.Port.ToString(), "--bind_ip", "127.0.0.1", "--replSet", lease.ReplicaSet,
            "--oplogSize", "32", "--wiredTigerCacheSizeGB", "0.25" }) start.ArgumentList.Add(arg);
        var child = Process.Start(start) ?? throw new InvalidOperationException("Could not start dedicated Mongo child.");
        static async Task Copy(StreamReader reader, string file)
        { await using var writer = new StreamWriter(file, append: true); while (await reader.ReadLineAsync() is { } line) await writer.WriteLineAsync(line); }
        var instance = new CanvasMongo(root, lease, child, Copy(child.StandardOutput, Path.Combine(root, "mongo.stdout.log")),
            Copy(child.StandardError, Path.Combine(root, "mongo.stderr.log")));
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            await instance.WaitAsync(false, timeout.Token);
            if (fresh) await instance.direct.GetDatabase("admin").RunCommandAsync<BsonDocument>(new BsonDocument("replSetInitiate",
                new BsonDocument { { "_id", lease.ReplicaSet }, { "members", new BsonArray { new BsonDocument { { "_id", 0 }, { "host", $"127.0.0.1:{lease.Port}" } } } } }), cancellationToken: timeout.Token);
            await instance.WaitAsync(true, timeout.Token);
            return instance;
        }
        catch { await instance.DisposeAsync(); throw; }
    }
    private async Task WaitAsync(bool primary, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (child.HasExited) throw new InvalidOperationException("Dedicated Mongo exited; see owned run logs.");
            try
            {
                var result = await direct.GetDatabase("admin").RunCommandAsync<BsonDocument>(new BsonDocument(primary ? "hello" : "ping", 1), cancellationToken: ct);
                if (!primary || result.GetValue("isWritablePrimary", false).ToBoolean()) return;
            }
            catch (Exception error) when (error is MongoException or TimeoutException) { }
            await Task.Delay(200, ct);
        }
    }
    public async ValueTask DisposeAsync()
    {
        if (!child.HasExited)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                await direct.GetDatabase("admin").RunCommandAsync<BsonDocument>(new BsonDocument { { "shutdown", 1 }, { "force", true } }, cancellationToken: timeout.Token);
            }
            catch (Exception error) when (error is MongoException or TimeoutException or OperationCanceledException) { }
            using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            try { await child.WaitForExitAsync(wait.Token); }
            catch (OperationCanceledException) { child.Kill(); await child.WaitForExitAsync(); }
        }
        await Task.WhenAll(stdout, stderr);
        await File.WriteAllTextAsync(Path.Combine(Root, "stopped.json"), JsonSerializer.Serialize(new { mongoPid = Pid, stopped = child.HasExited, dataRetained = true }));
        child.Dispose();
    }
}
