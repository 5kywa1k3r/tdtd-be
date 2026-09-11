using System.Security.Cryptography;
using MongoDB.Driver;
using tdtd_be.Common.Auth;
using tdtd_be.Common.Errors;
using tdtd_be.DTOs.Auth;
using tdtd_be.DTOs.StatisticsRun;
using tdtd_be.Models.Statistics;

namespace tdtd_be.Services.StatisticsRun;

public sealed partial class StatRunExportService
{
    public async Task<StatRunExportResponse> GetAsync(
        string exportId,
        string workId,
        string scopeType,
        string scopeId,
        string capabilityId,
        MeResponse actor,
        CancellationToken ct = default)
    {
        var access = await NormalizeAndAuthorizeAccessAsync(
            exportId, workId, scopeType, scopeId, capabilityId, actor, ct);
        var artifact = await FindAuthorizedArtifactAsync(access, actor, ct);
        return Map(artifact, false);
    }

    public async Task<StatRunExportDownload> DownloadAsync(
        string exportId,
        string workId,
        string scopeType,
        string scopeId,
        string capabilityId,
        MeResponse actor,
        CancellationToken ct = default)
    {
        var access = await NormalizeAndAuthorizeAccessAsync(
            exportId, workId, scopeType, scopeId, capabilityId, actor, ct);
        var artifact = await FindAuthorizedArtifactAsync(access, actor, ct);
        if (artifact.ExpiresAtUtc <= DateTime.UtcNow ||
            artifact.Status == StatRunExportStatuses.Expired)
        {
            throw new AppException(
                AppErrorCode.STAT_RUN_EXPORT_EXPIRED,
                new { reason = "EXPORT_EXPIRED", exportId = artifact.Id });
        }
        var content = await ReadArtifactGuardedAsync(
            artifact.StorageKey,
            artifact.ContentHash,
            artifact.ByteCount,
            ct);
        var collection = ExportCollection(artifact.ResultKind);
        var now = DateTime.UtcNow;
        await collection.UpdateOneAsync(
            item => item.Id == artifact.Id && !item.IsDeleted,
            Builders<StatRunExportArtifact>.Update
                .Inc(item => item.DownloadCount, 1)
                .Set(item => item.LastDownloadedAtUtc, now)
                .Set(item => item.UpdatedAtUtc, now)
                .Set(item => item.UpdatedByUserId, actor.Id),
            cancellationToken: ct);
        return new StatRunExportDownload
        {
            Content = content,
            ContentType = artifact.ContentType,
            FileName = artifact.FileName,
            ContentHash = artifact.ContentHash
        };
    }

    public async Task<StatRunExportCleanupResponse> CleanupExpiredAsync(
        StatRunExportCleanupRequest request,
        MeResponse actor,
        CancellationToken ct = default)
    {
        if (!RoleGuard.IsSystemAdmin(actor))
            throw AppExceptionFactory.Forbidden(AppErrorCode.AUTH_SYSTEM_ADMIN_REQUIRED);
        request ??= new StatRunExportCleanupRequest();
        if (request.Limit is < 1 or > 1000)
            throw Invalid("EXPORT_CLEANUP_LIMIT_INVALID");
        var cutoff = request.ExpiredBeforeUtc?.ToUniversalTime() ?? DateTime.UtcNow;
        var selected = new List<(IMongoCollection<StatRunExportArtifact> Collection, StatRunExportArtifact Artifact)>();
        foreach (var collection in ExportCollections())
        {
            var remaining = request.Limit - selected.Count;
            if (remaining <= 0)
                break;
            var items = await collection.Find(item =>
                    !item.IsDeleted && item.ExpiresAtUtc <= cutoff)
                .SortBy(item => item.ExpiresAtUtc)
                .ThenBy(item => item.Id)
                .Limit(remaining)
                .ToListAsync(ct);
            selected.AddRange(items.Select(item => (collection, item)));
        }
        selected = selected
            .OrderBy(item => item.Artifact.ExpiresAtUtc)
            .ThenBy(item => item.Artifact.Id, StringComparer.Ordinal)
            .Take(request.Limit)
            .ToList();
        var deleted = 0;
        if (!request.DryRun)
        {
            foreach (var item in selected)
            {
                DeleteArtifactGuarded(item.Artifact.StorageKey);
                var now = DateTime.UtcNow;
                var update = await item.Collection.UpdateOneAsync(
                    artifact => artifact.Id == item.Artifact.Id &&
                                !artifact.IsDeleted &&
                                artifact.ExpiresAtUtc <= cutoff,
                    Builders<StatRunExportArtifact>.Update
                        .Set(artifact => artifact.Status, StatRunExportStatuses.Expired)
                        .Set(artifact => artifact.IsDeleted, true)
                        .Set(artifact => artifact.UpdatedAtUtc, now)
                        .Set(artifact => artifact.UpdatedByUserId, actor.Id),
                    cancellationToken: ct);
                if (update.ModifiedCount == 1)
                    deleted++;
            }
        }
        return new StatRunExportCleanupResponse
        {
            DryRun = request.DryRun,
            ExpiredBeforeUtc = cutoff,
            Selected = selected.Count,
            Deleted = deleted,
            ExportIds = selected.Select(item => item.Artifact.Id).ToList()
        };
    }

    private async Task<ExportAccess> NormalizeAndAuthorizeAccessAsync(
        string exportId,
        string workId,
        string scopeType,
        string scopeId,
        string capabilityId,
        MeResponse actor,
        CancellationToken ct)
    {
        var normalizedWorkId = Required(workId, "EXPORT_WORK_ID_REQUIRED");
        var normalizedScopeType = Required(scopeType, "EXPORT_SCOPE_TYPE_REQUIRED").ToUpperInvariant();
        var normalizedScopeId = Required(scopeId, "EXPORT_SCOPE_ID_REQUIRED");
        var normalizedCapability = Required(capabilityId, "EXPORT_CAPABILITY_REQUIRED").ToUpperInvariant();
        _activation.RequireCapability(
            normalizedCapability,
            StatRunExportContract.RouteForCapability(normalizedCapability));
        await AuthorizeScopeAsync(
            normalizedWorkId,
            normalizedScopeType,
            normalizedScopeId,
            actor,
            ct);
        return new ExportAccess(
            Required(exportId, "EXPORT_ID_REQUIRED"),
            normalizedWorkId,
            normalizedScopeType,
            normalizedScopeId,
            normalizedCapability);
    }

    private async Task<StatRunExportArtifact> FindAuthorizedArtifactAsync(
        ExportAccess access,
        MeResponse actor,
        CancellationToken ct)
    {
        var collection = access.CapabilityId == StatRunCapabilities.Diff
            ? ExportCollection(StatRunExportResultKinds.Diff)
            : ExportCollection(StatRunExportResultKinds.Basic);
        var artifact = await collection.Find(item =>
                item.Id == access.ExportId &&
                !item.IsDeleted)
            .FirstOrDefaultAsync(ct);
        if (artifact is null)
            throw NotFound("EXPORT_NOT_FOUND");
        if (!string.Equals(artifact.WorkId, access.WorkId, StringComparison.Ordinal) ||
            !string.Equals(artifact.ScopeType, access.ScopeType, StringComparison.Ordinal) ||
            !string.Equals(artifact.ScopeId, access.ScopeId, StringComparison.Ordinal) ||
            !string.Equals(artifact.CapabilityId, access.CapabilityId, StringComparison.Ordinal) ||
            (!RoleGuard.IsSystemAdmin(actor) &&
             !string.Equals(artifact.RequestedByUserId, actor.Id, StringComparison.Ordinal)))
        {
            throw AppExceptionFactory.Forbidden(
                AppErrorCode.STAT_RUN_FORBIDDEN,
                new { reason = "EXPORT_FORBIDDEN", writes = 0 });
        }
        return artifact;
    }

    private StatRunExportResponse ValidateReplayAndMap(
        StatRunExportArtifact artifact,
        NormalizedExportRequest request,
        string requestHash,
        MeResponse actor,
        bool replay)
    {
        if (!string.Equals(artifact.RequestedByUserId, actor.Id, StringComparison.Ordinal) ||
            !string.Equals(artifact.CommandId, request.CommandId, StringComparison.Ordinal) ||
            !string.Equals(artifact.RequestHash, requestHash, StringComparison.Ordinal))
        {
            throw new AppException(
                AppErrorCode.STAT_RUN_COMMAND_REPLAY_MISMATCH,
                new { reason = "EXPORT_COMMAND_CHANGED_REPLAY", writes = 0 });
        }
        return Map(artifact, replay);
    }

    private static StatRunExportResponse Map(StatRunExportArtifact artifact, bool replay)
        => new()
        {
            ExportId = artifact.Id,
            ReceiptId = artifact.ReceiptId,
            CommandId = artifact.CommandId,
            RequestHash = artifact.RequestHash,
            IsReplay = replay,
            Status = artifact.ExpiresAtUtc <= DateTime.UtcNow
                ? StatRunExportStatuses.Expired
                : artifact.Status,
            CapabilityId = artifact.CapabilityId,
            ResultKind = artifact.ResultKind,
            Format = artifact.Format,
            WorkId = artifact.WorkId,
            ScopeType = artifact.ScopeType,
            ScopeId = artifact.ScopeId,
            PeriodInstanceKey = artifact.PeriodInstanceKey,
            ResultId = artifact.ResultId,
            ResultHash = artifact.ResultHash,
            ConfigHash = artifact.ConfigHash,
            SourceHash = artifact.SourceHash,
            LifecycleRevision = artifact.LifecycleRevision,
            CatalogVersion = artifact.CatalogVersion,
            CatalogRawSha256 = artifact.CatalogRawSha256,
            CatalogSemanticSha256 = artifact.CatalogSemanticSha256,
            StageLockSha256 = artifact.StageLockSha256,
            CandidateChainId = artifact.CandidateChainId,
            CandidatePromptId = artifact.CandidatePromptId,
            CandidateStage = artifact.CandidateStage,
            SchemaVersion = artifact.SchemaVersion,
            SemanticHash = artifact.SemanticHash,
            FileName = artifact.FileName,
            ContentType = artifact.ContentType,
            ContentHash = artifact.ContentHash,
            ByteCount = artifact.ByteCount,
            RowCount = artifact.RowCount,
            ColumnCount = artifact.ColumnCount,
            CompletedAtUtc = artifact.CompletedAtUtc,
            ExpiresAtUtc = artifact.ExpiresAtUtc,
            DownloadUrl = $"/api/stat-runs/exports/{Uri.EscapeDataString(artifact.Id)}/download"
        };

    private async Task<bool> WriteArtifactAtomicallyAsync(
        string storageKey,
        byte[] content,
        string expectedHash,
        CancellationToken ct)
    {
        var target = ResolveStoragePath(storageKey);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (File.Exists(target))
        {
            var existing = await File.ReadAllBytesAsync(target, ct);
            if (Hash(existing) != expectedHash)
                throw Invalid("EXPORT_STORAGE_HASH_COLLISION");
            return false;
        }
        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, content, ct);
            var persisted = await File.ReadAllBytesAsync(temporary, ct);
            if (Hash(persisted) != expectedHash)
                throw Invalid("EXPORT_STORAGE_WRITE_HASH_MISMATCH");
            try
            {
                File.Move(temporary, target, overwrite: false);
                return true;
            }
            catch (IOException) when (File.Exists(target))
            {
                var winner = await File.ReadAllBytesAsync(target, ct);
                if (Hash(winner) != expectedHash)
                    throw Invalid("EXPORT_STORAGE_HASH_COLLISION");
                return false;
            }
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private async Task<byte[]> ReadArtifactGuardedAsync(
        string storageKey,
        string expectedHash,
        long expectedBytes,
        CancellationToken ct)
    {
        var path = ResolveStoragePath(storageKey);
        return await ReadArtifactFileGuardedAsync(
                path,
                expectedHash,
                expectedBytes,
                ct)
            .ConfigureAwait(false);
    }

    internal static async Task<byte[]> ReadArtifactFileGuardedAsync(
        string path,
        string expectedHash,
        long expectedBytes,
        CancellationToken ct)
    {
        try
        {
            if (expectedBytes < 0 || expectedBytes > StatRunExportContract.MaxBytes)
                throw Invalid("EXPORT_ARTIFACT_BOUNDS_INVALID");
            if (!File.Exists(path))
                throw NotFound("EXPORT_ARTIFACT_NOT_FOUND");

            var file = new FileInfo(path);
            if (file.Length != expectedBytes)
                throw Invalid("EXPORT_ARTIFACT_INTEGRITY_FAILED");

            var bytes = new byte[checked((int)expectedBytes)];
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 81_920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length != expectedBytes)
                throw Invalid("EXPORT_ARTIFACT_INTEGRITY_FAILED");

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var offset = 0;
            while (offset < bytes.Length)
            {
                var read = await stream.ReadAsync(bytes.AsMemory(offset), ct)
                    .ConfigureAwait(false);
                if (read == 0)
                    throw Invalid("EXPORT_ARTIFACT_INTEGRITY_FAILED");
                hash.AppendData(bytes.AsSpan(offset, read));
                offset += read;
            }

            var trailing = new byte[1];
            if (await stream.ReadAsync(trailing, ct).ConfigureAwait(false) != 0)
                throw Invalid("EXPORT_ARTIFACT_INTEGRITY_FAILED");
            var actualHash = Convert.ToHexString(hash.GetHashAndReset())
                .ToLowerInvariant();
            if (!string.Equals(actualHash, expectedHash, StringComparison.Ordinal))
                throw Invalid("EXPORT_ARTIFACT_INTEGRITY_FAILED");
            return bytes;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (AppException)
        {
            throw;
        }
        catch (IOException)
        {
            throw Invalid("EXPORT_ARTIFACT_READ_FAILED");
        }
        catch (UnauthorizedAccessException)
        {
            throw Invalid("EXPORT_ARTIFACT_READ_FAILED");
        }
    }

    private void DeleteArtifactGuarded(string storageKey)
    {
        var path = ResolveStoragePath(storageKey);
        if (File.Exists(path))
            File.Delete(path);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory) &&
            !Directory.EnumerateFileSystemEntries(directory).Any())
        {
            Directory.Delete(directory);
        }
    }

    private string ResolveStoragePath(string storageKey)
    {
        if (string.IsNullOrWhiteSpace(storageKey) ||
            Path.IsPathRooted(storageKey) ||
            storageKey.Contains("..", StringComparison.Ordinal))
        {
            throw Invalid("EXPORT_STORAGE_KEY_INVALID");
        }
        var relative = storageKey.Replace('/', Path.DirectorySeparatorChar);
        var root = Path.GetFullPath(_artifactRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(root, relative));
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw Invalid("EXPORT_STORAGE_PATH_ESCAPE");
        return path;
    }

    private sealed record ExportAccess(
        string ExportId,
        string WorkId,
        string ScopeType,
        string ScopeId,
        string CapabilityId);
}
