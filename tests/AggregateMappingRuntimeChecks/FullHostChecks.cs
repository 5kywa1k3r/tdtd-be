using System.Diagnostics;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;
using Microsoft.AspNetCore.Identity;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Controllers;
using tdtd_be.Data;
using tdtd_be.DTOs.AggregateMapping;
using tdtd_be.DTOs.WorkAssignmentReports;
using tdtd_be.DTOs.WorkAssignments.Review;
using tdtd_be.Models.Enums;
using tdtd_be.Services.AggregateMapping;
using tdtd_be.Services.AggregateMapping.Persistence;

internal static class FullHostChecks
{
    internal static async Task Run(MongoDbContext db, RuntimeFixture fixture, string instanceId, string run, CancellationToken ct)
    {
        var socket = new TcpListener(IPAddress.Loopback, 0); socket.Start(); var port = ((IPEndPoint)socket.LocalEndpoint).Port; socket.Stop();
        var jwtKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
        var start = new ProcessStartInfo("dotnet") { WorkingDirectory = Directory.GetCurrentDirectory(), UseShellExecute = false,
            CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(typeof(AggregateMappingPreviewController).Assembly.Location);
        // Real Program and startup migrations. No secrets persisted; schedulers stay off.
        foreach (var pair in new Dictionary<string, string> {
            ["ASPNETCORE_ENVIRONMENT"] = "Testing", ["DOTNET_ENVIRONMENT"] = "Testing", ["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}",
            ["Mongo__ConnectionString"] = "mongodb://localhost:27017/?replicaSet=tdtd-rs", ["Mongo__Database"] = "tdtd", ["Mongo__TestingSkipIndexInitialization"] = "false",
            ["Hangfire__ServerEnabled"] = "false", ["Hangfire__DashboardEnabled"] = "false", ["Hangfire__RecurringRegistrationEnabled"] = "false",
            ["Hangfire__Prefix"] = "p05_" + run.Replace('-', '_'), ["Redis__Enabled"] = "false", ["Frontend__Enabled"] = "false",
            ["Jwt__Issuer"] = run, ["Jwt__Audience"] = run, ["Jwt__Key"] = jwtKey,
            ["AggregateMapping__V2Enabled"] = "true", ["AggregateMapping__ConfirmationKeyBase64"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) }) start.Environment[pair.Key] = pair.Value;
        using var process = Process.Start(start) ?? throw new InvalidOperationException("P05 full host failed to start");
        var stdout = process.StandardOutput.ReadToEndAsync(ct); var stderr = process.StandardError.ReadToEndAsync(ct);
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/api/") };
        var loginUsers = new HashSet<string>();
        var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));
        try
        {
            var ready = false;
            for (var attempt = 0; attempt < 120 && !process.HasExited; attempt++)
            {
                try { using var ping = await http.GetAsync("p05-nonexistent", ct); ready = true; break; }
                catch (HttpRequestException) { await Task.Delay(250, ct); }
            }
            if (!ready) throw new InvalidOperationException("P05 full Program host not ready; inspect full-host log");
            Console.WriteLine($"FULLHOST pid={process.Id} port={port} database=tdtd environment=Testing schedulers=OFF");
            async Task Actor(string actor)
            {
                var user = await db.Users.Find(u => u.Id == actor).SingleAsync(ct);
                if (loginUsers.Add(actor))
                {
                    user.Username = user.Username.ToLowerInvariant();
                    user.PasswordHash = new PasswordHasher<tdtd_be.Models.AppUser>().HashPassword(user, password);
                    await db.Users.ReplaceOneAsync(u => u.Id == user.Id, user, cancellationToken: ct);
                }
                http.DefaultRequestHeaders.Authorization = null;
                var login = await Send(HttpMethod.Post, "auth/login", new { username = user.Username, password });
                if (login.Status != HttpStatusCode.OK) throw new InvalidOperationException($"Full login failed: {(int)login.Status}");
                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.Body.GetProperty("accessToken").GetString());
            }
            await Actor(fixture.Actor);
            async Task<(HttpStatusCode Status, JsonElement Body)> Send(HttpMethod method, string path, object? body = null)
            {
                using var request = new HttpRequestMessage(method, path);
                if (body != null) request.Content = JsonContent.Create(body, options: AggregateCanonical.Json);
                using var response = await http.SendAsync(request, ct); var text = await response.Content.ReadAsStringAsync(ct);
                if ((int)response.StatusCode >= 500) throw new InvalidOperationException($"Full host {path} {(int)response.StatusCode}: {text}");
                var bodyJson = string.IsNullOrEmpty(text) ? default : JsonSerializer.Deserialize<JsonElement>(text);
                if (bodyJson.ValueKind == JsonValueKind.Object && bodyJson.TryGetProperty("lifecycleProjectionPending", out var pending) && pending.ValueKind == JsonValueKind.True)
                {
                    if (path.StartsWith("work-report-periods/", StringComparison.Ordinal) && path.EndsWith("/open", StringComparison.Ordinal))
                    {
                        var periodId = path.Split('/')[1];
                        var actualPeriod = await db.WorkReportPeriods.Find(p => p.Id == periodId).SingleAsync(ct);
                        var actualReport = await db.WorkAssignmentReports.Find(r => r.Id == actualPeriod.CurrentReportId).SingleAsync(ct);
                        Console.WriteLine("DIAGNOSTIC OpenPeriod persisted projection " + JsonSerializer.Serialize(new {
                            actualReport.LifecycleRevision, actualReport.LifecycleProjectionLastError,
                            Entries = actualReport.LifecycleProjectionOutbox.Select(e => new { e.State, e.LifecycleRevision }) }));
                    }
                    throw new InvalidOperationException($"Full host {path}: native commit has pending projection; inspect full-host log");
                }
                return (response.StatusCode, bodyJson);
            }
            void Check(bool value, string name) { if (!value) throw new InvalidOperationException(name); Console.WriteLine("PASS full-host " + name); }
            async Task<AggregateInstanceState> Instance() => AggregateMongoTransaction.Read<AggregateInstanceState>(await db.Db
                .GetCollection<BsonDocument>(AggregateCollections.Instances).Find(new BsonDocument("_id", instanceId)).SingleAsync(ct)).Value;
            async Task<AggregateLockState> SourceLock() => AggregateMongoTransaction.Read<AggregateLockState>(await db.Db
                .GetCollection<BsonDocument>(AggregateCollections.Locks).Find(new BsonDocument("_id", "REPORT:" + fixture.Source.Id)).SingleAsync(ct)).Value;
            async Task<string> Frozen() => string.Join("\n", (await db.Db.GetCollection<BsonDocument>(AggregateCollections.Frozen)
                .Find(new BsonDocument("target", fixture.Report.Id)).Sort(new BsonDocument("_id", 1)).ToListAsync(ct)).Select(x => x.ToJson()));
            var read = await Send(HttpMethod.Get, "work-assignment-reports/" + fixture.Report.Id);
            Check(read.Status == HttpStatusCode.OK, "real report GET resolves through production service registrations");
            var report = await db.WorkAssignmentReports.Find(r => r.Id == fixture.Report.Id).SingleAsync(ct);
            var save = await Send(HttpMethod.Put, $"work-assignment-reports/{report.Id}/draft", new SaveWorkAssignmentReportDraftRequest {
                CommandId = run + "-full-save", ExpectedPayloadRevision = report.PayloadRevision,
                FieldValuesJson = "{\"total\":999}", Values1D = [] });
            if ((int)save.Status < 400 || !save.Body.GetRawText().Contains("AGG_TARGET_MAPPED_READ_ONLY"))
                throw new InvalidOperationException($"Full SaveDraft {(int)save.Status}: {save.Body}");
            Check((int)save.Status >= 400 && save.Body.GetRawText().Contains("AGG_TARGET_MAPPED_READ_ONLY")
                && (await db.WorkAssignmentReports.Find(r => r.Id == report.Id).SingleAsync(ct)).PayloadRevision == report.PayloadRevision,
                "real SaveDraft API rejects manual overwrite of mapped target without payload write");
            var boot = await Send(HttpMethod.Post, "aggregate-v2/editor/bootstrap", new { reportId = report.Id });
            var context = boot.Body.GetProperty("context").Deserialize<AggregatePeriodContextDto>(AggregateCanonical.Json)!;
            var confirmation = await Send(HttpMethod.Post, $"aggregate-v2/reports/{report.Id}/submission-preview", new AggregateInstanceReadCommandDto(context));
            Check(confirmation.Status == HttpStatusCode.OK, "full host submission preview uses its own server confirmation key");
            var missingConfirmation = await Send(HttpMethod.Post, $"work-assignment-reports/{report.Id}/submit", new SubmitWorkAssignmentReportRequest {
                CommandId = run + "-full-no-confirm", ExpectedPayloadRevision = report.PayloadRevision, ExpectedLifecycleRevision = report.LifecycleRevision,
                LateReason = "P05 fixture" });
            var unchanged = await db.WorkAssignmentReports.Find(r => r.Id == report.Id).SingleAsync(ct);
            Check((int)missingConfirmation.Status >= 400 && missingConfirmation.Body.GetRawText().Contains("AGG_CONFIRMATION_REQUIRED")
                && unchanged.Status == WorkAssignmentReportStatus.Draft && unchanged.PayloadRevision == report.PayloadRevision
                && unchanged.LifecycleRevision == report.LifecycleRevision && (await Instance()).State == "DRAFT" && (await SourceLock()).Owners.Count == 0,
                "real Submit without confirmation rolls back native lifecycle and leaves mapping draft without owner locks");
            var submitCommand = new SubmitWorkAssignmentReportRequest {
                CommandId = run + "-full-submit", ExpectedPayloadRevision = report.PayloadRevision, ExpectedLifecycleRevision = report.LifecycleRevision,
                AggregateConfirmationToken = confirmation.Body.GetProperty("token").GetString(), LateReason = "P05 fixture", Note = "P05 runtime check" };
            var submit = await Send(HttpMethod.Post, $"work-assignment-reports/{report.Id}/submit", submitCommand);
            if (submit.Status is not (HttpStatusCode.OK or HttpStatusCode.Accepted)) throw new InvalidOperationException($"Full Submit {(int)submit.Status}: {submit.Body}");
            report = await db.WorkAssignmentReports.Find(r => r.Id == report.Id).SingleAsync(ct);
            Check(report.Status == WorkAssignmentReportStatus.Submitted, "real Submit API validates and commits native Submitted state");
            Check((await Instance()).State == "FROZEN" && (await SourceLock()).Owners.Count == 1,
                "real Submit commits mapping freeze and source owner lock with native report");
            var frozen = await Frozen();
            var replay = await Send(HttpMethod.Post, $"work-assignment-reports/{report.Id}/submit", submitCommand);
            Check(replay.Status is HttpStatusCode.OK or HttpStatusCode.Accepted
                && (await db.WorkAssignmentReports.Find(r => r.Id == report.Id).SingleAsync(ct)).LifecycleRevision == report.LifecycleRevision
                && frozen == await Frozen() && (await SourceLock()).Owners.Count == 1,
                "real Submit replay does not duplicate lifecycle revision, frozen evidence or owner lock");
            var source = await db.WorkAssignmentReports.Find(r => r.Id == fixture.Source.Id).SingleAsync(ct);
            var recallCommand = new ReturnReportRequest { CommandId = run + "-source-recall", ExpectedPayloadRevision = source.PayloadRevision,
                ExpectedLifecycleRevision = source.LifecycleRevision, Comment = "P05 source recall" };
            var lockedRecall = await Send(HttpMethod.Post, $"work-assignment-review/reports/{source.Id}/recall-approved", recallCommand);
            Check((int)lockedRecall.Status >= 400 && lockedRecall.Body.GetRawText().Contains("AGG_SOURCE_LOCKED")
                && (await db.WorkAssignmentReports.Find(r => r.Id == source.Id).SingleAsync(ct)).Status == WorkAssignmentReportStatus.Approved,
                "real reviewer recall is blocked while an upstream submitted report owns the source");
            var withdraw = await Send(HttpMethod.Post, $"work-assignment-reports/{report.Id}/withdraw-submitted", new ReturnWorkAssignmentReportRequest {
                CommandId = run + "-full-withdraw", ExpectedPayloadRevision = report.PayloadRevision, ExpectedLifecycleRevision = report.LifecycleRevision,
                ReturnReason = "P05 fixture withdrawal" });
            if (withdraw.Status is not (HttpStatusCode.OK or HttpStatusCode.Accepted)) throw new InvalidOperationException($"Full Withdraw {(int)withdraw.Status}: {withdraw.Body}");
            Check((await db.WorkAssignmentReports.Find(r => r.Id == report.Id).SingleAsync(ct)).Status == WorkAssignmentReportStatus.Draft,
                "real WithdrawSubmitted API restores Draft through production lifecycle policy");
            Check((await Instance()).State == "DRAFT" && (await SourceLock()).Owners.Count == 0 && frozen == await Frozen(),
                "real Withdraw releases its owner and preserves frozen historical evidence");
            var recall = await Send(HttpMethod.Post, $"work-assignment-review/reports/{source.Id}/recall-approved", recallCommand);
            if (recall.Status is not (HttpStatusCode.OK or HttpStatusCode.Accepted)) throw new InvalidOperationException($"Full source recall {(int)recall.Status}: {recall.Body}");
            source = await db.WorkAssignmentReports.Find(r => r.Id == source.Id).SingleAsync(ct);
            Check(source.Status == WorkAssignmentReportStatus.Submitted, "same reviewer recall succeeds after final upstream owner releases source");
            var returned = await Send(HttpMethod.Post, $"work-assignment-review/reports/{source.Id}/return", new ReturnReportRequest {
                CommandId = run + "-source-return", ExpectedPayloadRevision = source.PayloadRevision, ExpectedLifecycleRevision = source.LifecycleRevision,
                Comment = "P05 return source to author" });
            if (returned.Status is not (HttpStatusCode.OK or HttpStatusCode.Accepted)) throw new InvalidOperationException($"Full source return {(int)returned.Status}: {returned.Body}");
            source = await db.WorkAssignmentReports.Find(r => r.Id == source.Id).SingleAsync(ct);
            Check(source.Status == WorkAssignmentReportStatus.Draft, "real reviewer Return restores source Draft");
            await db.WorkAssignments.UpdateOneAsync(a => a.Id == source.WorkAssignmentId,
                Builders<tdtd_be.Models.WorkAssignment>.Update.Set(a => a.AutoApproveConditionJson,
                    tdtd_be.Services.WorkAssignments.Domain.WorkAssignmentAutoApproveConditionNormalizer.NormalizeOrNull("{\"enabled\":true}", fixture.SourceForm.FieldsJson)), cancellationToken: ct);
            await Actor(source.AssigneeUserId);
            var autoSubmit = await Send(HttpMethod.Post, $"work-assignment-reports/{source.Id}/submit", new SubmitWorkAssignmentReportRequest {
                CommandId = run + "-source-auto-submit", ExpectedPayloadRevision = source.PayloadRevision, ExpectedLifecycleRevision = source.LifecycleRevision,
                LateReason = "P05 fixture" });
            if (autoSubmit.Status is not (HttpStatusCode.OK or HttpStatusCode.Accepted)) throw new InvalidOperationException($"Full source auto-submit {(int)autoSubmit.Status}: {autoSubmit.Body}");
            source = await db.WorkAssignmentReports.Find(r => r.Id == source.Id).SingleAsync(ct);
            Check(source.Status == WorkAssignmentReportStatus.Approved && source.AutoApprovedAtUtc != null,
                "real Submit auto-approves source using assignment condition and persists approval metadata");
            await Actor(fixture.Actor);
            confirmation = await Send(HttpMethod.Post, $"aggregate-v2/reports/{report.Id}/submission-preview", new AggregateInstanceReadCommandDto(context));
            var refreshedPreview = confirmation.Body.Deserialize<AggregateSubmissionPreview>(AggregateCanonical.Json)!;
            Check(confirmation.Status == HttpStatusCode.OK && refreshedPreview.Instances.Single().Preview.Results.Single().Value?.GetString() == "31"
                && refreshedPreview.Instances.Single().Preview.ContributingSources.Single().ReportId == source.Id,
                "aggregate submission preview accepts source auto-approved by actual report API");
            await db.WorkAssignments.UpdateOneAsync(a => a.Id == fixture.Report.WorkAssignmentId,
                Builders<tdtd_be.Models.WorkAssignment>.Update.Set(a => a.AutoApproveConditionJson,
                    tdtd_be.Services.WorkAssignments.Domain.WorkAssignmentAutoApproveConditionNormalizer.NormalizeOrNull("{\"enabled\":true}", fixture.TargetForm.FieldsJson))
                    .Set(a => a.CreatedByUserId, fixture.Outsider), cancellationToken: ct);
            // A distinct reviewer now owns this synthetic assignment. Re-preview after authority/config changed.
            confirmation = await Send(HttpMethod.Post, $"aggregate-v2/reports/{report.Id}/submission-preview", new AggregateInstanceReadCommandDto(context));
            report = await db.WorkAssignmentReports.Find(r => r.Id == report.Id).SingleAsync(ct);
            var preAutoRevision = report.LifecycleRevision;
            var targetAutoSubmit = await Send(HttpMethod.Post, $"work-assignment-reports/{report.Id}/submit", new SubmitWorkAssignmentReportRequest {
                CommandId = run + "-target-auto-submit", ExpectedPayloadRevision = report.PayloadRevision, ExpectedLifecycleRevision = report.LifecycleRevision,
                AggregateConfirmationToken = confirmation.Body.GetProperty("token").GetString(), LateReason = "P05 fixture" });
            if (targetAutoSubmit.Status is not (HttpStatusCode.OK or HttpStatusCode.Accepted)) throw new InvalidOperationException($"Full target auto-submit {(int)targetAutoSubmit.Status}: {targetAutoSubmit.Body}");
            report = await db.WorkAssignmentReports.Find(r => r.Id == report.Id).SingleAsync(ct);
            Check(report.Status == WorkAssignmentReportStatus.Approved && report.LifecycleRevision == preAutoRevision + 2
                && (await Instance()).State == "FROZEN" && (await SourceLock()).Owners.Count == 1,
                "real auto-approved mapped target freezes once while native submit and approval advance two lifecycle revisions");
            frozen = await Frozen();
            await Actor(fixture.Outsider);
            var targetRecall = await Send(HttpMethod.Post, $"work-assignment-review/reports/{report.Id}/recall-approved", new ReturnReportRequest {
                CommandId = run + "-target-recall", ExpectedPayloadRevision = report.PayloadRevision, ExpectedLifecycleRevision = report.LifecycleRevision,
                Comment = "P05 recall target" });
            if (targetRecall.Status is not (HttpStatusCode.OK or HttpStatusCode.Accepted)) throw new InvalidOperationException($"Full target recall {(int)targetRecall.Status}: {targetRecall.Body}");
            report = await db.WorkAssignmentReports.Find(r => r.Id == report.Id).SingleAsync(ct);
            Check(report.Status == WorkAssignmentReportStatus.Submitted && frozen == await Frozen() && (await SourceLock()).Owners.Count == 1,
                "real reviewer Recall keeps mapped target frozen and source owned");
            var targetApprove = await Send(HttpMethod.Post, $"work-assignment-review/reports/{report.Id}/approve", new ApproveReportRequest {
                CommandId = run + "-target-approve", ExpectedPayloadRevision = report.PayloadRevision, ExpectedLifecycleRevision = report.LifecycleRevision,
                Comment = "P05 approve target" });
            if (targetApprove.Status is not (HttpStatusCode.OK or HttpStatusCode.Accepted)) throw new InvalidOperationException($"Full target approve {(int)targetApprove.Status}: {targetApprove.Body}");
            report = await db.WorkAssignmentReports.Find(r => r.Id == report.Id).SingleAsync(ct);
            Check(report.Status == WorkAssignmentReportStatus.Approved && frozen == await Frozen() && (await SourceLock()).Owners.Count == 1,
                "real distinct reviewer Approve preserves existing frozen snapshot and source lock");
            targetRecall = await Send(HttpMethod.Post, $"work-assignment-review/reports/{report.Id}/recall-approved", new ReturnReportRequest {
                CommandId = run + "-target-recall-again", ExpectedPayloadRevision = report.PayloadRevision, ExpectedLifecycleRevision = report.LifecycleRevision,
                Comment = "P05 recall before return" });
            if (targetRecall.Status is not (HttpStatusCode.OK or HttpStatusCode.Accepted)) throw new InvalidOperationException($"Full target second recall {(int)targetRecall.Status}: {targetRecall.Body}");
            report = await db.WorkAssignmentReports.Find(r => r.Id == report.Id).SingleAsync(ct);
            var targetReturn = await Send(HttpMethod.Post, $"work-assignment-review/reports/{report.Id}/return", new ReturnReportRequest {
                CommandId = run + "-target-return", ExpectedPayloadRevision = report.PayloadRevision, ExpectedLifecycleRevision = report.LifecycleRevision,
                Comment = "P05 return target" });
            if (targetReturn.Status is not (HttpStatusCode.OK or HttpStatusCode.Accepted)) throw new InvalidOperationException($"Full target return {(int)targetReturn.Status}: {targetReturn.Body}");
            Check((await db.WorkAssignmentReports.Find(r => r.Id == report.Id).SingleAsync(ct)).Status == WorkAssignmentReportStatus.Draft
                && (await Instance()).State == "DRAFT" && (await SourceLock()).Owners.Count == 0 && frozen == await Frozen()
                && (await db.WorkAssignmentReports.Find(r => r.Id == source.Id).SingleAsync(ct)).Status == WorkAssignmentReportStatus.Approved,
                "real reviewer Return releases target owner without cascading child status or deleting frozen evidence");

            var required = await RuntimeFixture.Seed(db, run + "-required", ct, requiredTarget: true);
            await Actor(required.Actor);
            var requiredBefore = await db.WorkAssignmentReports.Find(r => r.Id == required.Report.Id).SingleAsync(ct);
            var blankDraft = await Send(HttpMethod.Put, $"work-assignment-reports/{required.Report.Id}/draft", new SaveWorkAssignmentReportDraftRequest {
                CommandId = run + "-required-blank-draft", ExpectedPayloadRevision = requiredBefore.PayloadRevision, FieldValuesJson = "{}", Values1D = [] });
            if (blankDraft.Status is not (HttpStatusCode.OK or HttpStatusCode.Accepted)) throw new InvalidOperationException($"Full blank draft {(int)blankDraft.Status}: {blankDraft.Body}");
            Check((await db.WorkAssignmentReports.Find(r => r.Id == required.Report.Id).SingleAsync(ct)).Status == WorkAssignmentReportStatus.Draft,
                "ordinary unmapped report permits saving incomplete required field as Draft");
            requiredBefore = await db.WorkAssignmentReports.Find(r => r.Id == required.Report.Id).SingleAsync(ct);
            var requiredSubmit = await Send(HttpMethod.Post, $"work-assignment-reports/{required.Report.Id}/submit", new SubmitWorkAssignmentReportRequest {
                CommandId = run + "-required-blank-submit", ExpectedPayloadRevision = requiredBefore.PayloadRevision, ExpectedLifecycleRevision = requiredBefore.LifecycleRevision });
            if (requiredSubmit.Status != HttpStatusCode.BadRequest
                || requiredSubmit.Body.GetProperty("errorCode").GetString() != "WORK_ASSIGNMENT_REPORT_VALUES_INVALID"
                || requiredSubmit.Body.GetProperty("details").GetProperty("fieldId").GetString() != "total"
                || requiredSubmit.Body.GetProperty("details").GetProperty("reason").GetString() != "DYNAMIC_FORM_RUNTIME_FIELD_VALUE_REQUIRED")
                throw new InvalidOperationException($"Full required submit {(int)requiredSubmit.Status}: {requiredSubmit.Body}");
            Check((await db.WorkAssignmentReports.Find(r => r.Id == required.Report.Id).SingleAsync(ct)).LifecycleRevision == requiredBefore.LifecycleRevision,
                "real Submit rejects missing required native field before lifecycle change");
            var zeroDraft = await Send(HttpMethod.Put, $"work-assignment-reports/{required.Report.Id}/draft", new SaveWorkAssignmentReportDraftRequest {
                CommandId = run + "-required-zero-draft", ExpectedPayloadRevision = requiredBefore.PayloadRevision, FieldValuesJson = "{\"total\":0}", Values1D = [] });
            if (zeroDraft.Status != HttpStatusCode.OK) throw new InvalidOperationException($"Full zero draft {(int)zeroDraft.Status}: {zeroDraft.Body}");
            requiredBefore = await db.WorkAssignmentReports.Find(r => r.Id == required.Report.Id).SingleAsync(ct);
            var zeroSubmit = await Send(HttpMethod.Post, $"work-assignment-reports/{required.Report.Id}/submit", new SubmitWorkAssignmentReportRequest {
                CommandId = run + "-required-zero-submit", ExpectedPayloadRevision = requiredBefore.PayloadRevision, ExpectedLifecycleRevision = requiredBefore.LifecycleRevision });
            Check(zeroSubmit.Status is HttpStatusCode.OK or HttpStatusCode.Accepted
                && (await db.WorkAssignmentReports.Find(r => r.Id == required.Report.Id).SingleAsync(ct)).Status == WorkAssignmentReportStatus.Submitted,
                "ordinary unmapped report submits a genuine zero without aggregate confirmation");

            await Actor(fixture.Actor);
            confirmation = await Send(HttpMethod.Post, $"aggregate-v2/reports/{fixture.Report.Id}/submission-preview", new AggregateInstanceReadCommandDto(context));
            var oldJti = new JwtSecurityTokenHandler().ReadJwtToken(http.DefaultRequestHeaders.Authorization!.Parameter).Id;
            var refresh = await Send(HttpMethod.Post, "auth/refresh", new { });
            if (refresh.Status != HttpStatusCode.OK) throw new InvalidOperationException($"Full refresh failed: {(int)refresh.Status}");
            var nextAccessToken = refresh.Body.GetProperty("accessToken").GetString()!;
            var newJti = new JwtSecurityTokenHandler().ReadJwtToken(nextAccessToken).Id;
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", nextAccessToken);
            Check(!string.IsNullOrEmpty(oldJti) && !string.IsNullOrEmpty(newJti) && oldJti != newJti,
                "production login and refresh issue distinct access-token identities for confirmation fencing");
            report = await db.WorkAssignmentReports.Find(r => r.Id == fixture.Report.Id).SingleAsync(ct);
            var staleSubmit = await Send(HttpMethod.Post, $"work-assignment-reports/{report.Id}/submit", new SubmitWorkAssignmentReportRequest {
                CommandId = run + "-stale-after-refresh", ExpectedPayloadRevision = report.PayloadRevision, ExpectedLifecycleRevision = report.LifecycleRevision,
                AggregateConfirmationToken = confirmation.Body.GetProperty("token").GetString(), LateReason = "P05 fixture" });
            Check((int)staleSubmit.Status >= 400 && staleSubmit.Body.GetRawText().Contains("AGG_CONFIRMATION_")
                && (await db.WorkAssignmentReports.Find(r => r.Id == report.Id).SingleAsync(ct)).LifecycleRevision == report.LifecycleRevision,
                "token refresh rejects old submission confirmation without lifecycle write");
            var freshAfterRefresh = await Send(HttpMethod.Post, $"aggregate-v2/reports/{report.Id}/submission-preview", new AggregateInstanceReadCommandDto(context));
            Check(freshAfterRefresh.Status == HttpStatusCode.OK,
                "refreshed production login remains authorized to obtain a new aggregate confirmation");
            await FullHostSlotChecks.Run(db, fixture, instanceId, run, Actor, Send, ct);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            var log = await stdout + await stderr;
            await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory, run + "-full-host.log"), log);
            await db.Users.UpdateManyAsync(u => loginUsers.Contains(u.Id), Builders<tdtd_be.Models.AppUser>.Update.Set(u => u.PasswordHash, "!P05_RETIRED!"));
            await db.RefreshTokens.UpdateManyAsync(t => loginUsers.Contains(t.UserId) && t.RevokedAt == null,
                Builders<tdtd_be.Models.RefreshTokenDoc>.Update.Set(t => t.RevokedAt, DateTime.UtcNow));
        }
    }
}
