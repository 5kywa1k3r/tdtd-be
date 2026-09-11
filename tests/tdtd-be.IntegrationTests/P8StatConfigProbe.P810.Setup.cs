using System.Text.Json.Nodes;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Models;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P8StatConfigProbe
{
    private WorkAssignment _p810Assignment = default!;
    private P8BasicFixture _p810BasicFixture = default!;
    private P8AdvancedFixture _p810AdvancedFixture = default!;
    private P8DiffFixture _p810DiffFixture = default!;
    private P8FlowDraftFixture _p810BlockedProfileDraft = default!;
    private P8BasicConfigIdentity? _p810BasicDraft;
    private P8BasicConfigIdentity? _p810BasicStale;
    private P8AdvancedConfigIdentity? _p810AdvancedDraft;
    private P8AdvancedConfigIdentity? _p810AdvancedStale;
    private P8DiffConfigIdentity? _p810DiffDraft;
    private P8DiffConfigIdentity? _p810DiffStale;
    private readonly List<P810ActorMatrixRow> _p810ActorMatrix = [];
    private readonly List<P810RouteBinding> _p810RouteBindings = [];

    private static readonly (string Role, string ActorKey, string Relationship, bool CanManage)[]
        P810ActorDefinitions =
        [
            ("OWNER", "p810_owner", "assignment.CreatedByUserId", true),
            ("ISSUER", "p810_issuer", "assignment.IssuedByUnitId+leaderWatcher", false),
            ("REPORTER", "p810_reporter", "assignment.Assignees", false),
            ("REVIEWER", "p810_reviewer", "assignment.LeaderWatcherUserIds", false),
            ("COORDINATOR", "p810_coordinator", "assignment.LeaderWatcherUserIds", false),
            ("SYSTEM_ADMIN", "system_admin", "assignment.LeaderWatcherUserIds+system administrator", true),
            ("OUTSIDER", "p810_outsider", "different unit; no assignment relationship", false)
        ];

    private async Task BindP810ActorRelationshipsAsync(CancellationToken ct)
    {
        var owner = Actor("p810_owner");
        var issuer = Actor("p810_issuer");
        var reporter = Actor("p810_reporter");
        var reviewer = Actor("p810_reviewer");
        var coordinator = Actor("p810_coordinator");
        var systemAdmin = Actor("system_admin");
        var collection = _database.GetCollection<WorkAssignment>("work_assignments");
        var update = Builders<WorkAssignment>.Update
            .Set(item => item.CreatedByUserId, owner.Id)
            .Set(item => item.UpdatedByUserId, owner.Id)
            .Set(item => item.IssuedByUnitId, issuer.UnitId)
            .Set(item => item.Assignees,
            [
                P810UserRef(reporter, "REPORTER")
            ])
            .Set(item => item.LeaderWatcherUserIds,
            [
                issuer.Id,
                reviewer.Id,
                coordinator.Id,
                systemAdmin.Id
            ])
            .Set(item => item.LeaderWatchers,
            [
                P810UserRef(issuer, "ISSUER"),
                P810UserRef(reviewer, "REVIEWER"),
                P810UserRef(coordinator, "COORDINATOR"),
                P810UserRef(systemAdmin, "SYSTEM_ADMIN")
            ]);
        var result = await collection.UpdateOneAsync(
            item => item.Id == _p809AssignmentId && !item.IsDeleted,
            update,
            cancellationToken: ct);
        HarnessAssert.Equal(1L, result.MatchedCount,
            "P8-10 canonical assignment relationship update missed its owner");
        HarnessAssert.Equal(1L, result.ModifiedCount,
            "P8-10 canonical assignment relationships did not change exactly once");
        _p810Assignment = await collection.Find(
                item => item.Id == _p809AssignmentId && !item.IsDeleted)
            .SingleAsync(ct);
        HarnessAssert.Equal(owner.Id, _p810Assignment.CreatedByUserId,
            "P8-10 OWNER relationship was not persisted");
        HarnessAssert.Equal(issuer.UnitId, _p810Assignment.IssuedByUnitId,
            "P8-10 ISSUER unit relationship was not persisted");
        HarnessAssert.True(
            _p810Assignment.Assignees.Any(item => item.UserId == reporter.Id),
            "P8-10 REPORTER relationship was not persisted");
        foreach (var watcher in new[] { issuer, reviewer, coordinator, systemAdmin })
        {
            HarnessAssert.True(
                _p810Assignment.LeaderWatcherUserIds.Contains(watcher.Id, StringComparer.Ordinal),
                $"P8-10 watcher relationship missing {watcher.Key}");
        }
    }

    private static UserRef P810UserRef(P8Actor actor, string role)
        => new()
        {
            UserId = actor.Id,
            Username = actor.Username,
            FullName = $"P8-10 {role}",
            UnitId = actor.UnitId,
            UnitSymbol = "P810-A",
            UnitShortName = "P810-A",
            UnitName = "P8 Unit A"
        };

    private Task BuildP810FixturesAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var flowInstanceId = ObjectId.GenerateNewId().ToString();
        var flowBranchId = ObjectId.GenerateNewId().ToString();
        const string flowStepId = "p810-config-surface";
        _p810BasicFixture = new P8BasicFixture(
            "p810-ui",
            _p809Form.OwnerId,
            _p810Assignment,
            flowInstanceId,
            flowStepId,
            flowBranchId);
        _p810AdvancedFixture = new P8AdvancedFixture(
            "p810-ui",
            _p809Form.OwnerId,
            "main",
            _p810Assignment,
            "p810_owner");
        _p810DiffFixture = new P8DiffFixture(
            "p810-ui",
            _p809Form.OwnerId,
            _p810Assignment,
            flowInstanceId,
            flowStepId,
            flowBranchId);
        return Task.CompletedTask;
    }

    private async Task SeedP810BlockedProfileFixtureAsync(CancellationToken ct)
    {
        var payload = BuildP807MappedFlowPayload();
        payload["statisticProfile"] = new JsonObject
        {
            ["diffMode"] = "COUNT"
        };
        _p810BlockedProfileDraft = await CreateFlowDraftAsync(
            Actor("system_admin"),
            "p810-blocked-profile",
            "p810-profile-setup",
            payload,
            ct);
    }

    private async Task<IReadOnlyList<P810ExternalOracleInput>>
        LoadP810ExternalOracleInputsAsync(string[] args, CancellationToken ct)
    {
        var specs = new[]
        {
            (Kind: "COMPONENT", Option: "--p8-ui-component-oracle"),
            (Kind: "BROWSER", Option: "--p8-ui-browser-oracle")
        };
        var rows = new List<P810ExternalOracleInput>();
        foreach (var spec in specs)
        {
            var index = Array.FindIndex(args, value =>
                string.Equals(value, spec.Option, StringComparison.Ordinal));
            if (index < 0)
            {
                rows.Add(new P810ExternalOracleInput(
                    spec.Kind, false, null, null, 0, false,
                    "not supplied; no UI oracle inferred or fabricated"));
                continue;
            }
            if (index + 1 >= args.Length || string.IsNullOrWhiteSpace(args[index + 1]))
            {
                throw new InvalidOperationException(
                    $"{spec.Option} requires an explicit evidence path.");
            }
            var path = Path.GetFullPath(args[index + 1]);
            if (!File.Exists(path))
                throw new FileNotFoundException($"P8-10 {spec.Kind} oracle does not exist.", path);
            var bytes = await File.ReadAllBytesAsync(path, ct);
            var parsedJson = false;
            try
            {
                parsedJson = JsonNode.Parse(bytes) is not null;
            }
            catch (System.Text.Json.JsonException)
            {
                // Browser reporters may be text; the byte hash remains the source oracle.
            }
            rows.Add(new P810ExternalOracleInput(
                spec.Kind,
                true,
                path,
                Sha256(bytes),
                bytes.LongLength,
                parsedJson,
                "caller-supplied bytes; runner records hash only and does not synthesize verdicts"));
        }
        await EvidenceJson.WriteAsync(
            Path.Combine(_paths.RunRoot, "p8-ui-external-oracles.json"),
            new
            {
                chainId = ChainId,
                promptId = _promptId,
                noFabricatedUiOracle = true,
                inputs = rows
            },
            ct);
        return rows;
    }

    private async Task WriteP810FixtureAndActorEvidenceAsync(
        IReadOnlyList<P810ExternalOracleInput> externalOracles,
        CancellationToken ct)
    {
        await EvidenceJson.WriteAsync(
            Path.Combine(_paths.RunRoot, "p8-ui-actor-fixtures.json"),
            new
            {
                chainId = ChainId,
                promptId = _promptId,
                realKestrel = true,
                directMongoRelationshipOracle = true,
                assignment = new
                {
                    _p810Assignment.Id,
                    _p810Assignment.WorkId,
                    dynamicFormTemplateId = _p809Form.OwnerId,
                    _p810Assignment.CreatedByUserId,
                    _p810Assignment.IssuedByUnitId,
                    assigneeUserIds = _p810Assignment.Assignees.Select(item => item.UserId),
                    _p810Assignment.LeaderWatcherUserIds
                },
                actors = P810ActorDefinitions.Select(definition =>
                {
                    var actor = Actor(definition.ActorKey);
                    return new
                    {
                        definition.Role,
                        definition.ActorKey,
                        actor.Id,
                        actor.Username,
                        actor.UnitId,
                        actor.AccountKind,
                        actor.Roles,
                        definition.Relationship,
                        definition.CanManage,
                        authenticated = !string.IsNullOrWhiteSpace(actor.Token)
                    };
                }),
                externalOracles
            },
            ct);
    }
}

internal sealed record P810ExternalOracleInput(
    string Kind,
    bool Supplied,
    string? Path,
    string? Sha256,
    long ByteLength,
    bool ParsedJson,
    string TrustBoundary);

internal sealed record P810ActorMatrixRow(
    string Role,
    string ActorKey,
    string Relationship,
    int KnownOwnerStatus,
    bool CanReadConfig,
    bool CanManageDraft,
    bool CanLockVersion,
    int? UnknownOwnerStatus,
    bool NonLeakEquivalent);

internal sealed record P810RouteBinding(
    string CaseId,
    string Surface,
    string Method,
    string Route,
    string Oracle,
    string Availability);
