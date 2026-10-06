using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace tdtd_be.Services.AggregateMapping;

// Projection for aggregation only. Source payloads remain canonical rich HTML.
// Plain TEXT is never inferred to be markup from its contents.
internal static class AggregateTextProjection
{
    private static readonly HashSet<string> Blocks = new(StringComparer.OrdinalIgnoreCase)
        { "p", "div", "h1", "h2", "h3", "blockquote", "li", "ul", "ol", "table" };

    internal static string VisibleText(AggregateValue value)
        => value.TextFormat == "RICH_HTML" ? Read(value.Text ?? "").Visible : value.Text ?? "";

    internal static AggregateValue ForContent(AggregateValue value)
    {
        if (value.State != "VALUE" || value.TextFormat != "RICH_HTML") return value;
        var text = Read(value.Text ?? "");
        return value with { Text = text.Visible, TextFormat = null,
            // Preserve the existing LEN contract; paragraph-count semantics are separate.
            LengthText = value.LengthText ?? text.LegacyLength };
    }

    // A rich destination stores safe presentation HTML for plain aggregate output.
    // In particular, literal '<' or '&' must not become markup or fail canonicalization.
    internal static string RichDestination(string text)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        var paragraph = new XElement("p");
        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0) paragraph.Add(new XElement("br"));
            paragraph.Add(new XText(lines[i]));
        }
        return paragraph.ToString(SaveOptions.DisableFormatting);
    }

    private static (string Visible, string LegacyLength) Read(string text)
    {
        if (!text.Contains('<')) return (text, text);
        try
        {
            using var reader = XmlReader.Create(new StringReader("<root>" + text + "</root>"), new XmlReaderSettings {
                DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 1_000_000 });
            var root = XDocument.Load(reader, LoadOptions.PreserveWhitespace).Root!;
            return (Render(root.Nodes()), root.Value);
        }
        catch (XmlException) { throw new AggregatePreviewException("AGG_RICH_TEXT_INVALID"); }
    }

    private static string Render(IEnumerable<XNode> nodes)
    {
        var text = new StringBuilder(); var structuralEnd = false;
        void Append(string value)
        {
            if (value.Length == 0) return;
            text.Append(value); structuralEnd = false;
        }
        void Boundary()
        {
            if (text.Length == 0 || text[^1] == '\n') return;
            text.Append('\n'); structuralEnd = true;
        }
        void Visit(XNode node)
        {
            if (node is XText literal) { Append(literal.Value); return; }
            if (node is not XElement element) return;
            var tag = element.Name.LocalName;
            if (tag.Equals("br", StringComparison.OrdinalIgnoreCase)) { Append("\n"); return; }
            if (tag.Equals("tr", StringComparison.OrdinalIgnoreCase))
            {
                Boundary();
                var cells = element.Elements().Where(e => e.Name.LocalName is "td" or "th");
                Append(string.Join("\t", cells.Select(cell => Render(cell.Nodes()))));
                Boundary(); return;
            }
            var block = Blocks.Contains(tag);
            if (block) Boundary();
            foreach (var child in element.Nodes()) Visit(child);
            if (block) Boundary();
        }
        foreach (var node in nodes) Visit(node);
        // Remove only a separator inserted for the final block. Explicit <br> stays.
        if (structuralEnd) text.Length--;
        return text.ToString();
    }
}
