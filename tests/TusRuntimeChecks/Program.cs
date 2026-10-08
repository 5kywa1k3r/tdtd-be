using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using tdtd_be.Common.Auth;
using tdtd_be.Common.Errors;
using tdtd_be.Controllers;
using tdtd_be.DTOs.Auth;
using tdtd_be.Models;
using tdtd_be.Services.WorkAssignmentReports;
using Minio;
using Microsoft.Extensions.Options;
using tdtd_be.Uploads;
using tusdotnet.Interfaces;
using tusdotnet.Stores;

var tempRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "tdtd-tus-runtime-" + Guid.NewGuid().ToString("N")));
var tempParent = Path.GetFullPath(Path.GetTempPath());
if (!tempRoot.StartsWith(tempParent, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Invalid temp path");
Directory.CreateDirectory(tempRoot);

using var listener = new TcpListener(IPAddress.Loopback, 0);
listener.Start();
var port = ((IPEndPoint)listener.LocalEndpoint).Port;
listener.Stop();

var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
builder.Configuration["Jwt:Key"] = new string('x', 48);
builder.Services.Configure<UploadOptions>(options => options.MaxUploadBytes = 5 * 1024 * 1024);
builder.Services.AddSingleton<TusDiskStore>(_ => new TusDiskStore(tempRoot));
builder.Services.AddSingleton<ITusStore>(services => services.GetRequiredService<TusDiskStore>());
builder.Services.AddSingleton<ITusTerminationStore>(services => services.GetRequiredService<TusDiskStore>());
builder.Services.AddSingleton<UploadTokenService>();

await using var app = builder.Build();
app.MapPost("/probe/reports/{reportId}/evidence/{fieldId}", async (HttpContext context, string reportId, string fieldId) =>
{
    var form = await context.Request.ReadFormAsync();
    var file = form.Files.GetFile("file") ?? throw new Exception("Multipart field 'file' is missing");
    context.Items[MeAccessor.MeItemKey] = new MeResponse("test-user", "test", "Test", [], "test-unit", null, null, null, [], null, false);
    var reports = DispatchProxy.Create<IWorkAssignmentReportService, EvidenceReportStub>();
    var minio = DispatchProxy.Create<IMinioClient, EvidenceStorageStop>();
    var controller = new ReportEvidenceController(null!, reports,
        new MeAccessor(new HttpContextAccessor { HttpContext = context }), minio, builder.Configuration);
    try
    {
        await controller.Upload(reportId, fieldId, file, context.RequestAborted);
        throw new Exception("The isolated probe unexpectedly wrote an evidence file");
    }
    catch (EvidenceStorageReached)
    {
        return Results.Ok(new { receivedBytes = file.Length, storageReached = true });
    }
    catch (AppException exception)
    {
        return Results.BadRequest(new { code = exception.Code.ToString(), details = exception.Details });
    }
});
app.MapTusUploads();
try
{
    await app.StartAsync();
    using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
    var token = app.Services.GetRequiredService<UploadTokenService>()
        .Issue("test-user", "five-kb.txt", "text/plain", 5120, "UPLOAD", "test-source", 300);

    using var create = new HttpRequestMessage(HttpMethod.Post, "/api/uploads") { Content = new ByteArrayContent([]) };
    create.Headers.Host = "192.168.1.2:5080";
    create.Headers.Add("Tus-Resumable", "1.0.0");
    create.Headers.Add("Upload-Length", "5120");
    create.Headers.Add("Upload-Token", token);
    using var created = await client.SendAsync(create);
    if (created.StatusCode != HttpStatusCode.Created) throw new Exception($"CREATE returned {(int)created.StatusCode}: {await created.Content.ReadAsStringAsync()}");
    var location = created.Headers.Location?.ToString() ?? throw new Exception("CREATE omitted Location");
    if (!location.StartsWith("/api/uploads/", StringComparison.Ordinal) || location.Contains("192.168.", StringComparison.Ordinal))
        throw new Exception("CREATE leaked an absolute/internal Location: " + location);

    using var patch = new HttpRequestMessage(new HttpMethod("PATCH"), location) { Content = new ByteArrayContent(new byte[2560]) };
    patch.Headers.Add("Tus-Resumable", "1.0.0");
    patch.Headers.Add("Upload-Offset", "0");
    patch.Headers.Add("Upload-Token", token);
    patch.Content.Headers.ContentType = new MediaTypeHeaderValue("application/offset+octet-stream");
    using var patched = await client.SendAsync(patch);
    if (patched.StatusCode != HttpStatusCode.NoContent || patched.Headers.GetValues("Upload-Offset").Single() != "2560")
        throw new Exception($"PATCH returned {(int)patched.StatusCode}: {await patched.Content.ReadAsStringAsync()}");

    using var head = new HttpRequestMessage(HttpMethod.Head, location);
    head.Headers.Add("Tus-Resumable", "1.0.0");
    head.Headers.Add("Upload-Token", token);
    using var checkedUpload = await client.SendAsync(head);
    if (checkedUpload.StatusCode != HttpStatusCode.OK || checkedUpload.Headers.GetValues("Upload-Offset").Single() != "2560"
        || checkedUpload.Headers.GetValues("Upload-Length").Single() != "5120")
        throw new Exception($"HEAD returned {(int)checkedUpload.StatusCode}");

    using var unauthorized = new HttpRequestMessage(HttpMethod.Head, location);
    unauthorized.Headers.Add("Tus-Resumable", "1.0.0");
    using var denied = await client.SendAsync(unauthorized);
    if (denied.IsSuccessStatusCode) throw new Exception("HEAD accepted request without Upload-Token");
    Console.WriteLine("PASS TUS 5 KB CREATE, relative Location, 2.5 KB PATCH, HEAD offset, token gate");

    using var fiveKb = new MultipartFormDataContent();
    fiveKb.Add(new ByteArrayContent(new byte[5120]), "file", "five-kb.txt");
    using var accepted = await client.PostAsync("/probe/reports/report-1/evidence/field-1", fiveKb);
    using var acceptedBody = JsonDocument.Parse(await accepted.Content.ReadAsStringAsync());
    if (accepted.StatusCode != HttpStatusCode.OK || acceptedBody.RootElement.GetProperty("receivedBytes").GetInt64() != 5120
        || !acceptedBody.RootElement.GetProperty("storageReached").GetBoolean())
        throw new Exception("The actual evidence controller rejected a 5 KB multipart upload before storage");

    using var oversized = new MultipartFormDataContent();
    oversized.Add(new ByteArrayContent(new byte[5 * 1024 * 1024 + 1]), "file", "large.txt");
    using var rejected = await client.PostAsync("/probe/reports/report-1/evidence/field-1", oversized);
    using var rejectedBody = JsonDocument.Parse(await rejected.Content.ReadAsStringAsync());
    if (rejected.StatusCode != HttpStatusCode.BadRequest || rejectedBody.RootElement.GetProperty("code").GetString() != "UPLOAD_FILE_TOO_LARGE")
        throw new Exception("The actual evidence controller did not reject a file over 5 MiB");
    Console.WriteLine("PASS evidence multipart 5 KB reaches storage boundary; >5 MiB returns UPLOAD_FILE_TOO_LARGE");
}
finally
{
    await app.StopAsync();
    if (Directory.Exists(tempRoot) && tempRoot.StartsWith(tempParent, StringComparison.OrdinalIgnoreCase))
        Directory.Delete(tempRoot, recursive: true);
}

public class EvidenceReportStub : DispatchProxy
{
    protected override object? Invoke(MethodInfo? method, object?[]? args)
        => method?.Name == nameof(IWorkAssignmentReportService.AuthorizeEvidenceAsync)
            ? Task.FromResult(new WorkAssignmentReport())
            : throw new InvalidOperationException("Unexpected report service call: " + method?.Name);
}

public sealed class EvidenceStorageReached : Exception { }

public class EvidenceStorageStop : DispatchProxy
{
    protected override object? Invoke(MethodInfo? method, object?[]? args)
        => method?.Name == nameof(IMinioClient.PutObjectAsync)
            ? throw new EvidenceStorageReached()
            : throw new InvalidOperationException("Unexpected storage call: " + method?.Name);
}
