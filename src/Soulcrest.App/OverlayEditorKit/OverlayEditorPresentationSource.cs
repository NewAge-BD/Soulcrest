using Soulcrest.App.Overlay;
using Soulcrest.App.Services;

namespace Soulcrest.App.OverlayEditorKit;

/// <summary>A shared read-only projection. Opening an editor never starts a tracker or records a session.</summary>
public sealed class OverlayEditorPresentationSource : IOverlayEditorPresentationSource
{
    private readonly object _gate = new();
    private readonly Func<UnifiedLootOverlayState> _state;
    private readonly Func<RareDropNotice?> _notice;
    private readonly Func<LootTrackerSession, DateTimeOffset, OverlayEditorMarketSnapshot> _market;
    private readonly Func<IDisposable> _acquireMarket;
    private readonly TimeProvider _time;
    private readonly Func<string>? _nameLanguage;
    private readonly Func<bool>? _itemAnimationsEnabled;
    private readonly Func<AbyssRankOverlaySnapshot>? _abyss;
    private readonly Func<string>? _theme;
    private readonly Func<GenusInsightSnapshot>? _genus;
    private readonly Func<IDisposable>? _acquireGenus;
    private readonly Func<bool>? _hasGenusLayout;
    private IDisposable? _genusLease;
    private bool _acquiringGenus;
    private readonly HashSet<Consumer> _consumers = [];
    private Action? _detach;
    private ITimer? _timer;
    private bool _disposed;

    public OverlayEditorPresentationSource(UnifiedLootTrackerService sessions, RareDropAlertService notices,
        OverlayEditorMarketService market, SettingsService settings, IAbyssRankOverlaySource abyss, TimeProvider? time = null,
        GenusInsightStateService? genus = null, GenusMenuObservationService? menu = null, IOverlayService? layouts = null)
        : this(() => sessions.OverlayState, () => notices.LatestVisualNotice, market.Capture, market.Acquire, abyss, time,
            () => settings.Current.NameLanguage, () => settings.Current.LootDropAnimationsEnabled, () => settings.Current.OverlayTheme)
    {
        sessions.Changed += Refresh;
        notices.Changed += Refresh;
        market.Changed += Refresh;
        settings.Changed += Refresh;
        _detach += () => { sessions.Changed -= Refresh; notices.Changed -= Refresh; market.Changed -= Refresh; settings.Changed -= Refresh; };
        if (genus is not null) { _genus = () => genus.Snapshot; genus.Changed += Refresh; _detach += () => genus.Changed -= Refresh; }
        if (menu is not null) _acquireGenus = menu.Acquire;
        if (layouts is not null)
        {
            _hasGenusLayout = () => layouts.LoadError is null && layouts.Overlays.Any(window => window.Settings.Enabled
                && window.Settings.Widgets.Any(widget => widget.Kind == "genus-insight"));
            layouts.Changed += GenusLayoutsChanged; _detach += () => layouts.Changed -= GenusLayoutsChanged;
        }
    }

    internal OverlayEditorPresentationSource(Func<UnifiedLootOverlayState> state, Func<RareDropNotice?> notice,
        Func<LootTrackerSession, DateTimeOffset, OverlayEditorMarketSnapshot> market, Func<IDisposable> acquireMarket,
        IAbyssRankOverlaySource abyss, TimeProvider? time = null, Func<string>? nameLanguage = null,
        Func<bool>? itemAnimationsEnabled = null, Func<string>? theme = null)
        : this(state, notice, market, acquireMarket, time, nameLanguage, itemAnimationsEnabled, () => abyss.Snapshot, theme)
    {
        abyss.Changed += Refresh;
        _detach = () => abyss.Changed -= Refresh;
    }

    internal OverlayEditorPresentationSource(Func<UnifiedLootOverlayState> state, Func<RareDropNotice?> notice,
        Func<LootTrackerSession, DateTimeOffset, OverlayEditorMarketSnapshot> market, Func<IDisposable> acquireMarket,
        TimeProvider? time = null, Func<string>? nameLanguage = null, Func<bool>? itemAnimationsEnabled = null,
        Func<AbyssRankOverlaySnapshot>? abyss = null, Func<string>? theme = null,
        Func<GenusInsightSnapshot>? genus = null, Func<IDisposable>? acquireGenus = null, Func<bool>? hasGenusLayout = null)
    {
        _state = state; _notice = notice; _market = market; _acquireMarket = acquireMarket; _time = time ?? TimeProvider.System;
        _nameLanguage = nameLanguage;
        _itemAnimationsEnabled = itemAnimationsEnabled;
        _abyss = abyss;
        _theme = theme;
        _genus = genus; _acquireGenus = acquireGenus; _hasGenusLayout = hasGenusLayout;
    }

    public event Action? Changed;

    public OverlaySnapshot Capture(bool examples = false, string? language = null)
    {
        var selectedLanguage = language == "de" ? "de" : language is null ? UiText.Language : "en";
        var names = _nameLanguage?.Invoke() ?? selectedLanguage;
        var itemAnimationsEnabled = _itemAnimationsEnabled?.Invoke() ?? true;
        var themeId = OverlayEditorThemes.ResolveThemeId(_theme?.Invoke());
        if (examples) return OverlayEditorPresentation.Examples(_time.GetUtcNow(), _time.LocalTimeZone, selectedLanguage, names)
            with { ItemAnimationsEnabled = itemAnimationsEnabled, ThemeId = themeId };
        lock (_gate) if (_disposed) return new()
            { ClockUtcNow = _time.GetUtcNow(), UiLanguage = selectedLanguage, NameLanguage = names, ItemAnimationsEnabled = itemAnimationsEnabled, ThemeId = themeId };
        var state = _state();
        var now = _time.GetUtcNow();
        var session = OverlayEditorPresentation.CurrentSession(state, now);
        // One market projection, one session and one instant serve every module in this frame.
        var market = session is null ? null : _market(session, now);
        return OverlayEditorPresentation.Project(state, market, _notice(), now, _time.LocalTimeZone, selectedLanguage, names, _abyss?.Invoke())
            with { ItemAnimationsEnabled = itemAnimationsEnabled, ThemeId = themeId, GenusInsight = _genus?.Invoke() ?? GenusInsightSnapshot.Empty };
    }

    public IDisposable Acquire(bool examples = false, bool market = false)
    {
        lock (_gate) if (_disposed) return EmptyLease.Instance;
        var marketLease = market && !examples ? _acquireMarket() : null;
        Consumer? consumer = null;
        lock (_gate)
        {
            if (!_disposed)
            {
                consumer = new Consumer(this, marketLease, examples);
                _consumers.Add(consumer);
                _timer ??= _time.CreateTimer(_ => Refresh(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
            }
        }
        if (consumer is not null) { RefreshGenusLease(); return consumer; }
        marketLease?.Dispose();
        return EmptyLease.Instance;
    }

    private void Release(Consumer consumer)
    {
        lock (_gate)
        {
            _consumers.Remove(consumer);
            if (_consumers.Count == 0) { _timer?.Dispose(); _timer = null; }
        }
        RefreshGenusLease();
    }

    // Runtime visibility reports share this event. They must not trigger another presentation/render loop.
    private void GenusLayoutsChanged() => RefreshGenusLease();
    private bool WantsGenus() => !_disposed && _consumers.Any(consumer => !consumer.Examples) && _hasGenusLayout?.Invoke() == true;
    private void RefreshGenusLease()
    {
        IDisposable? release = null;
        var acquireThisCall = false;
        lock (_gate)
        {
            if (_acquiringGenus) return;
            if (!WantsGenus()) { release = _genusLease; _genusLease = null; }
            else if (_genusLease is null && _acquireGenus is not null) { _acquiringGenus = true; acquireThisCall = true; }
            else return;
        }
        release?.Dispose();
        if (!acquireThisCall) return;
        IDisposable? acquired = null;
        try { acquired = _acquireGenus!(); }
        finally
        {
            lock (_gate)
            {
                _acquiringGenus = false;
                if (WantsGenus()) { _genusLease = acquired; acquired = null; }
            }
            acquired?.Dispose();
        }
    }

    private void Refresh()
    {
        lock (_gate) if (_disposed || _consumers.Count == 0) return;
        SafeEvent.Raise(Changed, "Overlay editor presentation");
    }

    public void Dispose()
    {
        Consumer[] consumers;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _timer?.Dispose(); _timer = null;
            consumers = _consumers.ToArray(); _consumers.Clear();
        }
        _detach?.Invoke(); _detach = null;
        foreach (var consumer in consumers) consumer.Dispose();
        RefreshGenusLease();
    }

    private sealed class Consumer(OverlayEditorPresentationSource owner, IDisposable? market, bool examples) : IDisposable
    {
        internal bool Examples { get; } = examples;
        private OverlayEditorPresentationSource? _owner = owner;
        private IDisposable? _market = market;
        public void Dispose()
        {
            var source = Interlocked.Exchange(ref _owner, null);
            if (source is null) return;
            source.Release(this);
            Interlocked.Exchange(ref _market, null)?.Dispose();
        }
    }

    private sealed class EmptyLease : IDisposable
    {
        internal static EmptyLease Instance { get; } = new();
        public void Dispose() { }
    }
}
