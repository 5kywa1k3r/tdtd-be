using System.Globalization;

namespace tdtd_be.Services.AggregateMapping;

// Content policy, independent of the Form's editable label/maxLength. Transport,
// report counting and blank checks do not inspect or count characters as values.
internal static class AggregateTextPolicy
{
    internal const int ShortTextLimit = 1_000;
    internal const string Semantics = "TEXT_BLOCKS_VISIBLE_GRAPHEMES_1000_V1";
    internal const string StringListBlock = "STRING_LIST_BLOCK";
    internal const string BlockSeparator = "\n\n";

    internal static bool IsLong(AggregateValue value)
    {
        if (value.State != "VALUE") return false;
        var text = AggregateTextProjection.VisibleText(value).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var characters = StringInfo.GetTextElementEnumerator(text);
        for (var count = 0; characters.MoveNext();)
            if (++count > ShortTextLimit) return true;
        return false;
    }

    internal static void CheckValueOperation(string operation, AggregateChannel source)
    {
        if (source.Type != "TEXT" || source.TableOrigin) return; // Table contract stays intact.
        foreach (var item in source.Items) CheckValueOperation(operation, item.Value);
    }

    internal static void CheckValueOperation(string operation, AggregateValue value)
    {
        if (value.Type != "TEXT") return;
        if (operation == "COUNT_DISTINCT" && value.TextFormat == "RICH_HTML")
            throw new AggregatePreviewException("AGG_RICH_TEXT_DISTINCT_UNSUPPORTED");
        if (value.TextFormat == StringListBlock)
            throw new AggregatePreviewException("AGG_TEXT_BLOCK_CONCAT_REQUIRED");
        if (IsLong(value)) throw new AggregatePreviewException("AGG_LONG_TEXT_CONCAT_REQUIRED");
    }
}
