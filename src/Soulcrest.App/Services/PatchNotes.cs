using System.Text.RegularExpressions;

namespace Soulcrest.App.Services;

/// <summary>
/// The patch notes of docs/PATCHNOTES.md, embedded in the app for the Patchnotes tab (user request
/// 2026-10-07). Reads the small Markdown subset the file uses: "## 0.1.28 – 2026-10-07", "- **Neu:** text"
/// with indented continuation lines, **bold** and `code`.
/// </summary>
public static partial class PatchNotes
{
    public sealed record Entry(string Kind, string Text);

    public sealed record Release(string Version, string Date, IReadOnlyList<Entry> Entries);

    private static readonly Lazy<IReadOnlyList<Release>> Embedded = new(() =>
    {
        using var stream = typeof(PatchNotes).Assembly.GetManifestResourceStream("Soulcrest.PATCHNOTES.md");
        if (stream is null)
            return [];
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    });

    public static IReadOnlyList<Release> All => Embedded.Value;

    /// <summary>The tab starts with 0.1.28 (user decision 2026-10-07); older notes stay in the file.</summary>
    public static readonly Version FirstShown = new(0, 1, 28);

    public static IReadOnlyList<Release> Shown =>
        [.. All.Where(r => System.Version.TryParse(r.Version, out var v) && v >= FirstShown)];

    internal static IReadOnlyList<Release> Parse(string markdown)
    {
        var releases = new List<Release>();
        string? version = null, date = null;
        var entries = new List<Entry>();
        string? text = null;

        void EndEntry()
        {
            if (text is null)
                return;
            var kind = KindPattern().Match(text);
            entries.Add(kind.Success ? new Entry(kind.Groups[1].Value, text[kind.Length..].Trim()) : new Entry("", text));
            text = null;
        }

        void EndRelease()
        {
            EndEntry();
            if (version is not null)
                releases.Add(new Release(version, date ?? "", entries));
            entries = [];
        }

        foreach (var raw in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                EndRelease();
                var heading = line[3..].Split('–', 2, StringSplitOptions.TrimEntries);
                version = heading[0];
                date = heading.Length > 1 ? heading[1] : null;
            }
            else if (version is null)
                continue;
            else if (line.StartsWith("- ", StringComparison.Ordinal))
            {
                EndEntry();
                text = line[2..].Trim();
            }
            else if (text is not null && line.StartsWith("  ", StringComparison.Ordinal))
                text += " " + line.Trim();
            else
                EndEntry();
        }
        EndRelease();
        return releases;
    }

    /// <summary>A text piece of an entry: plain, bold or code.</summary>
    public readonly record struct Span(string Text, bool Bold, bool Code);

    public static IReadOnlyList<Span> Spans(string text)
    {
        var spans = new List<Span>();
        var at = 0;
        foreach (Match m in InlinePattern().Matches(text))
        {
            if (m.Index > at)
                spans.Add(new Span(text[at..m.Index], false, false));
            spans.Add(m.Groups[1].Success ? new Span(m.Groups[1].Value, true, false) : new Span(m.Groups[2].Value, false, true));
            at = m.Index + m.Length;
        }
        if (at < text.Length)
            spans.Add(new Span(text[at..], false, false));
        return spans;
    }

    [GeneratedRegex(@"^\*\*([^*:]+):\*\*")]
    private static partial Regex KindPattern();

    [GeneratedRegex(@"\*\*(.+?)\*\*|`([^`]+)`")]
    private static partial Regex InlinePattern();
}
