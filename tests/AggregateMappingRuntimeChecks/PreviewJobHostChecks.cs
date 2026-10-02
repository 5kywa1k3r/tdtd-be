using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using MongoDB.Driver;
using tdtd_be.Controllers;
using tdtd_be.Data;
using tdtd_be.DTOs.AggregateMapping;
using tdtd_be.Models;
using tdtd_be.Services.AggregateMapping.Persistence;

internal static class PreviewJobHostChecks
{
    // Real Program/Hangfire DI, but a fresh storage prefix containing only this test's job.
    internal static async Task Run(MongoDbContext db, string actor, AggregatePreviewJobRequest request, string run, CancellationToken ct)
    {
        var socket = new TcpListener(IPAddress.Loopback, 0); socket.Start(); var port = ((IPEndPoint)socket.LocalEndpoint).Port; socket.Stop();
        var prefix = "p05_job_" + run.Replace('-', '_');
        var start = new ProcessStartInfo("dotnet") { WorkingDirectory = Directory.GetCurrentDirectory(), UseShellExecute = false,
            CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(typeof(AggregateMappingPreviewController).Assembly.Location);
        foreach (var pair in new Dictionary<string, string> {
            ["ASPNETCORE_ENVIRONMENT"] = "Testing", ["DOTNET_ENVIRONMENT"] = "Testing", ["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}",
            ["Mongo__ConnectionString"] = "mongodb://localhost:27017/?replicaSet=tdtd-rs", ["Mongo__Database"] = "tdtd", ["Mongo__TestingSkipIndexInitialization"] = "false",
            ["Hangfire__ServerEnabled"] = "true", ["Hangfire__DashboardEnabled"] = "false", ["Hangfire__RecurringRegistrationEnabled"] = "false",
            ["Hangfire__Prefix"] = prefix, ["Hangfire__SchedulePollingSeconds"] = "5", ["Redis__Enabled"] = "false", ["Frontend__Enabled"] = "false",
            ["Jwt__Issuer"] = run, ["Jwt__Audience"] = run, ["Jwt__Key"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)),
            ["AggregateMapping__V2Enabled"] = "true", ["AggregateMapping__ConfirmationKeyBase64"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) }) start.Environment[pair.Key] = pair.Value;
        var user = await db.Users.Find(u => u.Id == actor).SingleAsync(ct);
        var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));
        user.Username = user.Username.ToLowerInvariant(); user.PasswordHash = new PasswordHasher<AppUser>().HashPassword(user, password);
        await db.Users.ReplaceOneAsync(u => u.Id == actor, user, cancellationToken: ct);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("preview job host startup failed");
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/api/") };
        try
        {
            for (var i = 0; i < 120; i++)
            {
                try { using var response = await http.GetAsync("p05-nonexistent", ct); break; }
                catch (HttpRequestException) when (!process.HasExited && i < 119) { await Task.Delay(250, ct); }
            }
            Console.WriteLine($"JOBHOST pid={process.Id} port={port} database=tdtd prefix={prefix} recurring=OFF dashboard=OFF");
            async Task<JsonElement> Post(string path, object body)
            {
                using var response = await http.PostAsJsonAsync(path, body, AggregateCanonical.Json, ct);
                var json = await response.Content.ReadAsStringAsync(ct);
                if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Job host {path}: {(int)response.StatusCode} {json}");
                if (path.StartsWith("aggregate-v2/") && response.Headers.CacheControl?.NoStore != true) throw new InvalidOperationException("Job response may be cached");
                return JsonSerializer.Deserialize<JsonElement>(json);
            }
            void Check(bool value, string name) { if (!value) throw new InvalidOperationException(name); Console.WriteLine("PASS job-host " + name); }
            var login = await Post("auth/login", new { username = user.Username, password });
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.GetProperty("accessToken").GetString());
            var first = await Post("aggregate-v2/preview-jobs/start", request);
            var id = first.GetProperty("id").GetString()!;
            var repeated = await Post("aggregate-v2/preview-jobs/start", request);
            Check(repeated.GetProperty("id").GetString() == id, "real HTTP start deduplicates within authenticated session");
            var current = await Post("aggregate-v2/preview-jobs/current", new { request.Context });
            Check(current.GetProperty("current").GetProperty("id").GetString() == id, "reopen endpoint recovers the same run");
            var final = first;
            for (var i = 0; i < 240 && final.GetProperty("state").GetString() is "QUEUED" or "RUNNING"; i++)
            {
                await Task.Delay(250, ct); final = await Post($"aggregate-v2/preview-jobs/{id}/read", new { });
            }
            Check(final.GetProperty("state").GetString() == "COMPLETED" && final.GetProperty("attempt").GetInt32() == 1,
                "existing Hangfire server activates worker and completes one leased attempt");
            Check(final.GetProperty("result").GetProperty("preview").GetProperty("results")[0].GetProperty("value").GetString() == "10",
                "real background worker reads native source and returns exact result");
            var legacy = await http.PostAsJsonAsync("aggregate-mapping/preview-jobs/start", request, AggregateCanonical.Json, ct);
            Check(!legacy.IsSuccessStatusCode, "job does not open legacy aggregate route");
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory, run + "-preview-job-host.log"), await stdout + await stderr);
            await db.Users.UpdateOneAsync(u => u.Id == actor, Builders<AppUser>.Update.Set(u => u.PasswordHash, "!P05_RETIRED!"));
            await db.RefreshTokens.UpdateManyAsync(t => t.UserId == actor && t.RevokedAt == null,
                Builders<RefreshTokenDoc>.Update.Set(t => t.RevokedAt, DateTime.UtcNow));
        }
    }
}
