using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using tdtd_be.Data;
using tdtd_be.Data.Infrastructure;
using tdtd_be.Models;
using tdtd_be.Common.Errors;
using tdtd_be.Services.WorkAssignments.Internal;

internal static class LiveRead
{
    public static async Task RunAsync(string repoRoot)
    {
        var config = new ConfigurationBuilder().SetBasePath(Path.Combine(repoRoot, "tdtd-be"))
            .AddJsonFile("appsettings.json").AddJsonFile("appsettings.Development.json", optional: true).Build();
        var options = config.GetSection("Mongo").Get<MongoOptions>() ?? throw new Exception("Missing local Mongo options");
        var db = new MongoDbContext(Options.Create(options));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = timeout.Token;
        var actor = await db.Users.Find(x => x.Username == "pv01" && !x.IsDeleted).FirstOrDefaultAsync(ct)
            ?? throw new Exception("PV01 not present in configured local DB");
        var actorUnit = await db.Units.Find(x => x.Id == actor.UnitId && !x.IsDeleted).FirstOrDefaultAsync(ct)
            ?? throw new Exception("PV01 unit not active");
        if (!WorkAssignmentTargetScopeValidator.ValidUnit(actorUnit)) throw new Exception("PV01 unit code invalid");
        var units = await db.Units.Find(x => !x.IsDeleted && x.Code != null && x.Code.StartsWith(actorUnit.Code!))
            .ToListAsync(ct);
        var ids = units.Select(x => x.Id).ToList();
        var accounts = await db.Users.Find(x => !x.IsDeleted && x.UnitId != null && ids.Contains(x.UnitId))
            .Project(x => new AppUser { Id = x.Id, UnitId = x.UnitId, Username = x.Username, AccountKind = x.AccountKind, PositionCode = x.PositionCode })
            .ToListAsync(ct);
        var map = units.ToDictionary(x => x.Id);
        var people = accounts.Where(WorkAssignmentTargetScopeValidator.IsPersonalRecipient).ToList();
        var ownPeople = people.Count(x => x.UnitId == actorUnit.Id && x.Id != actor.Id);
        var descendantPeople = people.Count(x => x.UnitId != actorUnit.Id);
        var managers = accounts.Where(WorkAssignmentTargetScopeValidator.IsUnitRecipient).ToList();
        var sourceChecks = 0;
        foreach (var person in people.Where(x => x.Id != actor.Id))
        {
            WorkAssignmentTargetScopeValidator.EnsureCanAssignTargets(actor, actorUnit, [person], map, false);
            sourceChecks++;
        }
        foreach (var manager in managers.Where(x => x.Id != actor.Id))
        {
            WorkAssignmentTargetScopeValidator.EnsureCanAssignTargets(actor, actorUnit, [manager], map, false);
            sourceChecks++;
            foreach (var person in people.Where(x => x.Id != actor.Id))
            {
                var overlap = WorkAssignmentTargetScopeValidator.FindOverlaps([manager, person], map).ContainsKey(person.Id);
                try
                {
                    WorkAssignmentTargetScopeValidator.EnsureCanAssignTargets(actor, actorUnit, [manager, person], map, false);
                    if (overlap) throw new Exception("Real-data overlap was not rejected");
                }
                catch (AppException ex) when (ex.Code == AppErrorCode.WORK_ASSIGNMENT_ASSIGNEE_SCOPE_INVALID && overlap) { }
                sourceChecks++;
            }
        }
        var coverage = managers.Select(manager => new {
            unitId = manager.UnitId,
            coveredPersonalCount = WorkAssignmentTargetScopeValidator.FindOverlaps(people.Append(manager), map).Count
        }).ToList();
        var works = await db.Works.Find(x => !x.IsDeleted && x.CreatedByUserId == actor.Id)
            .Project(x => new { x.Id, x.Name, x.CompletedAtUtc }).Limit(30).ToListAsync(ct);
        var evidence = new {
            date = DateTimeOffset.UtcNow, mode = "READ_ONLY_SOURCE_POLICY_WITH_CONFIGURED_LOCAL_DB",
            database = options.Database, actor = actor.Username, actorAccountKind = actor.AccountKind,
            actorUnitId = actorUnit.Id, actorUnitCode = actorUnit.Code, units = units.Count, accounts = accounts.Count,
            ownPeople, descendantPeople, unitRepresentatives = managers.Count, coverage, candidateWorkCount = works.Count,
            sourceChecks,
            note = "Read-only Mongo queries plus in-process source policy; no HTTP authentication, business writes or live BE deployment proof."
        };
        var output = Path.Combine(repoRoot, "outputs/recipient-scope-20261006/live-db-read.json");
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }), ct);
        Console.WriteLine($"READ ONLY PV01: {units.Count} units, {accounts.Count} accounts, {ownPeople} other personnel in own unit, {descendantPeople} personnel in descendants, {managers.Count} representatives. PASS {sourceChecks} in-process recipient checks on existing DB rows. No business writes or HTTP authentication.");
    }
}
