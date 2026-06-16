using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using tdtd_be.Models;
using tdtd_be.Options;
using tdtd_be.Services;

namespace tdtd_be.Services.WorkAssignments.Internal;

public sealed class WorkAssignmentTargetScopePolicy
{
    private readonly IReadOnlyList<NormalizedUnitTypeAssignmentRule> _rules;

    public WorkAssignmentTargetScopePolicy(IOptions<WorkAssignmentScopeOptions> options)
    {
        _rules = (options.Value.UnitTypeAssignmentRules ?? new List<UnitTypeAssignmentRuleOptions>())
            .Where(x => x.Enabled)
            .Select(NormalizeRule)
            .Where(x => x.ActorUnitTypeCodes.Count > 0 && x.TargetUnitTypeCodes.Count > 0)
            .ToList();
    }

    public bool AllowsConfiguredTarget(Unit actorUnit, Unit targetUnit, AppUser targetUser)
        => AllowsConfiguredTargetAccountKind(actorUnit, targetUnit, ResolveAccountKind(targetUser));

    public bool AllowsConfiguredTargetAccountKind(Unit actorUnit, Unit targetUnit, string targetAccountKind)
    {
        if (_rules.Count == 0)
            return false;

        var actorTypes = ResolveUnitTypeCodes(actorUnit);
        var targetTypes = ResolveUnitTypeCodes(targetUnit);
        var normalizedTargetKind = NormalizeCode(targetAccountKind);

        return _rules.Any(rule =>
            actorTypes.Overlaps(rule.ActorUnitTypeCodes) &&
            targetTypes.Overlaps(rule.TargetUnitTypeCodes) &&
            (rule.TargetAccountKinds.Count == 0 || rule.TargetAccountKinds.Contains(normalizedTargetKind)));
    }

    private static NormalizedUnitTypeAssignmentRule NormalizeRule(UnitTypeAssignmentRuleOptions rule)
        => new(
            NormalizeCodes(rule.ActorUnitTypeCodes),
            NormalizeCodes(rule.TargetUnitTypeCodes),
            NormalizeCodes(rule.TargetAccountKinds));

    private static HashSet<string> ResolveUnitTypeCodes(Unit unit)
    {
        var values = new List<string>();
        if (!string.IsNullOrWhiteSpace(unit.PrimaryUnitTypeCode))
            values.Add(unit.PrimaryUnitTypeCode);
        if (unit.UnitTypeCodes is { Count: > 0 })
            values.AddRange(unit.UnitTypeCodes);

        return NormalizeCodes(values);
    }

    private static HashSet<string> NormalizeCodes(IEnumerable<string>? values)
        => (values ?? Array.Empty<string>())
            .Select(NormalizeCode)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToHashSet(StringComparer.Ordinal);

    private static string NormalizeCode(string? value)
    {
        var normalized = (value ?? string.Empty).Trim().ToUpperInvariant();
        return string.IsNullOrWhiteSpace(normalized)
            ? string.Empty
            : Regex.Replace(normalized, @"[\s\-]+", "_");
    }

    private static string ResolveAccountKind(AppUser user)
    {
        if (string.Equals(user.AccountKind, ManagementAccountKind.UnitManager, StringComparison.OrdinalIgnoreCase) ||
            (user.Username ?? string.Empty).StartsWith(ManagementAccountConvention.UnitManagerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return ManagementAccountKind.UnitManager;
        }

        if (string.Equals(user.AccountKind, ManagementAccountKind.LevelManager, StringComparison.OrdinalIgnoreCase) ||
            (user.Username ?? string.Empty).StartsWith(ManagementAccountConvention.LevelManagerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return ManagementAccountKind.LevelManager;
        }

        return string.IsNullOrWhiteSpace(user.AccountKind)
            ? ManagementAccountKind.NormalUser
            : user.AccountKind;
    }

    private sealed record NormalizedUnitTypeAssignmentRule(
        HashSet<string> ActorUnitTypeCodes,
        HashSet<string> TargetUnitTypeCodes,
        HashSet<string> TargetAccountKinds);
}
