using MongoDB.Driver;

namespace tdtd_be.Common.Pickers;

public static class PickQueryHelper
{
    public static int GetUnitLevel(string? unitCode)
    {
        var code = (unitCode ?? "").Trim();
        if (code.Length == 0) return 0;
        return Math.Max(0, code.Length / 3);
    }

    // prefix range để tận dụng index tốt hơn regex
    // code >= prefix AND code < prefix + '\uffff'
    public static FilterDefinition<T> PrefixRange<T>(
        System.Linq.Expressions.Expression<Func<T, string?>> field,
        string? prefix)
    {
        prefix = (prefix ?? "").Trim();
        if (prefix.Length == 0) return Builders<T>.Filter.Empty;

        var lo = prefix;
        var hi = prefix + "\uffff";
        return Builders<T>.Filter.And(
            Builders<T>.Filter.Gte(field, lo),
            Builders<T>.Filter.Lt(field, hi)
        );
    }

    public static int ClampPageSize(int pageSize, int def = 20, int max = 50)
    {
        if (pageSize <= 0) return def;
        if (pageSize > max) return max;
        return pageSize;
    }
}