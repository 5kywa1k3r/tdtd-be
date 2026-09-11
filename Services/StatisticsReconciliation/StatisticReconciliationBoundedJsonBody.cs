using System.Text.Json;

namespace tdtd_be.Services.StatisticsReconciliation;

internal static class StatisticReconciliationBoundedJsonBody
{
    internal const int MaxCommandBytes = 8 * 1024;

    internal static async Task<JsonElement> ReadAsync(
        Stream body,
        CancellationToken ct,
        int maxBytes = MaxCommandBytes)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (!body.CanRead || maxBytes is < 1 or > 64 * 1024)
            throw new InvalidOperationException("RECHECK_BODY_INVALID");
        using var buffer = new MemoryStream(Math.Min(maxBytes, 4096));
        var chunk = new byte[Math.Min(4096, maxBytes + 1)];
        var total = 0;
        while (true)
        {
            var read = await body.ReadAsync(chunk.AsMemory(0,
                Math.Min(chunk.Length, maxBytes + 1 - total)), ct);
            if (read == 0)
                break;
            total += read;
            if (total > maxBytes)
                throw new InvalidOperationException(
                    "RECHECK_BODY_TOO_LARGE");
            await buffer.WriteAsync(chunk.AsMemory(0, read), ct);
            if (total == maxBytes)
            {
                var extra = new byte[1];
                if (await body.ReadAsync(extra, ct) != 0)
                    throw new InvalidOperationException(
                        "RECHECK_BODY_TOO_LARGE");
                break;
            }
        }
        if (total == 0)
            throw new InvalidOperationException("RECHECK_BODY_REQUIRED");
        buffer.Position = 0;
        try
        {
            using var document = await JsonDocument.ParseAsync(buffer,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 16
                }, ct);
            return document.RootElement.Clone();
        }
        catch (JsonException error)
        {
            throw new InvalidOperationException("RECHECK_BODY_INVALID", error);
        }
    }
}
