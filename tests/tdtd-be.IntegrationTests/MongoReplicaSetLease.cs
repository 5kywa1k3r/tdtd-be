using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using MongoDB.Bson;
using MongoDB.Driver;

namespace tdtd_be.IntegrationTests;

internal sealed class MongoReplicaSetLease : IAsyncDisposable
{
    private readonly HarnessPaths _paths;
    private readonly string _iterationRoot;
    private readonly ManagedChildProcess _process;
    private readonly MongoClient _directClient;
    private readonly string _databaseNamePrefix;
    private bool _processStopped;
    private readonly int _processId;

    private MongoReplicaSetLease(
        HarnessPaths paths,
        string iterationRoot,
        string mongodPath,
        string replicaSetName,
        string databaseName,
        string databaseNamePrefix,
        int port,
        string dataDirectory,
        string mongoLogPath,
        string stdoutPath,
        string stderrPath,
        ManagedChildProcess process,
        MongoClient directClient,
        MongoClient client)
    {
        _paths = paths;
        _iterationRoot = iterationRoot;
        MongodPath = mongodPath;
        ReplicaSetName = replicaSetName;
        DatabaseName = databaseName;
        _databaseNamePrefix = databaseNamePrefix;
        Port = port;
        DataDirectory = dataDirectory;
        MongoLogPath = mongoLogPath;
        StdoutPath = stdoutPath;
        StderrPath = stderrPath;
        _process = process;
        _processId = process.Id;
        _directClient = directClient;
        Client = client;
        ConnectionString = $"mongodb://127.0.0.1:{port}/?replicaSet={Uri.EscapeDataString(replicaSetName)}&retryWrites=true&serverSelectionTimeoutMS=10000";
    }

    public string MongodPath { get; }
    public string ReplicaSetName { get; }
    public string DatabaseName { get; }
    public int Port { get; }
    public string DataDirectory { get; }
    public string MongoLogPath { get; }
    public string StdoutPath { get; }
    public string StderrPath { get; }
    public string ConnectionString { get; }
    public MongoClient Client { get; }
    public int ProcessId => _processId;
    public bool DatabaseDropVerified { get; private set; }
    public bool ProcessStopVerified { get; private set; }
    public bool PortReleaseVerified { get; private set; }
    public bool DataDirectoryRemovalVerified { get; private set; }

    public static async Task<MongoReplicaSetLease> StartAsync(
        HarnessPaths paths,
        string iterationRoot,
        string runKey,
        int iteration,
        CancellationToken ct)
        => await StartCoreAsync(
            paths,
            iterationRoot,
            runKey,
            iteration,
            replicaSetPrefix: "p1rs_",
            databaseNamePrefix: "tdtd_p1_",
            ct);

    public static async Task<MongoReplicaSetLease> StartP9Async(
        HarnessPaths paths,
        string iterationRoot,
        string runKey,
        int iteration,
        CancellationToken ct)
        => await StartCoreAsync(
            paths,
            iterationRoot,
            runKey,
            iteration,
            replicaSetPrefix: "p9rs_",
            databaseNamePrefix: "tdtd_p9_",
            ct);

    public static async Task<MongoReplicaSetLease> StartP10Async(
        HarnessPaths paths,
        string iterationRoot,
        string runKey,
        int iteration,
        CancellationToken ct)
        => await StartCoreAsync(
            paths,
            iterationRoot,
            runKey,
            iteration,
            replicaSetPrefix: "p10rs_",
            databaseNamePrefix: "tdtd_p10_",
            ct);

    public static async Task<MongoReplicaSetLease> StartP11Async(
        HarnessPaths paths,
        string iterationRoot,
        string runKey,
        int iteration,
        CancellationToken ct)
        => await StartCoreAsync(
            paths,
            iterationRoot,
            runKey,
            iteration,
            replicaSetPrefix: "p11rs_",
            databaseNamePrefix: "tdtd_p11_",
            ct);

    private static async Task<MongoReplicaSetLease> StartCoreAsync(
        HarnessPaths paths,
        string iterationRoot,
        string runKey,
        int iteration,
        string replicaSetPrefix,
        string databaseNamePrefix,
        CancellationToken ct)
    {
        if (replicaSetPrefix is not ("p1rs_" or "p9rs_" or "p10rs_" or "p11rs_") ||
            databaseNamePrefix is not ("tdtd_p1_" or "tdtd_p9_" or "tdtd_p10_" or "tdtd_p11_"))
        {
            throw new InvalidOperationException(
                "Mongo integration lease prefixes must use a frozen harness mode.");
        }
        var mongodPath = HarnessPaths.ResolveMongodPath();
        var port = PortAllocator.GetFreeTcpPort();
        // Keep the iteration discriminator outside the truncation window so two
        // sequential clean runs can never resolve to the same logical names.
        var suffix = $"{SanitizeToken(runKey, 24)}_{iteration:00}";
        var replicaSetName = $"{replicaSetPrefix}{suffix}";
        var databaseName = $"{databaseNamePrefix}{suffix}";
        var dataDirectory = Path.Combine(iterationRoot, "mongo-data");
        var mongoLogPath = Path.Combine(iterationRoot, "mongod.log");
        var stdoutPath = Path.Combine(iterationRoot, "mongo.stdout.log");
        var stderrPath = Path.Combine(iterationRoot, "mongo.stderr.log");
        Directory.CreateDirectory(dataDirectory);

        var process = ManagedChildProcess.Start(
            mongodPath,
            new[]
            {
                "--dbpath", dataDirectory,
                "--port", port.ToString(CultureInfo.InvariantCulture),
                "--bind_ip", "127.0.0.1",
                "--replSet", replicaSetName,
                "--oplogSize", "64",
                "--logpath", mongoLogPath,
                "--logappend"
            },
            iterationRoot,
            new Dictionary<string, string?>
            {
                [P11ContinuationSnapshot.KeyEnvironmentVariable] = null
            },
            stdoutPath,
            stderrPath);

        var directSettings = MongoClientSettings.FromConnectionString(
            $"mongodb://127.0.0.1:{port}/?directConnection=true&serverSelectionTimeoutMS=1000");
        directSettings.ConnectTimeout = TimeSpan.FromSeconds(2);
        directSettings.SocketTimeout = TimeSpan.FromSeconds(5);
        var directClient = new MongoClient(directSettings);

        try
        {
            await WaitForPingAsync(directClient, process, mongoLogPath, stderrPath, ct);
            var host = $"127.0.0.1:{port}";
            var config = new BsonDocument
            {
                { "_id", replicaSetName },
                {
                    "members",
                    new BsonArray
                    {
                        new BsonDocument { { "_id", 0 }, { "host", host } }
                    }
                }
            };
            await directClient.GetDatabase("admin").RunCommandAsync<BsonDocument>(
                new BsonDocument("replSetInitiate", config),
                cancellationToken: ct);
            await WaitForPrimaryAsync(directClient, process, mongoLogPath, stderrPath, ct);

            var replicaSettings = MongoClientSettings.FromConnectionString(
                $"mongodb://127.0.0.1:{port}/?replicaSet={Uri.EscapeDataString(replicaSetName)}&retryWrites=true&serverSelectionTimeoutMS=10000");
            replicaSettings.ConnectTimeout = TimeSpan.FromSeconds(5);
            var client = new MongoClient(replicaSettings);
            await client.GetDatabase("admin").RunCommandAsync<BsonDocument>(
                new BsonDocument("ping", 1),
                cancellationToken: ct);

            return new MongoReplicaSetLease(
                paths,
                iterationRoot,
                mongodPath,
                replicaSetName,
                databaseName,
                databaseNamePrefix,
                port,
                dataDirectory,
                mongoLogPath,
                stdoutPath,
                stderrPath,
                process,
                directClient,
                client);
        }
        catch (Exception startupError)
        {
            await process.DisposeAsync();
            try
            {
                RemoveDataDirectoryGuarded(paths, dataDirectory);
            }
            catch (Exception cleanupError)
            {
                throw new AggregateException(
                    "Mongo startup failed and its isolated data directory could not be cleaned.",
                    startupError,
                    cleanupError);
            }
            throw;
        }
    }

    public async Task DropDatabaseGuardedAsync(CancellationToken ct)
    {
        if (!DatabaseName.StartsWith(_databaseNamePrefix, StringComparison.Ordinal) ||
            DatabaseName.Length <= _databaseNamePrefix.Length ||
            DatabaseName.Any(ch => !(char.IsLetterOrDigit(ch) || ch == '_')))
        {
            throw new InvalidOperationException($"Refusing to drop unguarded database name '{DatabaseName}'.");
        }

        await Client.DropDatabaseAsync(DatabaseName, ct);
        var remaining = await Client.ListDatabaseNames().ToListAsync(ct);
        if (remaining.Contains(DatabaseName, StringComparer.Ordinal))
            throw new InvalidOperationException($"Database '{DatabaseName}' still exists after guarded drop.");
        DatabaseDropVerified = true;
    }

    public async Task StopProcessAsync()
    {
        if (!_processStopped)
        {
            try
            {
                await _directClient.GetDatabase("admin").RunCommandAsync<BsonDocument>(
                    new BsonDocument { { "shutdown", 1 }, { "force", true } });
            }
            catch (MongoException)
            {
                // A successful shutdown closes the connection before a response can be read.
            }
            catch (TimeoutException)
            {
                // Kill fallback below is scoped to the exact child process.
            }

            await _process.StopAsync(TimeSpan.FromSeconds(15));
            if (!_process.HasExited)
                throw new InvalidOperationException($"mongod child process {_process.Id} is still running after stop.");
            _processStopped = true;
            ProcessStopVerified = true;
        }

        if (!PortReleaseVerified)
        {
            await PortAllocator.WaitUntilNotAcceptingAsync(
                Port,
                TimeSpan.FromSeconds(10),
                "MongoDB");
            PortReleaseVerified = true;
        }

        await EvidenceJson.WriteAsync(
            Path.Combine(_iterationRoot, "mongo-process-cleanup.json"),
            new
            {
                processId = ProcessId,
                port = Port,
                replicaSetName = ReplicaSetName,
                databaseName = DatabaseName,
                databaseNamePrefix = _databaseNamePrefix,
                processStopped = ProcessStopVerified,
                portReleased = PortReleaseVerified
            });
    }

    public void RemoveDataDirectoryGuarded()
    {
        RemoveDataDirectoryGuarded(_paths, DataDirectory);
        DataDirectoryRemovalVerified = true;
    }

    public async ValueTask DisposeAsync()
    {
        Exception? stopError = null;
        try
        {
            await StopProcessAsync();
        }
        catch (Exception error)
        {
            stopError = error;
        }

        try
        {
            await _process.DisposeAsync();
        }
        catch (Exception disposeError)
        {
            if (stopError is not null)
            {
                throw new AggregateException(
                    "Mongo process stop and disposal both failed.",
                    stopError,
                    disposeError);
            }
            throw;
        }

        if (stopError is not null)
        {
            throw new InvalidOperationException(
                "Mongo process disposal completed after stop verification failed.",
                stopError);
        }
    }

    private static async Task WaitForPingAsync(
        MongoClient client,
        ManagedChildProcess process,
        string mongoLogPath,
        string stderrPath,
        CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(45);
        Exception? last = null;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (process.HasExited)
                throw MongoExited(process, mongoLogPath, stderrPath);
            try
            {
                await client.GetDatabase("admin").RunCommandAsync<BsonDocument>(
                    new BsonDocument("ping", 1),
                    cancellationToken: ct);
                return;
            }
            catch (Exception ex) when (ex is MongoException or TimeoutException)
            {
                last = ex;
            }
            await Task.Delay(250, ct);
        }

        throw new TimeoutException($"Mongo ping timeout. Last={last?.Message}\n{LogTail.Read(mongoLogPath)}");
    }

    private static async Task WaitForPrimaryAsync(
        MongoClient client,
        ManagedChildProcess process,
        string mongoLogPath,
        string stderrPath,
        CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        Exception? last = null;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (process.HasExited)
                throw MongoExited(process, mongoLogPath, stderrPath);
            try
            {
                var hello = await client.GetDatabase("admin").RunCommandAsync<BsonDocument>(
                    new BsonDocument("hello", 1),
                    cancellationToken: ct);
                if (hello.TryGetValue("isWritablePrimary", out var value) && value.ToBoolean())
                    return;
            }
            catch (Exception ex) when (ex is MongoException or TimeoutException)
            {
                last = ex;
            }
            await Task.Delay(250, ct);
        }

        throw new TimeoutException($"Mongo replica-set PRIMARY timeout. Last={last?.Message}\n{LogTail.Read(mongoLogPath)}");
    }

    private static Exception MongoExited(
        ManagedChildProcess process,
        string mongoLogPath,
        string stderrPath)
        => new InvalidOperationException(
            $"mongod exited with code {process.ExitCode}.\nMONGO LOG:\n{LogTail.Read(mongoLogPath)}\nSTDERR:\n{LogTail.Read(stderrPath)}");

    private static string SanitizeToken(string value, int maxLength)
    {
        var normalized = new string(value.ToLowerInvariant()
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : '_')
            .ToArray());
        return normalized.Length <= maxLength ? normalized : normalized[..maxLength];
    }

    private static void RemoveDataDirectoryGuarded(HarnessPaths paths, string dataDirectory)
    {
        var allowedRoot = Path.GetFullPath(paths.IntegrationRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(dataDirectory);
        if (!target.StartsWith(allowedRoot, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetFileName(target), "mongo-data", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Refusing to remove unsafe Mongo data directory '{target}'.");
        }

        IOException? lastIoError = null;
        for (var attempt = 1; attempt <= 50 && Directory.Exists(target); attempt++)
        {
            try
            {
                Directory.Delete(target, recursive: true);
                lastIoError = null;
            }
            catch (IOException ex) when (attempt < 50)
            {
                lastIoError = ex;
                Thread.Sleep(100);
            }
        }
        if (Directory.Exists(target))
            throw new InvalidOperationException(
                $"Mongo data directory '{target}' still exists after bounded cleanup retries. Last={lastIoError?.Message}",
                lastIoError);
    }
}

internal static class PortAllocator
{
    public static int GetFreeBrowserHttpPort()
    {
        const int minimum = 20_000;
        const int maximumExclusive = 45_001;
        for (var attempt = 0; attempt < 128; attempt++)
        {
            var port = Random.Shared.Next(minimum, maximumExclusive);
            var listener = new TcpListener(IPAddress.Loopback, port);
            try
            {
                listener.Start();
                return port;
            }
            catch (SocketException ex) when (
                ex.SocketErrorCode is SocketError.AddressAlreadyInUse or
                    SocketError.AccessDenied)
            {
                // Retry another browser-safe high port.
            }
            finally
            {
                listener.Stop();
            }
        }

        throw new InvalidOperationException(
            "Unable to allocate a browser-safe HTTP port after 128 attempts.");
    }

    public static int GetFreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    public static async Task WaitUntilNotAcceptingAsync(
        int port,
        TimeSpan timeout,
        string owner)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            if (!await IsAcceptingAsync(port))
                return;
            await Task.Delay(100);
        }

        throw new InvalidOperationException(
            $"{owner} TCP port {port} is still accepting connections after {timeout.TotalSeconds:0.#} seconds.");
    }

    private static async Task<bool> IsAcceptingAsync(int port)
    {
        using var client = new TcpClient(AddressFamily.InterNetwork);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        try
        {
            await client.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
