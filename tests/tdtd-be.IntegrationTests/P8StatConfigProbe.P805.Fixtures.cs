using System.Text.Json.Nodes;
using MongoDB.Bson;
using MongoDB.Driver;
using tdtd_be.Models;
using tdtd_be.Services.DynamicForms;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P8StatConfigProbe
{
    private const string AdvancedMainSectionId = "advanced-main";
    private const string AdvancedSecondSectionId = "advanced-second";
    private readonly Dictionary<string, P8AdvancedFixture> _advancedFixtures =
        new(StringComparer.Ordinal);

    private async Task SeedP805FixturesAsync(CancellationToken ct)
    {
        var form = BuildAdvancedDynamicFormFixture();
        await _database.GetCollection<DynamicFormTemplate>(DynamicFormsCollection)
            .InsertOneAsync(form, cancellationToken: ct);

        var keys = Enumerable.Range(1, 18)
            .Select(index => $"{index:000}")
            .Concat([
                "quota-a", "quota-b", "quota-c", "quota-d",
                "race-a", "race-b", "race-c", "isolation"
            ])
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var assignments = keys.Select((key, index) =>
                BuildAdvancedAssignmentFixture(
                    key,
                    index,
                    form,
                    key.StartsWith("quota-", StringComparison.Ordinal) ||
                    key.StartsWith("race-", StringComparison.Ordinal)
                        ? "unit_manager_a"
                        : "system_admin"))
            .ToArray();
        await _database.GetCollection<WorkAssignment>("work_assignments")
            .InsertManyAsync(
                assignments.Select(fixture => fixture.Assignment),
                cancellationToken: ct);
        foreach (var fixture in assignments)
            _advancedFixtures.Add(fixture.Key, fixture);

        await EvidenceJson.WriteAsync(
            Path.Combine(_paths.RunRoot, "p8-05-fixtures.json"),
            new
            {
                chainId = ChainId,
                promptId = _promptId,
                form = new
                {
                    form.Id,
                    form.Code,
                    sectionIds = new[]
                    {
                        AdvancedMainSectionId,
                        AdvancedSecondSectionId
                    },
                    mainFieldCount = 1001,
                    secondFieldCount = 1,
                    published = true,
                    form.PublishedSchemaHash
                },
                assignments = assignments.Select(fixture => new
                {
                    fixture.Key,
                    fixture.Assignment.Id,
                    fixture.Assignment.WorkId,
                    fixture.Assignment.DynamicFormTemplateId,
                    fixture.OwnerActorKey,
                    ownerUnitId = Actor(fixture.OwnerActorKey).UnitId
                }),
                activeUnitAUsers = 2,
                fixtureWritesOutsideCaseDeltas = true
            },
            ct);
    }

    private DynamicFormTemplate BuildAdvancedDynamicFormFixture()
    {
        var formId = ObjectId.GenerateNewId().ToString();
        var fields = new JsonArray();
        foreach (var index in Enumerable.Range(1, 1001))
        {
            var id = AdvancedFieldId(index);
            fields.Add(new JsonObject
            {
                ["id"] = id,
                ["sectionId"] = AdvancedMainSectionId,
                ["key"] = id,
                ["name"] = $"P8 Advanced number {index}",
                ["type"] = "number",
                ["required"] = false,
                ["order"] = index - 1
            });
        }
        fields.Add(new JsonObject
        {
            ["id"] = "adv_second_0001",
            ["sectionId"] = AdvancedSecondSectionId,
            ["key"] = "adv_second_0001",
            ["name"] = "P8 Advanced second-section number",
            ["type"] = "number",
            ["required"] = false,
            ["order"] = 0
        });
        var fixedAt = new DateTime(2026, 8, 2, 0, 0, 0, DateTimeKind.Utc);
        var form = new DynamicFormTemplate
        {
            Id = formId,
            Code = "P8_ADVANCED_SUMMARY_FORM",
            Name = "P8 Advanced Summary exact-boundary concepts",
            Description = "P8-05 isolated configuration-only fixture",
            TagCodes = [],
            CreatedByUsername = Actor("system_admin").Username,
            SchemaVersion = 1,
            VersionNo = 1,
            FamilyId = formId,
            LineageStatus = DynamicFormLineageStatuses.Root,
            Revision = 1,
            IsActive = true,
            IsPublished = true,
            PublishedAtUtc = fixedAt,
            PublishedByUserId = _adminId,
            SectionsJson = new JsonArray(
                new JsonObject
                {
                    ["id"] = AdvancedMainSectionId,
                    ["title"] = "P8 Advanced Main",
                    ["description"] = null,
                    ["tagCodes"] = new JsonArray(),
                    ["order"] = 0
                },
                new JsonObject
                {
                    ["id"] = AdvancedSecondSectionId,
                    ["title"] = "P8 Advanced Second",
                    ["description"] = null,
                    ["tagCodes"] = new JsonArray(),
                    ["order"] = 1
                }).ToJsonString(),
            FieldsJson = fields.ToJsonString(),
            BlocksJson = "[]",
            CreatedByUserId = _adminId,
            UpdatedByUserId = _adminId,
            CreatedAtUtc = fixedAt,
            UpdatedAtUtc = fixedAt,
            IsDeleted = false
        };
        var snapshot = DynamicFormPublishedSchemaSnapshotBuilder.Build(form);
        form.PublishedSchemaSnapshotJson = snapshot.Json;
        form.PublishedSchemaHash = snapshot.Sha256;
        return form;
    }

    private P8AdvancedFixture BuildAdvancedAssignmentFixture(
        string key,
        int index,
        DynamicFormTemplate form,
        string ownerActorKey)
    {
        var actor = Actor(ownerActorKey);
        var assignmentId = ObjectId.GenerateNewId().ToString();
        var fixedAt = new DateTime(2026, 8, 2, 0, 2, 0, DateTimeKind.Utc)
            .AddSeconds(index);
        var assignment = new WorkAssignment
        {
            Id = assignmentId,
            WorkId = ObjectId.GenerateNewId().ToString(),
            DynamicFormTemplateId = form.Id,
            DynamicFormTemplateCode = form.Code,
            DynamicFormTemplateName = form.Name,
            DynamicFormFamilyId = form.FamilyId,
            DynamicFormVersionNo = form.VersionNo,
            WorkType = "P8_ADVANCED_SUMMARY",
            AssignmentType = "ONCE",
            AggregationType = "NONE",
            Assignees =
            [
                new UserRef
                {
                    UserId = actor.Id,
                    Username = actor.Username,
                    FullName = $"P8 Advanced owner {key}",
                    UnitId = actor.UnitId,
                    UnitSymbol = ownerActorKey,
                    UnitShortName = ownerActorKey,
                    UnitName = ownerActorKey
                }
            ],
            LeaderWatcherUserIds = [actor.Id],
            IsActive = true,
            RootAssignmentId = assignmentId,
            Level = 0,
            Code = $"P8-ADV-{key}",
            Name = $"P8 Advanced Summary owner {key}",
            Path = $"/{assignmentId}/",
            CreatedByUserId = actor.Id,
            UpdatedByUserId = actor.Id,
            CreatedAtUtc = fixedAt,
            UpdatedAtUtc = fixedAt,
            IsDeleted = false
        };
        return new P8AdvancedFixture(
            key,
            form.Id,
            AdvancedMainSectionId,
            assignment,
            ownerActorKey);
    }

    private P8AdvancedFixture AdvancedFixture(string key)
        => _advancedFixtures.TryGetValue(key, out var fixture)
            ? fixture
            : throw new HarnessCaseNotRunnableException(
                $"P8 Advanced Summary fixture {key} was not seeded.");
}

internal sealed record P8AdvancedFixture(
    string Key,
    string DynamicFormTemplateId,
    string SectionId,
    WorkAssignment Assignment,
    string OwnerActorKey);
