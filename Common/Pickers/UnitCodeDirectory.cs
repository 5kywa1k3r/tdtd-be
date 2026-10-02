using System.Text.RegularExpressions;

namespace tdtd_be.Common.Pickers;

public static class UnitCodeDirectory
{
    public static string? ParentPrefix(string? code)
    {
        var value = code?.Trim();
        return value is not null && Regex.IsMatch(value, @"\A[0-9]{3}(?:[0-9]{3})*\z")
            ? value[..^3] : null;
    }

    // Anchored prefix query; complete three-digit segments only, excludes the parent itself.
    public static string Pattern(string prefix) => "^" + Regex.Escape(prefix) + @"[0-9]{3}(?:[0-9]{3})*$";
}
