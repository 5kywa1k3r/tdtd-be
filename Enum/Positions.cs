using MongoDB.Bson;

namespace tdtd_be.Enum;

public static class Positions
{
    // Order theo chục để chèn giữa dễ.
    // Rank để so sánh quyền hạn (cùng rank = ngang cấp).
    private static readonly Dictionary<string, (int Order, int Rank)> PositionMeta =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // ===== TỈNH =====
            ["GIAM_DOC_CAT"] = (10, 100),
            ["PHO_GIAM_DOC_CAT"] = (11, 90),

            // ===== PHÒNG =====
            ["TRUONG_PHONG"] = (20, 80),
            ["PHO_TRUONG_PHONG_PHU_TRACH"] = (21, 80),
            ["PHO_TRUONG_PHONG"] = (22, 70),

            // ===== ĐỘI =====
            ["DOI_TRUONG"] = (30, 60),
            ["PHO_DOI_TRUONG_PHU_TRACH"] = (31, 60),
            ["PHO_DOI_TRUONG"] = (32, 50),

            // ===== PHƯỜNG, XÃ (tách riêng) =====
            ["TRUONG_CONG_AN_PHUONG"] = (40, 40),
            ["TRUONG_CONG_AN_XA"] = (41, 40),
            ["PHO_TRUONG_CONG_AN_PHUONG_PHU_TRACH"] = (42, 40),
            ["PHO_TRUONG_CONG_AN_XA_PHU_TRACH"] = (43, 40),
            ["PHO_TRUONG_CONG_AN_PHUONG"] = (44, 30),
            ["PHO_TRUONG_CONG_AN_XA"] = (45, 30),
        };

    public static IReadOnlyCollection<string> KnownCodes => PositionMeta.Keys.ToArray();

    public static string? Normalize(string? code)
    {
        var s = (code ?? "").Trim();
        return string.IsNullOrWhiteSpace(s) ? null : s;
    }

    public static bool IsValid(string? code)
    {
        var c = Normalize(code);
        return c is not null && PositionMeta.ContainsKey(c);
    }

    public static int GetOrder(string? code)
    {
        var c = Normalize(code);
        if (c is null) return int.MaxValue;
        return PositionMeta.TryGetValue(c, out var m) ? m.Order : int.MaxValue;
    }

    public static int GetRank(string? code)
    {
        var c = Normalize(code);
        if (c is null) return 0;
        return PositionMeta.TryGetValue(c, out var m) ? m.Rank : 0;
    }

    /// <summary>
    /// Build $switch for Mongo aggregate sorting.
    /// positionFieldExpr: "$positionCode"
    /// </summary>
    public static BsonValue BuildMongoSwitchOrder(string positionFieldExpr)
        => BuildMongoSwitch(positionFieldExpr, useRank: false);

    public static BsonValue BuildMongoSwitchRank(string positionFieldExpr)
        => BuildMongoSwitch(positionFieldExpr, useRank: true);

    private static BsonValue BuildMongoSwitch(string positionFieldExpr, bool useRank)
    {
        var branches = new BsonArray();

        foreach (var kv in PositionMeta.OrderBy(x => x.Value.Order))
        {
            branches.Add(new BsonDocument
            {
                { "case", new BsonDocument("$eq", new BsonArray { positionFieldExpr, kv.Key }) },
                { "then", useRank ? kv.Value.Rank : kv.Value.Order }
            });
        }

        return new BsonDocument("$switch", new BsonDocument
        {
            { "branches", branches },
            { "default", useRank ? 0 : 9999 }
        });
    }
}