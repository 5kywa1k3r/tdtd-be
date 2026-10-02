using System.Reflection;
using System.Runtime.ExceptionServices;
using Minio;

// Test-only fault boundary. The real client handles all calls except one upload;
// no storage service/process or other user's artifacts are disrupted.
public class MinioUploadFault : DispatchProxy
{
    private IMinioClient _inner = null!;
    private int _armed, _failures;
    internal int Failures => Volatile.Read(ref _failures);
    internal void Arm() => Interlocked.Exchange(ref _armed, 1);
    internal static (IMinioClient Client, MinioUploadFault Fault) Wrap(IMinioClient client)
    {
        var proxy = Create<IMinioClient, MinioUploadFault>();
        var fault = (MinioUploadFault)proxy; fault._inner = client;
        return (proxy, fault);
    }
    protected override object? Invoke(MethodInfo? method, object?[]? arguments)
    {
        if (method?.Name == "PutObjectAsync" && Interlocked.Exchange(ref _armed, 0) == 1)
        { Interlocked.Increment(ref _failures); throw new IOException("P05_INJECTED_MINIO_UPLOAD_FAILURE"); }
        try { return method!.Invoke(_inner, arguments); }
        catch (TargetInvocationException error) when (error.InnerException != null)
        { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
    }
}
