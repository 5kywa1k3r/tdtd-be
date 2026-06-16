using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using tdtd_be.Data.Infrastructure;

namespace tdtd_be.Models;

[BsonIgnoreExtraElements]
[BsonCollection("labels")]
public sealed class LabelCatalogItem : BaseEntity
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string Id { get; set; } = default!;

    [BsonElement("code")]
    public string Code { get; set; } = default!;

    [BsonElement("name")]
    public string Name { get; set; } = default!;

    [BsonElement("nameLower")]
    public string NameLower { get; set; } = default!;

    [BsonElement("description")]
    public string? Description { get; set; }

    [BsonElement("color")]
    public string? Color { get; set; }

    [BsonElement("groupCode")]
    public string? GroupCode { get; set; }

    [BsonElement("usage")]
    public string Usage { get; set; } = LabelUsages.Classification;

    [BsonElement("dataType")]
    public string DataType { get; set; } = LabelDataTypes.Number;

    [BsonElement("valueSourceType")]
    public string ValueSourceType { get; set; } = LabelValueSourceTypes.None;

    [BsonElement("valueOptions")]
    public List<LabelValueOption> ValueOptions { get; set; } = new();

    [BsonElement("valueSourceCatalogId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? ValueSourceCatalogId { get; set; }

    [BsonElement("valueSourceCatalogCode")]
    public string? ValueSourceCatalogCode { get; set; }

    [BsonElement("valueSourceCatalogName")]
    public string? ValueSourceCatalogName { get; set; }

    [BsonElement("scopeType")]
    public string ScopeType { get; set; } = LabelScopeTypes.Global;

    [BsonElement("scopeId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? ScopeId { get; set; }

    [BsonElement("managedByUserId")]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? ManagedByUserId { get; set; }

    [BsonElement("isSystem")]
    public bool IsSystem { get; set; }

    [BsonElement("isActive")]
    public bool IsActive { get; set; } = true;
}

public static class LabelScopeTypes
{
    public const string Global = "GLOBAL";
    public const string Level = "LEVEL";
    public const string Unit = "UNIT";
}

public static class LabelDataTypes
{
    public const string Number = "NUMBER";
    public const string ShortText = "SHORT_TEXT";
    public const string StringList = "STRING_LIST";
    public const string LongText = "LONG_TEXT";
    public const string Date = "DATE";
    public const string Boolean = "BOOLEAN";

    public static string Normalize(string? value)
    {
        var normalized = value?.Trim().ToUpperInvariant();
        return normalized switch
        {
            Number => Number,
            ShortText => ShortText,
            StringList => StringList,
            LongText => StringList,
            Date => Date,
            "FULL_DATE" => Date,
            "FULLDATE" => Date,
            "STRICT_DATE" => Date,
            Boolean => Boolean,
            "TEXT" => ShortText,
            "STRING" => ShortText,
            "SHORTTEXT" => ShortText,
            "STRINGLIST" => StringList,
            "MULTI_SELECT" => ShortText,
            "MULTISELECT" => ShortText,
            "LONGDATE" => StringList,
            "LONGTEXT" => StringList,
            _ => Number
        };
    }
}

public sealed class LabelValueOption
{
    [BsonElement("code")]
    public string Code { get; set; } = default!;

    [BsonElement("label")]
    public string Label { get; set; } = default!;
}

public static class LabelValueSourceTypes
{
    public const string None = "NONE";
    public const string FixedEnum = "FIXED_ENUM";
    public const string EnumCatalog = "ENUM_CATALOG";
    public const string SystemUnit = "SYSTEM_UNIT";
    public const string SystemUser = "SYSTEM_USER";
    public const string SystemPosition = "SYSTEM_POSITION";
    public const string SystemUnitType = "SYSTEM_UNIT_TYPE";

    public static string Normalize(string? value)
    {
        var normalized = value?.Trim().ToUpperInvariant();
        return normalized switch
        {
            FixedEnum => FixedEnum,
            "ENUM" => FixedEnum,
            "LIST" => FixedEnum,
            "STATIC_ENUM" => FixedEnum,
            EnumCatalog => EnumCatalog,
            "LABEL_ENUM" => EnumCatalog,
            "LABEL_ENUM_CATALOG" => EnumCatalog,
            "CUSTOM_ENUM" => EnumCatalog,
            "CUSTOM_ENUM_CATALOG" => EnumCatalog,
            SystemUnit => SystemUnit,
            "UNIT" => SystemUnit,
            "UNITS" => SystemUnit,
            SystemUser => SystemUser,
            "USER" => SystemUser,
            "USERS" => SystemUser,
            SystemPosition => SystemPosition,
            "POSITION" => SystemPosition,
            "POSITIONS" => SystemPosition,
            SystemUnitType => SystemUnitType,
            "UNIT_TYPE" => SystemUnitType,
            "UNIT_TYPES" => SystemUnitType,
            _ => None
        };
    }

    public static bool UsesCatalog(string? value)
    {
        var normalized = Normalize(value);
        return normalized is EnumCatalog or SystemUnit or SystemUser or SystemPosition or SystemUnitType;
    }
}

public static class LabelUsages
{
    public const string Classification = "CLASSIFICATION";
    public const string Statistic = "STATISTIC";
    public const string TableTarget = "TABLE_TARGET";

    public static string Normalize(string? value, string fallback = Classification)
    {
        var normalized = value?.Trim().ToUpperInvariant();
        return normalized switch
        {
            Classification => Classification,
            Statistic => Statistic,
            TableTarget => TableTarget,
            "TAG" => Classification,
            "FORM" => Classification,
            "SECTION" => Classification,
            "BLOCK" => Classification,
            "LABEL" => Classification,
            "STATS" => Statistic,
            "FIELD_STATISTIC" => Statistic,
            _ => fallback
        };
    }

    public static bool CanUseAsStatistic(string? value)
    {
        return Normalize(value, string.Empty) == Statistic;
    }

    public static bool CanUseAsTableTarget(string? value)
    {
        return Normalize(value, string.Empty) == TableTarget;
    }
}
