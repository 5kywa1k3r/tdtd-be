using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using tdtd_be.Controllers;

internal static class ProvenanceStartupChecks
{
    internal static async Task Run()
    {
        var directory=Path.GetFullPath("../outputs/p05-provenance-repair-20260929");Directory.CreateDirectory(directory);
        var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();var port=((IPEndPoint)listener.LocalEndpoint).Port;listener.Stop();
        var info=new ProcessStartInfo("dotnet") { WorkingDirectory=Directory.GetCurrentDirectory(),UseShellExecute=false,CreateNoWindow=true,
            WindowStyle=ProcessWindowStyle.Hidden,RedirectStandardOutput=true,RedirectStandardError=true };
        info.ArgumentList.Add(typeof(AggregateMappingPreviewController).Assembly.Location);
        foreach(var pair in new Dictionary<string,string>{
            ["ASPNETCORE_ENVIRONMENT"]="Development",["DOTNET_ENVIRONMENT"]="Development",["ASPNETCORE_URLS"]=$"http://127.0.0.1:{port}",
            ["Mongo__Database"]="tdtd",["Mongo__ConnectionString"]="mongodb://localhost:27017/?replicaSet=tdtd-rs",["Mongo__TestingSkipIndexInitialization"]="false",
            ["Hangfire__ServerEnabled"]="false",["Hangfire__RecurringRegistrationEnabled"]="false",["Hangfire__DashboardEnabled"]="false",
            ["Hangfire__Prefix"]="p05_provenance_"+DateTime.UtcNow.ToString("yyyyMMddHHmmss"),["Redis__Enabled"]="false",["Frontend__Enabled"]="false",
            ["Jwt__Issuer"]="p05-provenance",["Jwt__Audience"]="p05-provenance",["Jwt__Key"]=Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)),
            ["AggregateMapping__Enabled"]="false",["AggregateMapping__V2Enabled"]="true",["AggregateMapping__ConfirmationKeyBase64"]=Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))})info.Environment[pair.Key]=pair.Value;
        using var process=Process.Start(info)??throw new InvalidOperationException("Startup failed");
        var stdout=process.StandardOutput.ReadToEndAsync();var stderr=process.StandardError.ReadToEndAsync();
        try
        {
            using var timeout=new CancellationTokenSource(TimeSpan.FromMinutes(3));using var http=new HttpClient{Timeout=TimeSpan.FromSeconds(3)};
            while(!process.HasExited&&!timeout.IsCancellationRequested)
            {
                try
                {
                    using var response=await http.GetAsync($"http://127.0.0.1:{port}/health/ready",timeout.Token);
                    if(response.StatusCode==HttpStatusCode.OK)
                    {
                        var body=await response.Content.ReadAsStringAsync(timeout.Token);
                        File.WriteAllText(Path.Combine(directory,"startup-result.json"),System.Text.Json.JsonSerializer.Serialize(new{environment="Development",skipIndexInitialization=false,port,pid=process.Id,status=200,body}));
                        Console.WriteLine($"PASS normal Program startup: Development, skipIndexInitialization=false, health/ready=200 {body}; port={port}; schedulers=OFF");return;
                    }
                }
                catch(HttpRequestException){}catch(TaskCanceledException)when(!timeout.IsCancellationRequested){}
                await Task.Delay(250,timeout.Token);
            }
            throw new InvalidOperationException("Normal Program startup failed; see normal-startup.stdout/stderr.log");
        }
        finally
        {
            if(!process.HasExited){process.Kill(entireProcessTree:true);await process.WaitForExitAsync();}
            File.WriteAllText(Path.Combine(directory,"normal-startup.stdout.log"),await stdout);File.WriteAllText(Path.Combine(directory,"normal-startup.stderr.log"),await stderr);
        }
    }
}
