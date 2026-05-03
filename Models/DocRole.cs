using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace tdtd_be.Models;

public enum DocType
{
    WORK = 1,
    WORK_ASSIGNMENT = 2,
    WORK_REPORT = 3
}

public enum DocRoleType
{
    OWNER = 1,
    LEADER_DIRECTIVE = 2,
    LEADER_WATCH = 3,

    ASSIGNEE = 10,
    ASSIGNER = 11,
    ASSIGNMENT_LEADER_WATCH = 13,
    ASSIGNMENT_BRANCH_VIEWER = 14,

    WORK_PARTICIPANT = 20
}

[BsonIgnoreExtraElements]
public sealed class DocRole : DocRoleBase
{
    [BsonElement("role")]
    public DocRoleType Role { get; set; }
}
