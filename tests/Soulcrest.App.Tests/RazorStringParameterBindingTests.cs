using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Soulcrest.App.Components;
using Soulcrest.Core.Daevanion;
using Xunit;

namespace Soulcrest.App.Tests;

/// <summary>
/// Razor passes <c>Param="Name"</c> to a <see cref="string"/> component parameter as the literal text "Name".
/// Only <c>Param="@Name"</c> or <c>Param="@(expression)"</c> binds the C# value. Every other parameter type is
/// already evaluated as C#, so this mistake is silent exactly for strings.
/// </summary>
public sealed class RazorStringParameterBindingTests
{
    [Fact]
    public void NoRazorFileBindsAStringParameterToAnIdentifierWithoutAt()
    {
        var root = Path.Combine(TestPrerequisites.RepositoryRoot, "src");
        var files = Directory.EnumerateFiles(root, "*.razor", SearchOption.AllDirectories).ToArray();
        Assert.NotEmpty(files);
        var components = ComponentStringParameters(typeof(MapView).Assembly);
        Assert.True(components.ContainsKey(nameof(MonsterSearchCard)), "The scan must know the app's own components.");
        var violations = new List<string>();
        var checkedBindings = 0;
        foreach (var file in files)
        {
            var source = File.ReadAllText(file);
            foreach (var binding in Scan(source, components))
            {
                checkedBindings++;
                if (binding.IsSuspicious) violations.Add($"{Path.GetRelativePath(TestPrerequisites.RepositoryRoot, file)}:{binding.Line}: " +
                    $"<{binding.Component} {binding.Parameter}=\"{binding.Value}\"> passes the literal text; use {binding.Parameter}=\"@({binding.Value})\".");
            }
        }
        Assert.True(checkedBindings > 50, "The scanner must actually find string parameter bindings; found " + checkedBindings + ".");
        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
    }

    [Theory]
    [InlineData("""<MonsterSearchCard MapId="_mapId" />@code { private string _mapId = ""; }""", "_mapId")]
    [InlineData("""<DaevanionBoardSvg Board="b" Language="Language" />@code { private string Language => UiText.Language; }""", "Language")]
    [InlineData("""@{ var replay = CurrentReplay; }<LootTrackerRouteMap Session="s" MapId="replay.MapId" Replay="replay" />""", "replay.MapId")]
    [InlineData("""@foreach (var current in items) { <MonsterSearchCard SpawnNote="@(_note ?? "")" MapId="current.Map.Id" Busy="_busy" /> }""", "current.Map.Id")]
    [InlineData("""<OverlaySettingsLink OverlayId="Language" />@code { [Parameter] public string Language { get; set; } = ""; }""", "Language")]
    public void ScannerFlagsIdentifierAndMemberAccessWithoutAt(string source, string expected)
    {
        var bindings = Scan(source, ComponentStringParameters(typeof(MapView).Assembly)).Where(binding => binding.IsSuspicious).ToArray();
        Assert.Equal(expected, Assert.Single(bindings).Value);
    }

    [Theory]
    [InlineData("""<MonsterSearchCard MapId="@_mapId" />@code { private string _mapId = ""; }""")]
    [InlineData("""<MonsterSearchCard MapId="@(_mapId)" SpawnNote="@(_note ?? "")" />""")]
    [InlineData("""<DaevanionBoardSvg Language="de" />@code { private string Language => "de"; }""")]
    [InlineData("""<LootTrackerRouteMap MapId="altgard" Session="session" />""")]
    [InlineData("""<MonsterSearchCard Busy="_busy" Monster="_focus" />@code { private bool _busy; }""")]
    [InlineData("""@foreach (var map in maps) { <span>@map</span> }<OverlaySettingsLink OverlayId="map" />""")]
    [InlineData("""@if (view.Boss is { } boss) { <b>@boss.Name</b> }<OverlaySettingsLink OverlayId="boss" />""")]
    public void ScannerAcceptsExplicitBindingsLiteralsAndNonStringParameters(string source) =>
        Assert.DoesNotContain(Scan(source, ComponentStringParameters(typeof(MapView).Assembly)), binding => binding.IsSuspicious);

    /// <summary>Asserts the production SVG of one board in the requested UI language, not the English parameter default.</summary>
    internal static void AssertDaevanionSvgLanguage(string html, DaevanionBoard board, string language)
    {
        var other = language == "de" ? "en" : "de";
        var start = html.IndexOf("<svg", StringComparison.Ordinal);
        Assert.True(start >= 0, "The view must render the production board SVG.");
        var svg = html[start..(html.IndexOf("</svg>", start, StringComparison.Ordinal) + 6)];
        Assert.Matches("^<svg[^>]*aria-label=\"" + Regex.Escape(board.Name.For(language)) + ": ", svg);
        var localized = board.Nodes.Where(node => node.Title.For(language) != node.Title.For(other)).ToArray();
        Assert.NotEmpty(localized); // Otherwise this board cannot distinguish the two languages.
        foreach (var node in localized)
        {
            Assert.Contains("<title>" + node.Title.For(language) + " · ", svg);
            Assert.DoesNotContain("<title>" + node.Title.For(other) + " · ", svg);
        }
    }

    internal sealed record Binding(string Component, string Parameter, string Value, int Line, bool IsSuspicious);

    private static readonly Regex IdentifierPath = new(@"^[A-Za-z_]\w*(?:\.[A-Za-z_]\w*)*$", RegexOptions.CultureInvariant);

    internal static Dictionary<string, HashSet<string>> ComponentStringParameters(Assembly assembly)
    {
        var result = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var type in assembly.GetTypes().Where(type => typeof(IComponent).IsAssignableFrom(type) && !type.IsAbstract))
        {
            var name = type.IsGenericType ? type.Name[..type.Name.IndexOf('`')] : type.Name;
            var parameters = type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Where(property => property.PropertyType == typeof(string) && property.GetCustomAttribute<ParameterAttribute>() is not null)
                .Select(property => property.Name);
            if (result.TryGetValue(name, out var existing)) existing.UnionWith(parameters);
            else result[name] = [.. parameters];
        }
        return result;
    }

    /// <summary>
    /// Reads the attributes of every tag that names a known component. Quoted values may contain nested C# string
    /// literals inside parentheses (<c>"@(x ?? "")"</c>), so values are read with a small paren-aware tokenizer.
    /// </summary>
    internal static IEnumerable<Binding> Scan(string source, IReadOnlyDictionary<string, HashSet<string>> components)
    {
        var text = Regex.Replace(source, @"@\*.*?\*@", match => new string('\n', match.Value.Count(c => c == '\n')), RegexOptions.Singleline);
        var scopes = WithoutRazorCodeBlockBraces(text);
        foreach (Match tag in Regex.Matches(text, @"<([A-Z][\w]*)(?=[\s/>])"))
        {
            if (!components.TryGetValue(tag.Groups[1].Value, out var stringParameters)) continue;
            var position = tag.Index + tag.Length;
            foreach (var (name, value, offset) in Attributes(text, position))
            {
                if (!stringParameters.Contains(name)) continue;
                var line = 1 + text.AsSpan(0, offset).Count('\n');
                yield return new(tag.Groups[1].Value, name, value, line, !value.StartsWith('@') && IsSuspicious(scopes, value, offset));
            }
        }
    }

    /// <summary>
    /// A literal is suspicious when it names a C# field (<c>_x</c>), a member or injected service of the component,
    /// or a local/loop/pattern variable that is in scope at the tag (<c>replay.MapId</c> inside the block that
    /// declares <c>replay</c>). Plain words such as <c>altgard</c> stay valid literals, also when an unrelated
    /// loop elsewhere in the same file happens to use that word as its variable name (<c>OverlayId="map"</c>).
    /// </summary>
    private static bool IsSuspicious(string text, string value, int use)
    {
        if (!IdentifierPath.IsMatch(value)) return false;
        var head = value.Split('.')[0];
        if (head.StartsWith('_')) return true;
        if (head is "this" or "UiText" && value.Contains('.')) return true; // Instance and static roots.
        var name = Regex.Escape(head);
        if (Regex.IsMatch(text, @"\b(?:private|protected|public|internal)\b[^;{}()=\n]*?\b" + name + @"\s*(?:=>|=(?!=)|;|\{)")
            || Regex.IsMatch(text, @"@inject\s+\S+\s+" + name + @"\b"))
            return true;
        foreach (Match declaration in Regex.Matches(text[..use], @"(?:\bvar|[\w>\]?}]+)\s+" + name + @"\s*(?:=(?![=>])|;|\bin\b|\))"))
            if (InScope(text, declaration.Index + declaration.Length, use, headerStyle: declaration.Value.TrimEnd().EndsWith(')') || declaration.Value.EndsWith("in")))
                return true;
        return false;
    }

    /// <summary>Razor emits <c>@{ ... }</c> without a C# block, so its locals stay visible to the following markup.</summary>
    private static string WithoutRazorCodeBlockBraces(string text)
    {
        var chars = text.ToCharArray();
        for (var start = text.IndexOf("@{", StringComparison.Ordinal); start >= 0; start = text.IndexOf("@{", start + 2, StringComparison.Ordinal))
        {
            var depth = 0;
            for (var i = start + 1; i < text.Length; i++)
            {
                if (text[i] == '{') depth++;
                else if (text[i] == '}' && --depth == 0) { chars[start + 1] = chars[i] = ' '; break; }
            }
        }
        return new string(chars);
    }

    /// <summary>
    /// Brace-depth approximation of C# block scope. A header declaration (<c>foreach (var x in ...)</c>,
    /// <c>if (... is { } x)</c>) lives in the block that follows; a statement declaration lives until its block closes.
    /// </summary>
    private static bool InScope(string text, int start, int use, bool headerStyle)
    {
        var depth = 0;
        var opened = false;
        for (var i = start; i < use; i++)
        {
            if (text[i] == '{') { depth++; opened = true; }
            else if (text[i] == '}')
            {
                depth--;
                if (depth < 0 || headerStyle && opened && depth == 0) return false;
            }
        }
        return !headerStyle || opened;
    }

    private static IEnumerable<(string Name, string Value, int Offset)> Attributes(string text, int position)
    {
        while (position < text.Length)
        {
            while (position < text.Length && char.IsWhiteSpace(text[position])) position++;
            if (position >= text.Length || text[position] is '>' or '/') yield break;
            var nameStart = position;
            while (position < text.Length && (char.IsLetterOrDigit(text[position]) || text[position] is '@' or ':' or '-' or '_' or '.')) position++;
            if (position == nameStart) yield break; // Not a parseable tag; ignore rather than guess.
            var name = text[nameStart..position];
            if (position >= text.Length || text[position] != '=') continue; // Boolean attribute without value.
            position++;
            if (position >= text.Length || text[position] is not ('"' or '\'')) yield break;
            var quote = text[position++];
            var valueStart = position;
            if (!ReadValue(text, ref position, quote)) yield break;
            yield return (name, text[valueStart..position].Trim(), valueStart);
            position++; // Closing quote.
        }
    }

    private static bool ReadValue(string text, ref int position, char quote)
    {
        var depth = 0;
        while (position < text.Length)
        {
            var c = text[position];
            if (depth == 0 && c == quote) return true;
            if (c is '(' or '[' or '{') depth++;
            else if (c is ')' or ']' or '}') depth = Math.Max(0, depth - 1);
            else if (depth > 0 && c is '"' or '\'')
            {
                var literal = c; position++;
                while (position < text.Length && text[position] != literal) position += text[position] == '\\' ? 2 : 1;
            }
            position++;
        }
        return false;
    }
}
