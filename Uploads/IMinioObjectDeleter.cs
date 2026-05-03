namespace tdtd_be.Uploads;

public interface IMinioObjectDeleter
{
    Task RemoveAsync(string bucket, string objectKey, CancellationToken ct);
}
