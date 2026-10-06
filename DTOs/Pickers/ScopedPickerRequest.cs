using System.Text.Json.Serialization;
using tdtd_be.Models;

namespace tdtd_be.DTOs.Pickers;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PickerPurpose { WorkLeaders, AssignmentRecipients, AssignmentWatchers }

public sealed class PickerContext
{
    public PickerPurpose? Purpose { get; set; }
    public string? WorkId { get; set; }
    public string? ParentAssignmentId { get; set; }
    public List<string> AssigneeUnitIds { get; set; } = new();
    public List<string> AssigneeUserIds { get; set; } = new();
}

public sealed class ScopedPickerRequest
{
    public PickerContext Context { get; set; } = new();
    public string Operation { get; set; } = "";
    public string? ParentId { get; set; }
    public string? UnitId { get; set; }
    public string? Q { get; set; }
    public List<string> Ids { get; set; } = new();
    public int Page { get; set; }
    public int PageSize { get; set; } = 20;
}

// Internal read scope; never serialized as the picker response.
public sealed record AssignmentPickerScope(List<Unit> Units, List<AppUser> Users, HashSet<string> SelectableUnitIds)
{
    public Dictionary<string, Unit> UserConflicts { get; init; } = new(StringComparer.Ordinal);
    public long? UserTotalRows { get; init; }
}
