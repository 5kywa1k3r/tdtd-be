using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;

namespace tdtd_be.IntegrationTests;

/// <summary>
/// Secret-safe, content-addressed bridge between the isolated P11-03 and
/// P11-04 databases. The complete logical payload is AEAD encrypted; the
/// sidecar contains only metadata, counts, hashes, and finalized identities.
/// </summary>
internal static class P11ContinuationSnapshot
{
    internal const string KeyEnvironmentVariable = "P11_CONTINUATION_KEY_B64";
    internal const string SnapshotOption = "--continuation-snapshot";
    internal const string ManifestOption = "--continuation-manifest";

    private const string PayloadSchema = "P11_CONTINUITY_SNAPSHOT_PAYLOAD_V2";
    private const string ManifestSchema = "P11_CONTINUITY_SNAPSHOT_MANIFEST_V2";
    private const string SourcePromptId = "P11-03";
    private const string IdentityTupleSchema = "P11_CORE_IDENTITY_TUPLE_V2";
    private const int NonceLength = 12;
    private const int TagLength = 16;
    private const int KeyLength = 32;
    private const int MaximumEnvelopeBytes = 512 * 1024 * 1024;
    private const int MaximumPayloadBytes = 768 * 1024 * 1024;
    private static readonly byte[] Magic = "P11CSN01"u8.ToArray();
    private static readonly byte[] AssociatedData = "P11_CONTINUITY_AES_256_GCM_V1"u8.ToArray();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    private static readonly string[] RequiredIdentityKeys =
    [
        "formVersionId",
        "flowFamilyId",
        "flowVersionId",
        "flowPayloadHash",
        "flowContributionPolicy",
        "flowContributionPolicyHash",
        "workId",
        "flowInstanceId",
        "stepInstanceId",
        "assignmentId",
        "reportId",
        "launchCommandId"
    ];

    internal static SnapshotPaths? OptionalExportPaths(
        string[] args,
        HarnessPaths paths)
    {
        var pair = ParsePathPair(args, required: false);
        if (pair is null) return null;
        return ValidatePathPair(pair, paths.RunRoot, requireExisting: false);
    }

    internal static SnapshotPaths RequiredRestorePaths(
        string[] args,
        HarnessPaths paths)
    {
        var pair = ParsePathPair(args, required: true)!;
        var chainRoot = Directory.GetParent(paths.IntegrationRoot)?.FullName
            ?? throw new InvalidOperationException("P11 chain artifact root cannot be resolved.");
        var predecessorRoot = Path.Combine(chainRoot, SourcePromptId);
        return ValidatePathPair(pair, predecessorRoot, requireExisting: true);
    }

    internal static async Task<SnapshotResult> CreateAsync(
        IMongoDatabase database,
        SnapshotPaths paths,
        string chainId,
        string runKey,
        JsonNode browserIds,
        string oracleIdentitySha256,
        CancellationToken ct)
    {
        if (File.Exists(paths.SnapshotPath) || File.Exists(paths.ManifestPath))
            throw new InvalidOperationException("P11 continuation output paths must be append-only and absent.");

        var key = ReadKeyFromEnvironment();
        try
        {
            var ids = NormalizeIds(browserIds);
            var identitySha256 = CoreIdentitySha256(ids);
            if (!FixedTimeEqualsHex(identitySha256, oracleIdentitySha256))
                throw new InvalidOperationException("P11 continuation identity disagrees with the P11-03 direct-Mongo oracle.");

            await VerifyExactFinalizedIdentitiesAsync(database, ids, ct);
            var identitySetSha256 = IdentitySetSha256(ids);
            var capturedAtUtc = DateTime.UtcNow;
            var collections = await CaptureCollectionsAsync(database, ct);
            var payload = new SnapshotPayload(
                PayloadSchema,
                SourcePromptId,
                chainId,
                Sha256Utf8(runKey),
                capturedAtUtc,
                IdentityTupleSchema,
                RequiredIdentityKeys,
                ids,
                identitySha256,
                identitySetSha256,
                collections);
            var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
            if (payloadBytes.Length > MaximumPayloadBytes)
                throw new InvalidOperationException("P11 continuation logical payload exceeds the bounded size limit.");
            var payloadSha256 = Sha256(payloadBytes);
            var compressed = Compress(payloadBytes);
            var nonce = RandomNumberGenerator.GetBytes(NonceLength);
            var ciphertext = new byte[compressed.Length];
            var tag = new byte[TagLength];
            using (var aes = new AesGcm(key, TagLength))
                aes.Encrypt(nonce, compressed, ciphertext, tag, AssociatedData);

            var envelope = new byte[Magic.Length + NonceLength + TagLength + ciphertext.Length];
            Magic.CopyTo(envelope, 0);
            nonce.CopyTo(envelope, Magic.Length);
            tag.CopyTo(envelope, Magic.Length + NonceLength);
            ciphertext.CopyTo(envelope, Magic.Length + NonceLength + TagLength);
            if (envelope.Length > MaximumEnvelopeBytes)
                throw new InvalidOperationException("P11 continuation encrypted envelope exceeds the bounded size limit.");

            var snapshotSha256 = Sha256(envelope);
            var collectionMetadata = collections
                .Select(item => new SnapshotCollectionMetadata(
                    item.Name,
                    item.Documents.Count,
                    item.DocumentsSha256))
                .ToArray();
            var documentCount = collectionMetadata.Sum(item => item.DocumentCount);
            var manifest = new SnapshotManifest(
                ManifestSchema,
                SourcePromptId,
                chainId,
                Sha256Utf8(runKey),
                capturedAtUtc,
                new SnapshotCipherMetadata(
                    "AES-256-GCM",
                    "ENVIRONMENT_ONLY",
                    256,
                    NonceLength,
                    TagLength),
                snapshotSha256,
                payloadSha256,
                payloadBytes.LongLength,
                envelope.LongLength,
                collectionMetadata.Length,
                documentCount,
                collectionMetadata,
                IdentityTupleSchema,
                RequiredIdentityKeys,
                ids,
                identitySha256,
                identitySetSha256);

            Directory.CreateDirectory(Path.GetDirectoryName(paths.SnapshotPath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(paths.ManifestPath)!);
            await using (var snapshotStream = new FileStream(
                             paths.SnapshotPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             128 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await snapshotStream.WriteAsync(envelope, ct);
                await snapshotStream.FlushAsync(ct);
            }
            await WriteManifestCreateNewAsync(paths.ManifestPath, manifest, ct);

            return new SnapshotResult(
                paths.SnapshotPath,
                paths.ManifestPath,
                snapshotSha256,
                payloadSha256,
                identitySha256,
                identitySetSha256,
                collectionMetadata.Length,
                documentCount,
                ids,
                capturedAtUtc,
                Sha256Utf8(runKey));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    internal static async Task<SnapshotResult> RestoreAsync(
        IMongoDatabase database,
        SnapshotPaths paths,
        string expectedChainId,
        CancellationToken ct)
    {
        var key = ReadKeyFromEnvironment();
        try
        {
            var manifest = await ReadManifestAsync(paths.ManifestPath, ct);
            ValidateManifestEnvelope(manifest, expectedChainId);
            var envelope = await File.ReadAllBytesAsync(paths.SnapshotPath, ct);
            if (envelope.Length > MaximumEnvelopeBytes)
                throw new InvalidOperationException("P11 continuation encrypted envelope exceeds the bounded size limit.");
            if (!FixedTimeEqualsHex(Sha256(envelope), manifest.SnapshotSha256))
                throw new CryptographicException("P11 continuation snapshot digest verification failed.");
            if (envelope.LongLength != manifest.SnapshotBytes)
                throw new InvalidOperationException("P11 continuation snapshot byte count disagrees with its manifest.");

            var compressed = Decrypt(envelope, key);
            var payloadBytes = DecompressBounded(compressed);
            if (!FixedTimeEqualsHex(Sha256(payloadBytes), manifest.PayloadSha256))
                throw new CryptographicException("P11 continuation payload digest verification failed.");
            if (payloadBytes.LongLength != manifest.PayloadBytes)
                throw new InvalidOperationException("P11 continuation payload byte count disagrees with its manifest.");
            var payload = JsonSerializer.Deserialize<SnapshotPayload>(payloadBytes, JsonOptions)
                ?? throw new InvalidOperationException("P11 continuation payload is empty.");
            ValidatePayloadAgainstManifest(payload, manifest, expectedChainId);

            var existingNames = (await database.ListCollectionNamesAsync(cancellationToken: ct))
                .ToList(ct)
                .Where(name => !name.StartsWith("system.", StringComparison.Ordinal))
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();
            if (existingNames.Length != 0)
                throw new InvalidOperationException("P11 continuation restore target database is not empty.");

            foreach (var collection in payload.Collections.OrderBy(item => item.Name, StringComparer.Ordinal))
            {
                ValidateCollectionArchive(collection);
                if (collection.Documents.Count == 0)
                {
                    await database.CreateCollectionAsync(collection.Name, cancellationToken: ct);
                    continue;
                }

                var documents = collection.Documents
                    .Select(DecodeDocument)
                    .ToArray();
                var target = database.GetCollection<BsonDocument>(collection.Name);
                foreach (var batch in documents.Chunk(500))
                    await target.InsertManyAsync(batch, cancellationToken: ct);
            }

            var restoredCollections = await CaptureCollectionsAsync(database, ct);
            VerifyRestoredCollectionSet(payload.Collections, restoredCollections);
            await VerifyExactFinalizedIdentitiesAsync(database, payload.Ids, ct);

            return new SnapshotResult(
                paths.SnapshotPath,
                paths.ManifestPath,
                manifest.SnapshotSha256,
                manifest.PayloadSha256,
                manifest.IdentitySha256,
                manifest.IdentitySetSha256,
                manifest.CollectionCount,
                manifest.DocumentCount,
                new Dictionary<string, string>(manifest.Ids, StringComparer.Ordinal),
                manifest.CapturedAtUtc,
                manifest.SourceRunKeySha256);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static SnapshotPaths? ParsePathPair(string[] args, bool required)
    {
        var snapshot = SingleOption(args, SnapshotOption);
        var manifest = SingleOption(args, ManifestOption);
        if (snapshot is null && manifest is null)
        {
            if (required)
                throw new ArgumentException(
                    $"Both {SnapshotOption} and {ManifestOption} are required for exact P11-03 continuation.",
                    nameof(args));
            return null;
        }
        if (snapshot is null || manifest is null)
            throw new ArgumentException(
                $"{SnapshotOption} and {ManifestOption} must be supplied together.",
                nameof(args));
        return new SnapshotPaths(snapshot, manifest);
    }

    private static string? SingleOption(string[] args, string name)
    {
        var values = new List<string>();
        for (var index = 0; index < args.Length; index++)
        {
            if (!string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase)) continue;
            if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"Option {name} requires a value.", nameof(args));
            values.Add(args[index + 1]);
        }
        return values.Count switch
        {
            0 => null,
            1 => values[0],
            _ => throw new ArgumentException($"Option {name} must appear exactly once.", nameof(args))
        };
    }

    private static SnapshotPaths ValidatePathPair(
        SnapshotPaths pair,
        string allowedRoot,
        bool requireExisting)
    {
        var snapshot = RequireGuardedPath(pair.SnapshotPath, allowedRoot, "snapshot");
        var manifest = RequireGuardedPath(pair.ManifestPath, allowedRoot, "manifest");
        if (string.Equals(snapshot, manifest, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("P11 continuation snapshot and manifest paths must be distinct.");
        if (requireExisting)
        {
            if (!File.Exists(snapshot)) throw new FileNotFoundException("P11 continuation snapshot is missing.", snapshot);
            if (!File.Exists(manifest)) throw new FileNotFoundException("P11 continuation manifest is missing.", manifest);
        }
        return new SnapshotPaths(snapshot, manifest);
    }

    private static string RequireGuardedPath(string value, string allowedRoot, string kind)
    {
        if (!Path.IsPathFullyQualified(value))
            throw new ArgumentException($"P11 continuation {kind} path must be absolute.");
        var root = Path.GetFullPath(allowedRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(value);
        if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(target, root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"P11 continuation {kind} path is outside its guarded artifact root.");
        }
        return target;
    }

    private static byte[] ReadKeyFromEnvironment()
    {
        var encoded = Environment.GetEnvironmentVariable(KeyEnvironmentVariable);
        Environment.SetEnvironmentVariable(KeyEnvironmentVariable, null);
        if (string.IsNullOrWhiteSpace(encoded))
            throw new InvalidOperationException("P11 continuation AEAD key environment variable is missing.");
        byte[] key;
        try { key = Convert.FromBase64String(encoded); }
        catch (FormatException error)
        {
            throw new InvalidOperationException(
                "P11 continuation AEAD key must be canonical base64.", error);
        }
        if (key.Length == KeyLength &&
            string.Equals(Convert.ToBase64String(key), encoded,
                StringComparison.Ordinal))
        {
            return key;
        }
        CryptographicOperations.ZeroMemory(key);
        throw new InvalidOperationException(
            "P11 continuation AEAD key must be canonical base64 for exactly 32 bytes.");
    }

    private static async Task<List<SnapshotCollection>> CaptureCollectionsAsync(
        IMongoDatabase database,
        CancellationToken ct)
    {
        var collectionInfos = (await database.ListCollectionsAsync(cancellationToken: ct))
            .ToList(ct)
            .Where(item => item.TryGetValue("name", out var name) &&
                           name.IsString &&
                           !name.AsString.StartsWith("system.", StringComparison.Ordinal))
            .OrderBy(item => item["name"].AsString, StringComparer.Ordinal)
            .ToArray();
        var result = new List<SnapshotCollection>(collectionInfos.Length);
        foreach (var info in collectionInfos)
        {
            var name = info["name"].AsString;
            var type = info.GetValue("type", "collection").AsString;
            if (!string.Equals(type, "collection", StringComparison.Ordinal))
                throw new InvalidOperationException($"P11 continuation does not materialize non-collection Mongo owner '{name}'.");
            ValidateCollectionName(name);
            var documents = await database.GetCollection<BsonDocument>(name)
                .Find(FilterDefinition<BsonDocument>.Empty)
                .ToListAsync(ct);
            var encoded = documents
                .Select(document => document.ToBson())
                .OrderBy(DocumentSortKey, StringComparer.Ordinal)
                .ThenBy(bytes => Sha256(bytes), StringComparer.Ordinal)
                .Select(Convert.ToBase64String)
                .ToArray();
            result.Add(new SnapshotCollection(
                name,
                encoded,
                CollectionSha256(name, encoded)));
        }
        return result;
    }

    private static string DocumentSortKey(byte[] raw)
    {
        var document = BsonSerializer.Deserialize<BsonDocument>(raw);
        if (!document.TryGetValue("_id", out var id)) return string.Empty;
        return id.ToJson(new MongoDB.Bson.IO.JsonWriterSettings
        {
            OutputMode = MongoDB.Bson.IO.JsonOutputMode.CanonicalExtendedJson
        });
    }

    private static string CollectionSha256(string name, IReadOnlyList<string> documents)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendLengthPrefixed(hash, Encoding.UTF8.GetBytes(name));
        foreach (var document in documents)
            AppendLengthPrefixed(hash, Convert.FromBase64String(document));
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void AppendLengthPrefixed(IncrementalHash hash, byte[] value)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(length, value.Length);
        hash.AppendData(length);
        hash.AppendData(value);
    }

    private static BsonDocument DecodeDocument(string encoded)
    {
        byte[] raw;
        try { raw = Convert.FromBase64String(encoded); }
        catch (FormatException error)
        {
            throw new InvalidOperationException("P11 continuation contains an invalid BSON record encoding.", error);
        }
        return BsonSerializer.Deserialize<BsonDocument>(raw);
    }

    private static void ValidateCollectionArchive(SnapshotCollection collection)
    {
        ValidateCollectionName(collection.Name);
        if (!FixedTimeEqualsHex(
                CollectionSha256(collection.Name, collection.Documents),
                collection.DocumentsSha256))
        {
            throw new CryptographicException($"P11 continuation collection digest failed for '{collection.Name}'.");
        }
    }

    private static void ValidateCollectionName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) ||
            name.StartsWith("system.", StringComparison.Ordinal) ||
            name.Contains('\0') ||
            name.Contains('$'))
        {
            throw new InvalidOperationException("P11 continuation contains an unsafe Mongo collection name.");
        }
    }

    private static void VerifyRestoredCollectionSet(
        IReadOnlyList<SnapshotCollection> expected,
        IReadOnlyList<SnapshotCollection> actual)
    {
        var expectedRows = expected.OrderBy(item => item.Name, StringComparer.Ordinal).ToArray();
        var actualRows = actual.OrderBy(item => item.Name, StringComparer.Ordinal).ToArray();
        if (expectedRows.Length != actualRows.Length)
            throw new InvalidOperationException("P11 continuation restored collection count mismatch.");
        for (var index = 0; index < expectedRows.Length; index++)
        {
            var left = expectedRows[index];
            var right = actualRows[index];
            if (!string.Equals(left.Name, right.Name, StringComparison.Ordinal) ||
                left.Documents.Count != right.Documents.Count ||
                !FixedTimeEqualsHex(left.DocumentsSha256, right.DocumentsSha256))
            {
                throw new InvalidOperationException($"P11 continuation restored collection mismatch at '{left.Name}'.");
            }
        }
    }

    private static Dictionary<string, string> NormalizeIds(JsonNode idsNode)
    {
        if (idsNode is not JsonObject idsObject)
            throw new InvalidOperationException("P11 continuation requires a JSON object of finalized P11-03 identities.");
        var ids = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in idsObject.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            if (property.Value is null || property.Value.GetValueKind() != JsonValueKind.String)
                throw new InvalidOperationException($"P11 continuation identity '{property.Key}' must be a string.");
            var value = property.Value.GetValue<string>().Trim();
            if (string.IsNullOrWhiteSpace(value) || value.Length > 512)
                throw new InvalidOperationException($"P11 continuation identity '{property.Key}' is blank or oversized.");
            ids[property.Key] = value;
        }
        foreach (var key in RequiredIdentityKeys)
            if (!ids.ContainsKey(key))
                throw new InvalidOperationException($"P11 continuation finalized identity '{key}' is missing.");
        return ids;
    }

    private static string CoreIdentitySha256(IReadOnlyDictionary<string, string> ids)
        => Sha256Utf8(string.Join("\n", RequiredIdentityKeys.Select(key => ids[key])));

    private static string IdentitySetSha256(IReadOnlyDictionary<string, string> ids)
        => Sha256Utf8(string.Join("\n", ids.OrderBy(item => item.Key, StringComparer.Ordinal)
            .Select(item => $"{item.Key}\0{item.Value}")));

    private static async Task VerifyExactFinalizedIdentitiesAsync(
        IMongoDatabase database,
        IReadOnlyDictionary<string, string> ids,
        CancellationToken ct)
    {
        var normalized = new Dictionary<string, string>(ids, StringComparer.Ordinal);
        foreach (var key in RequiredIdentityKeys)
            if (!normalized.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
                throw new InvalidOperationException($"P11 continuation restored identity '{key}' is missing.");
        ObjectId Id(string key) => ObjectId.TryParse(normalized[key], out var value)
            ? value
            : throw new InvalidOperationException($"P11 continuation identity '{key}' is not an ObjectId.");
        if (!string.Equals(normalized["flowContributionPolicy"], "INCLUDE", StringComparison.Ordinal))
            throw new InvalidOperationException("P11 continuation Flow contribution policy must be INCLUDE.");
        RequireSha256(normalized["flowPayloadHash"], "Flow payload");
        RequireSha256(normalized["flowContributionPolicyHash"], "Flow contribution-policy");

        var f = Builders<BsonDocument>.Filter;
        async Task RequireOne(string collection, FilterDefinition<BsonDocument> filter, string label)
        {
            var count = await database.GetCollection<BsonDocument>(collection)
                .CountDocumentsAsync(filter, cancellationToken: ct);
            if (count != 1)
                throw new InvalidOperationException($"P11 continuation exact {label} identity count is {count}, expected 1.");
        }

        await RequireOne("dynamic_form_templates", f.Eq("_id", Id("formVersionId")), "Form version");
        await RequireOne(
            "dynamic_flow_templates",
            f.Eq("_id", Id("flowFamilyId")) &
            f.Eq("rootDynamicFormTemplateId", Id("formVersionId")) &
            f.Eq("currentVersionId", Id("flowVersionId")) &
            f.Eq("hasLockedVersion", true),
            "Flow family/Form/current-version binding");
        await RequireOne(
            "dynamic_flow_template_versions",
            f.Eq("_id", Id("flowVersionId")) &
            f.Eq("templateId", Id("flowFamilyId")) &
            f.Eq("rootDynamicFormTemplateId", Id("formVersionId")) &
            f.Eq("payloadHash", normalized["flowPayloadHash"]) &
            f.Eq("contributionPolicy", normalized["flowContributionPolicy"]) &
            f.Eq("contributionPolicyHash", normalized["flowContributionPolicyHash"]),
            "Flow version/family/Form binding");
        await RequireOne("works", f.Eq("_id", Id("workId")), "Work");
        await RequireOne(
            "dynamic_flow_instances",
            f.Eq("_id", Id("flowInstanceId")) &
            f.Eq("workId", Id("workId")) &
            f.Eq("flowTemplateId", Id("flowFamilyId")) &
            f.Eq("flowTemplateVersionId", Id("flowVersionId")) &
            f.Eq("launchCommandId", normalized["launchCommandId"]),
            "Flow instance");
        await RequireOne(
            "dynamic_flow_step_instances",
            f.Eq("_id", Id("stepInstanceId")) &
            f.Eq("flowInstanceId", Id("flowInstanceId")) &
            f.Eq("formVersionId", Id("formVersionId")),
            "step instance");
        await RequireOne(
            "work_assignments",
            f.Eq("_id", Id("assignmentId")) &
            f.Eq("workId", Id("workId")) &
            f.Eq("dynamicFormTemplateId", Id("formVersionId")) &
            f.Eq("flowInstanceId", Id("flowInstanceId")),
            "assignment");
        await RequireOne(
            "work_assignment_report",
            f.Eq("_id", Id("reportId")) &
            f.Eq("workAssignmentId", Id("assignmentId")) &
            f.Eq("workId", Id("workId")) &
            f.Eq("dynamicFormTemplateId", Id("formVersionId")),
            "report");

        var instance = await database.GetCollection<BsonDocument>("dynamic_flow_instances")
            .Find(f.Eq("_id", Id("flowInstanceId"))).SingleAsync(ct);
        var report = await database.GetCollection<BsonDocument>("work_assignment_report")
            .Find(f.Eq("_id", Id("reportId"))).SingleAsync(ct);
        var instanceState = SafeBson(instance, "state");
        var reportState = SafeBson(report, "status");
        if (!TerminalValue(instanceState, "FINALIZED", "Finalized") ||
            !TerminalValue(reportState, "Approved", "2"))
        {
            throw new InvalidOperationException(
                $"P11 continuation restored identities are not finalized (instance={instanceState}; report={reportState}).");
        }
    }

    private static string SafeBson(BsonDocument document, string name)
        => document.TryGetValue(name, out var value)
            ? value.IsString ? value.AsString : value.ToString() ?? "NULL"
            : "MISSING";

    private static bool TerminalValue(string actual, params string[] expected)
        => expected.Any(value => string.Equals(actual, value, StringComparison.OrdinalIgnoreCase));

    private static void ValidateManifestEnvelope(SnapshotManifest manifest, string expectedChainId)
    {
        if (!string.Equals(manifest.SchemaVersion, ManifestSchema, StringComparison.Ordinal) ||
            !string.Equals(manifest.SourcePromptId, SourcePromptId, StringComparison.Ordinal) ||
            !string.Equals(manifest.ChainId, expectedChainId, StringComparison.Ordinal) ||
            !string.Equals(manifest.IdentityTupleSchema, IdentityTupleSchema, StringComparison.Ordinal) ||
            !ExactIdentityFields(manifest.IdentityFields) ||
            !string.Equals(manifest.Cipher.Algorithm, "AES-256-GCM", StringComparison.Ordinal) ||
            !string.Equals(manifest.Cipher.KeySource, "ENVIRONMENT_ONLY", StringComparison.Ordinal) ||
            manifest.Cipher.KeyBits != 256 ||
            manifest.Cipher.NonceBytes != NonceLength ||
            manifest.Cipher.TagBytes != TagLength ||
            manifest.CollectionCount < 1 ||
            manifest.DocumentCount < 1)
        {
            throw new InvalidOperationException("P11 continuation manifest contract is invalid.");
        }
        RequireSha256(manifest.SnapshotSha256, "snapshot");
        RequireSha256(manifest.PayloadSha256, "payload");
        RequireSha256(manifest.IdentitySha256, "identity");
        RequireSha256(manifest.IdentitySetSha256, "identity-set");
        RequireSha256(manifest.SourceRunKeySha256, "source-run-key");
    }

    private static void ValidatePayloadAgainstManifest(
        SnapshotPayload payload,
        SnapshotManifest manifest,
        string expectedChainId)
    {
        if (!string.Equals(payload.SchemaVersion, PayloadSchema, StringComparison.Ordinal) ||
            !string.Equals(payload.SourcePromptId, SourcePromptId, StringComparison.Ordinal) ||
            !string.Equals(payload.ChainId, expectedChainId, StringComparison.Ordinal) ||
            !string.Equals(payload.IdentityTupleSchema, IdentityTupleSchema, StringComparison.Ordinal) ||
            !ExactIdentityFields(payload.IdentityFields) ||
            !string.Equals(payload.SourceRunKeySha256, manifest.SourceRunKeySha256, StringComparison.Ordinal) ||
            payload.CapturedAtUtc != manifest.CapturedAtUtc ||
            payload.Collections.Count != manifest.CollectionCount ||
            payload.Collections.Sum(item => (long)item.Documents.Count) != manifest.DocumentCount)
        {
            throw new InvalidOperationException("P11 continuation payload metadata disagrees with its manifest.");
        }
        var normalizedIds = NormalizeIds(JsonSerializer.SerializeToNode(payload.Ids, JsonOptions)!);
        if (!DictionaryEqual(normalizedIds, manifest.Ids) ||
            !FixedTimeEqualsHex(CoreIdentitySha256(normalizedIds), manifest.IdentitySha256) ||
            !FixedTimeEqualsHex(IdentitySetSha256(normalizedIds), manifest.IdentitySetSha256) ||
            !FixedTimeEqualsHex(payload.IdentitySha256, manifest.IdentitySha256) ||
            !FixedTimeEqualsHex(payload.IdentitySetSha256, manifest.IdentitySetSha256))
        {
            throw new InvalidOperationException("P11 continuation finalized identities disagree with the manifest.");
        }
        var manifestCollections = manifest.Collections.ToDictionary(item => item.Name, StringComparer.Ordinal);
        if (manifestCollections.Count != payload.Collections.Count)
            throw new InvalidOperationException("P11 continuation collection manifest contains duplicates.");
        foreach (var collection in payload.Collections)
        {
            ValidateCollectionArchive(collection);
            if (!manifestCollections.TryGetValue(collection.Name, out var metadata) ||
                metadata.DocumentCount != collection.Documents.Count ||
                !FixedTimeEqualsHex(metadata.DocumentsSha256, collection.DocumentsSha256))
            {
                throw new InvalidOperationException($"P11 continuation collection metadata mismatch at '{collection.Name}'.");
            }
        }
    }

    private static bool ExactIdentityFields(IReadOnlyList<string>? fields)
        => fields is not null && fields.Count == RequiredIdentityKeys.Length &&
           fields.Select((field, index) => string.Equals(field, RequiredIdentityKeys[index], StringComparison.Ordinal))
               .All(equal => equal);

    private static bool DictionaryEqual(
        IReadOnlyDictionary<string, string> left,
        IReadOnlyDictionary<string, string> right)
        => left.Count == right.Count && left.All(item =>
            right.TryGetValue(item.Key, out var value) &&
            string.Equals(item.Value, value, StringComparison.Ordinal));

    private static byte[] Compress(byte[] payload)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
            gzip.Write(payload);
        return output.ToArray();
    }

    private static byte[] Decrypt(byte[] envelope, byte[] key)
    {
        var minimum = Magic.Length + NonceLength + TagLength;
        if (envelope.Length < minimum || !envelope.AsSpan(0, Magic.Length).SequenceEqual(Magic))
            throw new InvalidOperationException("P11 continuation snapshot envelope header is invalid.");
        var nonce = envelope.AsSpan(Magic.Length, NonceLength);
        var tag = envelope.AsSpan(Magic.Length + NonceLength, TagLength);
        var ciphertext = envelope.AsSpan(minimum);
        var plaintext = new byte[ciphertext.Length];
        using var aes = new AesGcm(key, TagLength);
        aes.Decrypt(nonce, ciphertext, tag, plaintext, AssociatedData);
        return plaintext;
    }

    private static byte[] DecompressBounded(byte[] compressed)
    {
        using var input = new MemoryStream(compressed, writable: false);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        var buffer = new byte[128 * 1024];
        while (true)
        {
            var read = gzip.Read(buffer, 0, buffer.Length);
            if (read == 0) break;
            if (output.Length + read > MaximumPayloadBytes)
                throw new InvalidOperationException("P11 continuation decompressed payload exceeds the bounded size limit.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private static async Task WriteManifestCreateNewAsync(
        string path,
        SnapshotManifest manifest,
        CancellationToken ct)
    {
        await using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await JsonSerializer.SerializeAsync(stream, manifest, new JsonSerializerOptions(JsonOptions)
        {
            WriteIndented = true
        }, ct);
        await stream.FlushAsync(ct);
    }

    private static async Task<SnapshotManifest> ReadManifestAsync(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<SnapshotManifest>(stream, JsonOptions, ct)
               ?? throw new InvalidOperationException("P11 continuation manifest is empty.");
    }

    private static void RequireSha256(string value, string label)
    {
        if (value.Length != 64 || value.Any(ch => !Uri.IsHexDigit(ch)))
            throw new InvalidOperationException($"P11 continuation {label} SHA-256 is invalid.");
    }

    private static bool FixedTimeEqualsHex(string left, string right)
    {
        try
        {
            var leftBytes = Convert.FromHexString(left);
            var rightBytes = Convert.FromHexString(right);
            return leftBytes.Length == rightBytes.Length &&
                   CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string Sha256(byte[] value)
        => Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();

    private static string Sha256Utf8(string value)
        => Sha256(Encoding.UTF8.GetBytes(value));

    internal sealed record SnapshotPaths(string SnapshotPath, string ManifestPath);

    internal sealed record SnapshotResult(
        string SnapshotPath,
        string ManifestPath,
        string SnapshotSha256,
        string PayloadSha256,
        string IdentitySha256,
        string IdentitySetSha256,
        int CollectionCount,
        long DocumentCount,
        IReadOnlyDictionary<string, string> Ids,
        DateTime CapturedAtUtc,
        string SourceRunKeySha256);

    private sealed record SnapshotPayload(
        string SchemaVersion,
        string SourcePromptId,
        string ChainId,
        string SourceRunKeySha256,
        DateTime CapturedAtUtc,
        string IdentityTupleSchema,
        IReadOnlyList<string> IdentityFields,
        Dictionary<string, string> Ids,
        string IdentitySha256,
        string IdentitySetSha256,
        List<SnapshotCollection> Collections);

    private sealed record SnapshotCollection(
        string Name,
        IReadOnlyList<string> Documents,
        string DocumentsSha256);

    private sealed record SnapshotManifest(
        string SchemaVersion,
        string SourcePromptId,
        string ChainId,
        string SourceRunKeySha256,
        DateTime CapturedAtUtc,
        SnapshotCipherMetadata Cipher,
        string SnapshotSha256,
        string PayloadSha256,
        long PayloadBytes,
        long SnapshotBytes,
        int CollectionCount,
        long DocumentCount,
        IReadOnlyList<SnapshotCollectionMetadata> Collections,
        string IdentityTupleSchema,
        IReadOnlyList<string> IdentityFields,
        Dictionary<string, string> Ids,
        string IdentitySha256,
        string IdentitySetSha256);

    private sealed record SnapshotCipherMetadata(
        string Algorithm,
        string KeySource,
        int KeyBits,
        int NonceBytes,
        int TagBytes);

    private sealed record SnapshotCollectionMetadata(
        string Name,
        long DocumentCount,
        string DocumentsSha256);
}
