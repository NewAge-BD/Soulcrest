using System.Text;

namespace Soulcrest.Core.Text;

/// <summary>Comparing read names (pet window): normalized form and edit distance.</summary>
public static class NameText
{
    public static string Normalize(string text)
    {
        var builder = new StringBuilder(text.Length);
        var lastWasSpace = true;
        foreach (var raw in text.Normalize(NormalizationForm.FormKC))
        {
            var c = char.ToLowerInvariant(raw);
            if (char.IsLetterOrDigit(c) || c == '\'')
            {
                builder.Append(c);
                lastWasSpace = false;
            }
            else if (!lastWasSpace)
            {
                builder.Append(' ');
                lastWasSpace = true;
            }
        }
        return builder.ToString().Trim();
    }

    public static int Levenshtein(string a, string b)
    {
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++)
            previous[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }
            (previous, current) = (current, previous);
        }
        return previous[b.Length];
    }
}
