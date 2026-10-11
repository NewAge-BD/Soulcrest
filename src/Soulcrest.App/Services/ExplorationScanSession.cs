namespace Soulcrest.App.Services;

/// <summary>
/// Evidence for the currently recognised world map. A map change starts fresh confirmation;
/// saved character progress belongs to ExplorationService and is never changed here.
/// </summary>
internal sealed class ExplorationScanSession(ExplorationService exploration)
{
    public string? Map { get; private set; }
    public IReadOnlyList<string> MapTitles { get; private set; } = [];
    public ExplorationPlace[] Candidates { get; private set; } = [];
    public KibeliskSession? Kibelisks { get; private set; }
    public int KibeliskSettled { get; set; }
    public ExplorationScanService.Confirmation Confirmation { get; set; } = new();
    public HashSet<string> Seen { get; private set; } = [];
    public HashSet<string> FinishedKinds { get; private set; } = [];
    public HashSet<string> FullKinds { get; private set; } = [];

    /// <summary>Movement or an unclear map interrupts votes, while confirmed progress stays collected.</summary>
    public void ResetPending()
    {
        Confirmation = new();
        Kibelisks?.ResetPending();
    }

    public bool SwitchMap(string map)
    {
        if (Map == map) return false;
        var places = exploration.Places.Where(place => place.Map == map).ToArray();
        Map = map;
        MapTitles = exploration.MapTitles(map).ToArray();
        Candidates = places.Where(place => place.Kind is "dungeon" or "stronghold").ToArray();
        var kibelisks = places.Where(place => place.Kind == "kibelisk").ToArray();
        Kibelisks = kibelisks.Length == 0 ? null : new KibeliskSession(kibelisks);
        KibeliskSettled = 0;
        Confirmation = new();
        Seen = [];
        FinishedKinds = [];
        FullKinds = [];
        return true;
    }
}
