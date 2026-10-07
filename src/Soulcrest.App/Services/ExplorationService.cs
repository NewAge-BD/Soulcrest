using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Soulcrest.App.Services;

/// <summary>A sealed dungeon, stronghold or Kibelisk (Kind "dungeon", "stronghold", "kibelisk") at map position X/Y.</summary>
public sealed record ExplorationPlace(string Id, string Map, string Kind, int Marker, string En, string? De, double X = 0, double Y = 0)
{
    public string Name(string language) => language == "de" && !string.IsNullOrWhiteSpace(De) ? De : En;
}
public sealed class CharacterExploration
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Character 1";
    public HashSet<string> Done { get; set; } = [];
}
public sealed class ExplorationProfiles
{
    public string Active { get; set; } = "";
    public List<CharacterExploration> Characters { get; set; } = [];
}

public enum CharacterChoice { Unchanged, Selected, Adopted, Created }

/// <summary>Character-only exploration state. Pet progress remains server-wide.</summary>
public sealed partial class ExplorationService
{
    private readonly object _gate = new();
    private readonly string _path;
    private readonly ExplorationProfiles _state;
    public IReadOnlyList<ExplorationPlace> Places { get; }
    public event Action? Changed;
    public ExplorationService(ProgressService progress) : this(Path.Combine(AppPaths.DataDirectory, "exploration.json"), LoadPlaces(progress)) { }
    internal ExplorationService(string path, IReadOnlyList<ExplorationPlace> places)
    {
        _path = path; Places = places; _state = JsonFile.Load<ExplorationProfiles>(path);
        if (_state.Characters.Count == 0) _state.Characters.Add(new());
        if (!_state.Characters.Any(c => c.Id == _state.Active)) _state.Active = _state.Characters[0].Id;
        foreach (var c in _state.Characters)
            foreach (var (old, now) in Renamed)
                if (c.Done.Remove(old) && Places.Any(p => p.Id == now)) c.Done.Add(now);
    }

    /// <summary>
    /// Ids from before the import named the Altgard Kibelisk at 5407/3467 "Shulak Street Stall" (user screenshots
    /// 2026-10-06): it was "#1" of the two "Steel Hammer Temporary Trading Post", the other one was "#2".
    /// </summary>
    private static readonly (string Old, string New)[] Renamed =
    [
        ("altgard|kibelisk|Steel Hammer Temporary Trading Post#1", "altgard|kibelisk|Shulak Street Stall"),
        ("altgard|kibelisk|Steel Hammer Temporary Trading Post#2", "altgard|kibelisk|Steel Hammer Temporary Trading Post"),
    ];
    public string ActiveId { get { lock (_gate) return _state.Active; } }
    public IReadOnlyList<(string Id, string Name)> Characters { get { lock (_gate) return _state.Characters.Select(c => (c.Id, c.Name)).ToArray(); } }
    public HashSet<string> Completed { get { lock (_gate) return new(_state.Characters.Single(c => c.Id == _state.Active).Done); } }
    public void Select(string id)
    {
        lock (_gate) { if (!_state.Characters.Any(c => c.Id == id)) return; _state.Active = id; JsonFile.Save(_path, _state); }
        Changed?.Invoke();
    }
    public void Add(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        lock (_gate) { var c = new CharacterExploration { Name = name.Trim() }; _state.Characters.Add(c); _state.Active = c.Id; JsonFile.Save(_path, _state); }
        Changed?.Invoke();
    }
    /// <summary>
    /// The character detected after a loading screen (user request 2026-10-06): its profile is chosen.
    /// An unknown name takes over the oldest never-renamed default profile ("Character 1"), so progress
    /// checked off before the detection stays; otherwise a new profile is made.
    /// </summary>
    public CharacterChoice UseCharacter(string name)
    {
        name = name.Trim();
        if (name.Length == 0) return CharacterChoice.Unchanged;
        CharacterChoice choice;
        lock (_gate)
        {
            var known = _state.Characters.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
            if (known is not null)
            {
                if (known.Id == _state.Active) return CharacterChoice.Unchanged;
                _state.Active = known.Id;
                choice = CharacterChoice.Selected;
            }
            else if (_state.Characters.FirstOrDefault(c => DefaultName().IsMatch(c.Name)) is { } unnamed)
            {
                unnamed.Name = name;
                _state.Active = unnamed.Id;
                choice = CharacterChoice.Adopted;
            }
            else
            {
                var created = new CharacterExploration { Name = name };
                _state.Characters.Add(created);
                _state.Active = created.Id;
                choice = CharacterChoice.Created;
            }
            JsonFile.Save(_path, _state);
        }
        Changed?.Invoke();
        return choice;
    }

    [GeneratedRegex(@"^Character \d+$")]
    private static partial Regex DefaultName();

    public void Rename(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        lock (_gate) { _state.Characters.Single(c => c.Id == _state.Active).Name = name.Trim(); JsonFile.Save(_path, _state); }
        Changed?.Invoke();
    }
    public void SetDone(string character, IEnumerable<string> ids, bool done = true)
    {
        var changed = false;
        lock (_gate)
        {
            var c = _state.Characters.SingleOrDefault(c => c.Id == character);
            if (c is null) return;
            foreach (var id in ids.Where(id => Places.Any(p => p.Id == id))) changed |= done ? c.Done.Add(id) : c.Done.Remove(id);
            if (changed) JsonFile.Save(_path, _state);
        }
        if (changed) Changed?.Invoke();
    }
    private static IReadOnlyList<ExplorationPlace> LoadPlaces(ProgressService progress)
    {
        var result = new List<ExplorationPlace>();
        if (progress.MapDataDirectory is null) return result;
        foreach (var map in progress.Maps)
        {
            var path = Path.Combine(progress.MapDataDirectory, map.Id, "data.js");
            if (!File.Exists(path)) continue;
            var text = File.ReadAllText(path); var start = text.IndexOf("] = {", StringComparison.Ordinal); var end = text.LastIndexOf('}');
            if (start < 0) continue;
            using var doc = JsonDocument.Parse(text.AsMemory(start + 4, end - start - 3));
            var cats = doc.RootElement.GetProperty("categories").EnumerateArray().Select(c => c.GetProperty("name").GetString()).ToArray();
            var index = 0;
            foreach (var m in doc.RootElement.GetProperty("markers").EnumerateArray())
            {
                // Kibelisks: done per character on arrival (user request 2026-10-06, Questrunner Q1).
                var kind = cats[m[0].GetInt32()] switch { "Sealed Dungeon" => "dungeon", "Stronghold" => "stronghold", "Kibelisk" => "kibelisk", _ => null };
                if (kind is not null && m[3].GetString() is { Length: > 0 } en)
                    result.Add(new($"{map.Id}|{kind}|{en}", map.Id, kind, index, en, m[4].GetString(), m[1].GetDouble(), m[2].GetDouble()));
                index++;
            }
        }
        return UniqueKibelisks(result);
    }

    /// <summary>
    /// Some Kibelisks share a name at places far apart (Verteron "Cantas Valley Mushroom Tree Peak",
    /// 170 px apart). They get "#1", "#2" by their x position, so reaching one does not check off the other.
    /// </summary>
    internal static List<ExplorationPlace> UniqueKibelisks(List<ExplorationPlace> places)
    {
        foreach (var group in places.Where(p => p.Kind == "kibelisk").GroupBy(p => p.Id).Where(g => g.Count() > 1).ToList())
        {
            var number = 0;
            foreach (var place in group.OrderBy(p => p.X).ThenBy(p => p.Y))
                places[places.IndexOf(place)] = place with { Id = $"{place.Id}#{++number}" };
        }
        return places;
    }

    internal static string Normalize(string text)
    {
        text = text.ToLowerInvariant().Replace("ß", "ss").Normalize(NormalizationForm.FormD);
        text = new string(text.Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray());
        text = Regex.Replace(text, @"\bentrance\b|^eingang (zur|zum|zu den|zu der) ", "");
        return new string(text.Where(char.IsLetterOrDigit).ToArray());
    }
    internal static ExplorationPlace? Match(string text, IEnumerable<ExplorationPlace> candidates)
    {
        var name = Normalize(text);
        // List names evidenced by the user's Altgard screenshots (2026-10-04).
        var alias = name switch { "lostruins" => "Lost Ruin Entrance", "fafnitestorageroom" => "Fafnite Storage Entrance", _ => null };
        if (alias is not null)
        {
            var matches = candidates.Where(p => p.Map == "altgard" && p.Kind == "dungeon" && p.En == alias).DistinctBy(p => p.Id).ToArray();
            if (matches.Length == 1) return matches[0];
        }
        if (name.Length < 5) return null;
        var exact = candidates.Where(p => Normalize(p.En) == name || p.De is not null && Normalize(p.De) == name).DistinctBy(p => p.Id).ToArray();
        if (exact.Length > 0) return exact.Length == 1 ? exact[0] : null;
        var ranked = candidates.GroupBy(p => p.Id).Select(g => g.First()).Select(p => (Place: p,
            Score: new[] { p.En, p.De }.Where(n => !string.IsNullOrEmpty(n)).Select(n => Similarity(name, Normalize(n!))).Max()))
            .OrderByDescending(p => p.Score).Take(2).ToArray();
        return ranked.Length > 0 && ranked[0].Score >= .90 && (ranked.Length == 1 || ranked[0].Score - ranked[1].Score >= .08) ? ranked[0].Place : null;
    }
    internal static int Distance(string a, string b) => (int)Math.Round((1 - Similarity(a, b)) * Math.Max(a.Length, b.Length));

    private static double Similarity(string a, string b)
    {
        var previous = Enumerable.Range(0, b.Length + 1).ToArray();
        for (var i = 1; i <= a.Length; i++)
        {
            var row = new int[b.Length + 1]; row[0] = i;
            for (var j = 1; j <= b.Length; j++) row[j] = Math.Min(Math.Min(row[j - 1] + 1, previous[j] + 1), previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            previous = row;
        }
        return 1 - (double)previous[b.Length] / Math.Max(a.Length, b.Length);
    }
}
