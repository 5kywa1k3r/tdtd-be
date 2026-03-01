namespace tdtd_be.Uploads;

public sealed class UploadOptions
{
    public long MaxUploadBytes { get; set; } = 524_288_000;
    public int ChunkSizeBytes { get; set; } = 5 * 1024 * 1024;

    // token riêng cho upload-session (ngắn hạn)
    public int UploadTokenTtlSeconds { get; set; } = 600; // 10 phút

    // presign
    public int PresignTtlSecondsDefault { get; set; } = 60;
    public int PresignTtlSecondsMax { get; set; } = 300;

    // storage
    public string Bucket { get; set; } = "tdtd-attachments";
}