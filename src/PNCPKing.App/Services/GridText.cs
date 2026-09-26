using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Reflection;
using System.Text;

namespace PNCPKing.App.Services;

// Text operations have no dependency on WPF, storage or the PNCP client.
internal static class GridText
{
    private static readonly CompareInfo Comparison = CultureInfo.GetCultureInfo("pt-BR").CompareInfo;
    private static readonly ConcurrentDictionary<(Type, string), PropertyInfo[]> Paths = new();

    public static object? Read(object row, string path)
    {
        var properties = Paths.GetOrAdd((row.GetType(), path), key =>
        {
            var result = new List<PropertyInfo>();
            var type = key.Item1;
            foreach (var name in key.Item2.Split('.'))
            {
                var property = type.GetProperty(name);
                if (property is null) return [];
                result.Add(property);
                type = property.PropertyType;
            }
            return result.ToArray();
        });
        object? value = row;
        foreach (var property in properties)
        {
            if (value is null) break;
            value = property.GetValue(value);
        }
        return properties.Length == 0 ? null : value;
    }

    public static string Format(object? value, string? format, CultureInfo culture) =>
        value is null ? string.Empty : string.IsNullOrEmpty(format)
            ? Convert.ToString(value, culture) ?? string.Empty
            : string.Format(culture, format.Contains('{') ? format : "{0:" + format + "}", value);

    public static string SingleLine(string text)
    {
        var index = text.IndexOfAny(['\r', '\n', '\t', '\u0085', '\u2028', '\u2029']);
        if (index < 0) return text;
        var builder = new StringBuilder(text.Length);
        builder.Append(text.AsSpan(0, index));
        for (var i = index; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
            builder.Append(c is '\r' or '\n' or '\t' or '\u0085' or '\u2028' or '\u2029' ? ' ' : c);
        }
        return builder.ToString();
    }

    public static (int Start, int Length) Find(string text, string term)
    {
        if (string.IsNullOrWhiteSpace(term)) return (-1, 0);
        var start = Comparison.IndexOf(text.AsSpan(), term.AsSpan(),
            CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace, out var length);
        return length == 0 ? (-1, 0) : (start, length);
    }

    public static string Tabular(IEnumerable<string[]> rows) => string.Join("\r\n", rows.Select(row =>
        string.Join('\t', row.Select(value => value.IndexOfAny(['\t', '\r', '\n', '"']) < 0
            ? value : "\"" + value.Replace("\"", "\"\"") + "\""))));

    public static string ClipboardHtml(IEnumerable<string[]> rows, IReadOnlyList<bool> textColumns)
    {
        var body = new StringBuilder("<table>");
        foreach (var row in rows)
        {
            body.Append("<tr>");
            for (var i = 0; i < row.Length; i++)
            {
                body.Append(textColumns[i] ? "<td style=\"mso-number-format:'\\@'\">" : "<td>");
                body.Append(WebUtility.HtmlEncode(row[i]).Replace("\r\n", "\n").Replace("\n", "<br>"));
                body.Append("</td>");
            }
            body.Append("</tr>");
        }
        body.Append("</table>");
        const string prefix = "<html><body><!--StartFragment-->";
        const string suffix = "<!--EndFragment--></body></html>";
        const string header = "Version:0.9\r\nStartHTML:{0:0000000000}\r\nEndHTML:{1:0000000000}\r\nStartFragment:{2:0000000000}\r\nEndFragment:{3:0000000000}\r\n";
        var start = Encoding.UTF8.GetByteCount(string.Format(CultureInfo.InvariantCulture, header, 0, 0, 0, 0));
        var fragmentStart = start + Encoding.UTF8.GetByteCount(prefix);
        var fragmentEnd = fragmentStart + Encoding.UTF8.GetByteCount(body.ToString());
        var end = fragmentEnd + Encoding.UTF8.GetByteCount(suffix);
        return string.Format(CultureInfo.InvariantCulture, header, start, end, fragmentStart, fragmentEnd) + prefix + body + suffix;
    }
}
