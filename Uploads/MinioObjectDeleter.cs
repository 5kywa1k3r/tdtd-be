using Minio;
using Minio.DataModel.Args;

namespace tdtd_be.Uploads;

public sealed class MinioObjectDeleter : IMinioObjectDeleter
{
    private readonly IMinioClient _minio;

    public MinioObjectDeleter(IMinioClient minio)
    {
        _minio = minio;
    }

    public async Task RemoveAsync(string bucket, string objectKey, CancellationToken ct)
    {
        try
        {
            await _minio.RemoveObjectAsync(
                new RemoveObjectArgs()
                    .WithBucket(bucket)
                    .WithObject(objectKey),
                ct);
        }
        catch (Minio.Exceptions.ObjectNotFoundException)
        {
            // ignore
        }
    }
}