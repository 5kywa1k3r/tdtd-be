using MongoDB.Driver;
using tdtd_be.Models;

namespace tdtd_be.IntegrationTests;

internal sealed partial class P8StatConfigProbe
{
    private static readonly string[] P811P810OnlyUsernames =
    [
        "p810_owner",
        "p810_issuer",
        "p810_reporter",
        "p810_reviewer",
        "p810_coordinator",
        "p810_outsider"
    ];

    private IReadOnlyList<P811PhaseUserSnapshot> _p811UsersBeforeP805Hide =
        Array.Empty<P811PhaseUserSnapshot>();
    private IReadOnlyList<P811PhaseUserSnapshot> _p811UsersAfterP805Hide =
        Array.Empty<P811PhaseUserSnapshot>();
    private long _p811HideMatchedCount;
    private long _p811HideModifiedCount;
    private int _p811UnitANonDeletedDuringP805;

    private async Task HideP810OnlyUsersForP805Async(CancellationToken ct)
    {
        var users = _database.GetCollection<AppUser>("users");
        var exactUsersFilter = Builders<AppUser>.Filter.In(
            user => user.Username,
            P811P810OnlyUsernames);
        var before = await users.Find(exactUsersFilter)
            .SortBy(user => user.Username)
            .ToListAsync(ct);

        HarnessAssert.Equal(
            P811P810OnlyUsernames.Length,
            before.Count,
            "P8-11 phase isolation did not find the exact six P8-10-only users.");
        HarnessAssert.True(
            before.All(user => !user.IsDeleted),
            "A P8-10-only user was already deleted before the P8-05 phase.");
        HarnessAssert.True(
            before.Select(user => user.Username)
                .OrderBy(value => value, StringComparer.Ordinal)
                .SequenceEqual(
                    P811P810OnlyUsernames.OrderBy(
                        value => value,
                        StringComparer.Ordinal),
                    StringComparer.Ordinal),
            "P8-11 phase isolation selected a user outside the exact owned set.");
        HarnessAssert.Equal(
            5,
            before.Count(user => string.Equals(
                user.UnitId,
                _unitAId,
                StringComparison.Ordinal)),
            "The exact P8-10-only Unit A user set drifted.");
        HarnessAssert.Equal(
            1,
            before.Count(user => string.Equals(
                user.UnitId,
                _unitBId,
                StringComparison.Ordinal)),
            "The exact P8-10-only outsider user set drifted.");

        _p811UsersBeforeP805Hide = P811PhaseSnapshots(before);
        var update = await users.UpdateManyAsync(
            exactUsersFilter &
            Builders<AppUser>.Filter.Eq(user => user.IsDeleted, false),
            Builders<AppUser>.Update.Set(user => user.IsDeleted, true),
            cancellationToken: ct);
        _p811HideMatchedCount = update.MatchedCount;
        _p811HideModifiedCount = update.ModifiedCount;
        HarnessAssert.Equal(
            (long)P811P810OnlyUsernames.Length,
            update.MatchedCount,
            "P8-11 phase isolation did not match exactly six active P8-10-only users.");
        HarnessAssert.Equal(
            (long)P811P810OnlyUsernames.Length,
            update.ModifiedCount,
            "P8-11 phase isolation did not hide exactly six P8-10-only users.");

        var hidden = await users.Find(exactUsersFilter)
            .SortBy(user => user.Username)
            .ToListAsync(ct);
        _p811UsersAfterP805Hide = P811PhaseSnapshots(hidden);
        HarnessAssert.True(
            hidden.Count == P811P810OnlyUsernames.Length &&
            hidden.All(user => user.IsDeleted),
            "P8-11 phase isolation failed to persist IsDeleted=true for the exact owned users.");

        _p811UnitANonDeletedDuringP805 = checked((int)await users
            .CountDocumentsAsync(
                user => user.UnitId == _unitAId && !user.IsDeleted,
                cancellationToken: ct));
        HarnessAssert.Equal(
            2,
            _p811UnitANonDeletedDuringP805,
            "P8-05 Unit A nondeleted user cardinality is not exactly two after phase isolation.");

        await WriteP811PhaseIsolationEvidenceAsync(
            "HIDDEN_FOR_P805",
            restoreMatchedCount: null,
            restoreModifiedCount: null,
            restoredUsers: null,
            ct);
    }

    private async Task RestoreP810OnlyUsersForP810Async(CancellationToken ct)
    {
        HarnessAssert.Equal(
            P811P810OnlyUsernames.Length,
            _p811UsersBeforeP805Hide.Count,
            "P8-10 phase restore has no exact P8-05 hide snapshot.");
        var users = _database.GetCollection<AppUser>("users");
        var exactIds = _p811UsersBeforeP805Hide
            .Select(user => user.Id)
            .ToArray();
        var restoreFilter = Builders<AppUser>.Filter.In(
                                user => user.Id,
                                exactIds) &
                            Builders<AppUser>.Filter.In(
                                user => user.Username,
                                P811P810OnlyUsernames) &
                            Builders<AppUser>.Filter.Eq(
                                user => user.IsDeleted,
                                true);
        var update = await users.UpdateManyAsync(
            restoreFilter,
            Builders<AppUser>.Update.Set(user => user.IsDeleted, false),
            cancellationToken: ct);
        HarnessAssert.Equal(
            (long)P811P810OnlyUsernames.Length,
            update.MatchedCount,
            "P8-11 phase restore did not match the exact six hidden users.");
        HarnessAssert.Equal(
            (long)P811P810OnlyUsernames.Length,
            update.ModifiedCount,
            "P8-11 phase restore did not restore exactly six users.");

        var restored = await users.Find(
                Builders<AppUser>.Filter.In(user => user.Id, exactIds))
            .SortBy(user => user.Username)
            .ToListAsync(ct);
        var restoredSnapshots = P811PhaseSnapshots(restored);
        HarnessAssert.True(
            restoredSnapshots.SequenceEqual(_p811UsersBeforeP805Hide),
            "P8-11 phase restore did not reproduce the exact pre-hide user documents.");
        HarnessAssert.True(
            restored.All(user => !user.IsDeleted),
            "A P8-10-only user remained deleted before the P8-10 phase.");

        await WriteP811PhaseIsolationEvidenceAsync(
            "RESTORED_FOR_P810",
            update.MatchedCount,
            update.ModifiedCount,
            restoredSnapshots,
            ct);
    }

    private async Task WriteP811PhaseIsolationEvidenceAsync(
        string state,
        long? restoreMatchedCount,
        long? restoreModifiedCount,
        IReadOnlyList<P811PhaseUserSnapshot>? restoredUsers,
        CancellationToken ct)
    {
        await EvidenceJson.WriteAsync(
            Path.Combine(_paths.RunRoot,
                "p8-p805-p810-user-phase-isolation.json"),
            new
            {
                schemaVersion = 1,
                chainId = ChainId,
                promptId = _promptId,
                contract = "P8-11-P805-P810-USER-PHASE-ISOLATION-1",
                state,
                exactOwnedUsernames = P811P810OnlyUsernames,
                changedField = "isDeleted",
                hide = new
                {
                    matchedCount = _p811HideMatchedCount,
                    modifiedCount = _p811HideModifiedCount,
                    usersBefore = _p811UsersBeforeP805Hide,
                    usersAfter = _p811UsersAfterP805Hide,
                    unitANonDeletedCount =
                        _p811UnitANonDeletedDuringP805,
                    exactP805Cardinality =
                        _p811UnitANonDeletedDuringP805 == 2
                },
                restore = restoreMatchedCount is null
                    ? null
                    : new
                    {
                        matchedCount = restoreMatchedCount.Value,
                        modifiedCount = restoreModifiedCount!.Value,
                        users = restoredUsers,
                        exactDocumentsRestored = restoredUsers is not null &&
                            restoredUsers.SequenceEqual(
                                _p811UsersBeforeP805Hide)
                    },
                passed = _p811HideMatchedCount ==
                         P811P810OnlyUsernames.Length &&
                         _p811HideModifiedCount ==
                         P811P810OnlyUsernames.Length &&
                         _p811UnitANonDeletedDuringP805 == 2 &&
                         (restoreMatchedCount is null ||
                          restoreMatchedCount.Value ==
                          P811P810OnlyUsernames.Length &&
                          restoreModifiedCount ==
                          P811P810OnlyUsernames.Length &&
                          restoredUsers is not null &&
                          restoredUsers.SequenceEqual(
                              _p811UsersBeforeP805Hide))
            },
            ct);
    }

    private static IReadOnlyList<P811PhaseUserSnapshot> P811PhaseSnapshots(
        IEnumerable<AppUser> users)
        => users.OrderBy(user => user.Username, StringComparer.Ordinal)
            .Select(user => new P811PhaseUserSnapshot(
                user.Id,
                user.Username,
                user.UnitId,
                user.IsDeleted))
            .ToArray();
}

internal sealed record P811PhaseUserSnapshot(
    string Id,
    string Username,
    string? UnitId,
    bool IsDeleted);
