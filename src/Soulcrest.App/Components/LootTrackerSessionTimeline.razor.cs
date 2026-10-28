using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Soulcrest.App.Services;

namespace Soulcrest.App.Components;

public partial class LootTrackerSessionTimeline
{
    [Parameter, EditorRequired] public LootTrackerSession Session { get; set; } = new();
    [Parameter] public TimeSpan Elapsed { get; set; }
    [Parameter] public bool FollowEnd { get; set; }
    [Parameter] public bool IsExample { get; set; }
    [Parameter] public bool Embedded { get; set; }
    [Parameter] public bool IsPlaying { get; set; }
    [Parameter] public EventCallback TogglePlayback { get; set; }
    [Parameter] public EventCallback<TimeSpan> Seek { get; set; }
    [Parameter] public EventCallback JumpToEnd { get; set; }
    private sealed record ExactRow(TimeSpan At, TimeSpan? End, string Kind, string Label, string Value);
    private readonly LootTrackerTimelineViewport _viewport = new();
    private readonly HashSet<string> _items = new(StringComparer.Ordinal);
    private readonly HashSet<string> _resources = new(StringComparer.Ordinal);
    private IReadOnlyList<LootTrackerTimelineResources.Resource> _resourceChoices = [];
    private LootTrackerSession _timelineSession = new();
    private bool _resourcesOpen;
    private HashSet<string> _layers = new(StringComparer.Ordinal);
    private IReadOnlyList<LootTrackerTimeline.Series> _series = [];
    private IReadOnlyList<LootTrackerTimelineLayers.Layer> _available = [];
    private IReadOnlyList<LootTrackerTimeline.Event> _events = [];
    private IReadOnlyList<LootTrackerTimeline.EventGroup> _groups = [];
    private IReadOnlyDictionary<LootTrackerTimeline.EventGroup, string> _groupLabels = new Dictionary<LootTrackerTimeline.EventGroup, string>();
    private IReadOnlyList<LootTrackerTimelineLayout.Plot> _plots = [];
    private IReadOnlyList<LootTrackerTimelineLayout.LootMoment> _lootMoments = [];
    private IReadOnlyList<LootTrackerTimelineLayout.LootCluster> _lootGroups = [];
    private IReadOnlyList<LootTrackerTimelineLayout.LootItem> _lootItems = [];
    private IReadOnlyList<ExactRow> _exactRows = [];
    private IReadOnlyList<LootTrackerTimelineLayout.Curve> _overviewCurves = [];
    private LootTrackerTimeline.EventGroup? _selected;
    private LootTrackerSession? _built;
    private LootTimelineValueMode _mode;
    private LootTrackerViewMode _view;
    private string? _language, _feedback, _layoutKey;
    private bool _saving, _failed, _lootOpen, _exactOpen, _wasPlaying, _autoFollow = true, _attached, _disposed;
    private int _exactPage = 1;
    private readonly string _exactId = "loot-timeline-values-" + Guid.NewGuid().ToString("N");
    private readonly string _helpId = "loot-timeline-help-" + Guid.NewGuid().ToString("N");
    private ElementReference _track, _headGrip;
    private double _trackWidth = 1000, _trackLeft;
    private readonly record struct TrackBounds(double Width, double Left);
    private sealed class PointerGesture(long generation, long pointer, bool head, double x)
    {
        public long Generation { get; } = generation;
        public long Pointer { get; } = pointer;
        public bool Head { get; } = head;
        public bool Active { get; set; } = true;
        public bool Canceled { get; set; }
        public bool Dragged { get; set; }
        public double DownX { get; } = x;
        public double LastX { get; set; } = x;
        public double X { get; set; } = x;
        public Task<TrackBounds> Measurement { get; set; } = Task.FromResult(new TrackBounds(1000, 0));
    }
    private PointerGesture? _gesture;
    private long _pointerGeneration;
    private DotNetObjectReference<LootTrackerSessionTimeline>? _reference;
    private IJSRuntime? JavaScript => Services.GetService<IJSRuntime>();
    private CultureInfo Culture => CultureInfo.GetCultureInfo(UiText.Language == "de" ? "de-DE" : "en-US");
    private int ExactPages => Math.Max(1, (_exactRows.Count + 24) / 25);
    private int MaximumIcons => Math.Max(1, Math.Min(8, (int)(_trackWidth / 31) - 1));
    private string MaximumSeconds => N(Math.Max(.1, Session.Elapsed.TotalSeconds));
    private string CurrentSeconds => N(Math.Clamp(Elapsed.TotalSeconds, 0, Math.Max(0, Session.Elapsed.TotalSeconds)));
    private bool On(string key) => _layers.Contains(key);
    private static string N(double value) => LootTrackerTimeline.N(value);
    private string X(TimeSpan at) => N(_viewport.Fraction(at) * 1000);
    private double FullFraction(TimeSpan at) => LootTrackerTimeline.Fraction(at, Session.Elapsed);
    private string Number(long? value) => value?.ToString("N0", Culture) ?? "—";
    private static string Duration(TimeSpan value) => value.TotalHours >= 1 ? $"{(int)value.TotalHours}:{value.Minutes:00}:{value.Seconds:00}" : $"{(int)value.TotalMinutes:00}:{value.Seconds:00}";
    private static string ExactTime(TimeSpan value) => ((long)Math.Max(0, value.TotalHours)).ToString("00", CultureInfo.InvariantCulture) + value.ToString(@"\:mm\:ss\.fff", CultureInfo.InvariantCulture);
    private string TickTime(TimeSpan value) => _viewport.Window.TotalSeconds < 10
        ? (value.TotalHours >= 1 ? ((long)value.TotalHours).ToString(CultureInfo.InvariantCulture) + ":" + value.ToString(@"mm\:ss\.f", CultureInfo.InvariantCulture)
            : value.ToString(@"m\:ss\.f", CultureInfo.InvariantCulture)) : Duration(value);
    private string TickStyle(TimeSpan at)
    {
        var fraction = _viewport.Fraction(at);
        return "left:" + N(fraction * 100) + "%;transform:translateX(" + (fraction < .02 ? "0" : fraction > .98 ? "-100%" : "-50%") + ")";
    }
    private string EventLeft(TimeSpan at) => "left:clamp(0px,calc(" + N(_viewport.Fraction(at) * 100) + "% - 12px),calc(100% - 24px))";
    private string LootLeft(LootTrackerTimelineLayout.LootCluster cluster)
    {
        var width = (Math.Min(cluster.Items.Count, MaximumIcons) + (cluster.Items.Count > MaximumIcons ? 1 : 0)) * 31 - 5;
        return "left:clamp(0px,calc(" + N(_viewport.Fraction(cluster.Elapsed) * 100) + "% - 13px),calc(100% - " + width + "px))";
    }
    private static string AxisLabel(LootTrackerTimelineLayout.Curve curve) => curve.Series.Key switch
    { "ap" => "AP", "rp" => "RP", "kina" => "Kina", _ => UiText.T("Menge") };
    private string ScaleRange(LootTrackerTimelineLayout.Curve curve) => curve.HasValues
        ? Number(curve.ScaleMinimum) + " – " + Number(curve.ScaleMaximum) : UiText.T("Unbekannt");
    private string ScaleTitle(LootTrackerTimelineLayout.Curve curve) => curve.HasValues
        ? UiText.F("Eigene Skala: {0} bis {1}", Number(curve.ScaleMinimum), Number(curve.ScaleMaximum)) : UiText.T("Unbekannt");
    private string BarTitle(LootTrackerTimelineLayout.Curve curve, LootTrackerTimelineLayout.Bar bar) =>
        UiText.T(curve.Series.Label) + " · " + Number(bar.Value) + " · " + ExactTime(bar.From) + " – " + ExactTime(bar.To);
    private static string Glyph(string lane) => lane switch { "kill" => "⚔", "death" or "failed" => "×", "rank" => "↑", "gather" => "✦", _ => "◇" };

    protected override void OnParametersSet()
    {
        var view = LootTrackerSessionProjection.View(Session);
        var changedSession = _built?.Id != Session.Id || _view != view;
        _viewport.UpdateDuration(Session.Elapsed, changedSession);
        if (changedSession) { InvalidateGesture(); _selected = null; _autoFollow = true; _lootOpen = false; _resourcesOpen = false; _exactPage = 1; }
        if (FollowEnd || _selected is { } selected && !selected.Events.Any(row => row.Elapsed == Elapsed)) _selected = null;
        if (!ReferenceEquals(_built, Session) || _mode != Settings.Current.LootTimelineValueMode || _language != UiText.Language || changedSession)
        {
            if (_language != UiText.Language) _selected = null;
            _built = Session; _mode = Settings.Current.LootTimelineValueMode; _language = UiText.Language; _view = view;
            var allResources = _resourceChoices.All(row => _resources.Contains(row.Key));
            _resourceChoices = LootTrackerTimelineResources.Choices(Session);
            if (changedSession || allResources) { _resources.Clear(); _resources.UnionWith(_resourceChoices.Select(row => row.Key)); }
            RebuildTimelineData();
            _lootItems = (Session.Loot.IsDefaultOrEmpty
                    ? LootTrackerTimelineLayout.LootMoments(Session).SelectMany(row => row.Items)
                    : Session.Loot.Select(row => new LootTrackerTimelineLayout.LootItem(LootTrackerTimelineLayout.CanonicalKey(row.Id), row.Name,
                        row.Quantity, MonsterLootIconCatalog.Default.DropAsset(row.Id))))
                .GroupBy(row => row.Key, StringComparer.Ordinal)
                .Select(group => group.First() with { Quantity = group.Sum(row => row.Quantity) })
                .OrderBy(row => row.Name, StringComparer.Create(Culture, false)).ToArray();
            if (changedSession) { _items.Clear(); _items.UnionWith(_lootItems.Select(row => row.Key)); }
        }
        if (changedSession || !IsExample) _layers = Settings.GetLootTimelineLayers(view).ToHashSet(StringComparer.Ordinal);
        if (IsPlaying && !_wasPlaying) _autoFollow = true;
        if (IsPlaying && _autoFollow) _viewport.EnsureVisible(Elapsed);
        _wasPlaying = IsPlaying;
        RefreshLayout();
    }

    private void RebuildTimelineData()
    {
        _timelineSession = LootTrackerTimelineResources.Project(Session, _resources);
        _series = LootTrackerTimeline.Build(_timelineSession, _mode);
        _available = LootTrackerTimelineLayers.Available(_timelineSession, _series);
        _events = LootTrackerTimeline.Events(_timelineSession).Select(row => row.Lane == "failure" ? row with { Lane = "failed" } : row).ToArray();
        _lootMoments = LootTrackerTimelineLayout.LootMoments(_timelineSession);
        var overview = new LootTrackerTimelineViewport(); overview.UpdateDuration(Session.Elapsed);
        _overviewCurves = LootTrackerTimelineLayout.Plots(_series, overview).SelectMany(plot => plot.Curves).ToArray();
        _layoutKey = null;
    }
    private static string ResourceLabel(LootTrackerTimelineResources.Resource resource) => resource.Key == "unknown"
        ? UiText.T("Unbekannte Ressource") : !string.IsNullOrWhiteSpace(resource.Name) ? resource.Name
            : UiText.F("Ressource #{0}", resource.GatherableId);
    private void ToggleResource(string key, bool selected)
    {
        if (selected) _resources.Add(key); else _resources.Remove(key);
        _selected = null; _exactPage = 1; RebuildTimelineData(); RefreshLayout();
    }

    private void RefreshLayout()
    {
        var key = _viewport.From.Ticks + ":" + _viewport.To.Ticks + ":" + N(_trackWidth) + ":" +
            string.Join(",", _layers.Order(StringComparer.Ordinal)) + ":" + string.Join(",", _items.Order(StringComparer.Ordinal));
        if (_layoutKey == key) return;
        _layoutKey = key;
        _plots = LootTrackerTimelineLayout.Plots(_series.Where(row => On(row.Key)).ToArray(), _viewport);
        _groups = LootTrackerTimelineLayout.ClusterEvents(_events.Where(row => On(row.Lane)), _viewport, _trackWidth);
        _groupLabels = _groups.ToDictionary(row => row, row => string.Join(" · ", row.Events.Select(item => ExactTime(item.Elapsed) + " " + item.Label)));
        _lootGroups = On("loot") ? LootTrackerTimelineLayout.ClusterLoot(_lootMoments, _viewport, _trackWidth, _items) : [];
        _exactRows = ExactRows(); _exactPage = Math.Clamp(_exactPage, 1, ExactPages);
    }
    private void ChangedView() { _autoFollow = false; _exactPage = 1; _selected = null; RefreshLayout(); }
    private void Zoom(double factor) { _viewport.ZoomBy(factor); ChangedView(); }
    private void Pan(double windows) { _viewport.Pan(windows); ChangedView(); }
    private void ResetView() { _viewport.Reset(); ChangedView(); }
    private void Wheel(WheelEventArgs args) { if (args.ShiftKey && !args.CtrlKey && !args.AltKey && !args.MetaKey && args.DeltaY != 0) Zoom(args.DeltaY < 0 ? 1.25 : .8); }
    private void ToggleExact() { _exactOpen = !_exactOpen; _exactPage = 1; }
    private void ToggleItem(string key, bool selected)
    {
        if (selected) _items.Add(key); else _items.Remove(key);
        _exactPage = 1; _layoutKey = null; RefreshLayout();
    }
    private async Task ToggleLayer(string key, bool selected)
    {
        if (_saving) return;
        var next = new HashSet<string>(_layers, StringComparer.Ordinal);
        if (selected) next.Add(key); else next.Remove(key);
        _saving = true; _failed = false;
        try
        {
            if (!IsExample) await Task.Run(() => Settings.SetLootTimelineLayers(_view, next));
            _layers = next; _layoutKey = null; _exactPage = 1; RefreshLayout();
            _feedback = IsExample ? null : "Timeline-Einstellung gespeichert.";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { _failed = true; _feedback = "Timeline-Einstellung konnte nicht gespeichert werden."; }
        finally { _saving = false; }
    }
    private async Task ChangeMode(ChangeEventArgs args)
    {
        if (IsExample || _saving || !Enum.TryParse<LootTimelineValueMode>(args.Value?.ToString(), out var mode) || !Enum.IsDefined(mode)) return;
        _saving = true; _failed = false;
        try { await Task.Run(() => Settings.SetLootTimelineValueMode(mode)); _feedback = "Timeline-Einstellung gespeichert."; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { _failed = true; _feedback = "Timeline-Einstellung konnte nicht gespeichert werden."; }
        finally { _saving = false; OnParametersSet(); }
    }
    private Task Scrub(ChangeEventArgs args)
    {
        return double.TryParse(args.Value?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && double.IsFinite(seconds)
            ? SeekAt(TimeSpan.FromSeconds(Math.Clamp(seconds, 0, Math.Max(0, Session.Elapsed.TotalSeconds)))) : Task.CompletedTask;
    }
    private Task SeekAt(TimeSpan at) { _selected = null; return Seek.InvokeAsync(at); }
    private async Task JumpEnd() { _selected = null; await JumpToEnd.InvokeAsync(); }
    private async Task Select(LootTrackerTimeline.EventGroup group) { _selected = group; await Seek.InvokeAsync(group.Elapsed); }
    private Task SelectLoot(LootTrackerTimelineLayout.LootCluster cluster)
    {
        var ids = cluster.Moments.Select(row => row.Id).ToHashSet(StringComparer.Ordinal);
        var events = _events.Where(row => ids.Contains(row.Id)).ToArray();
        return events.Length == 0 ? Task.CompletedTask : Select(new("loot", cluster.Elapsed, events));
    }
    private string GroupLabel(LootTrackerTimeline.EventGroup group) => _groupLabels[group];
    private Task TrackKeyDown(KeyboardEventArgs args)
    {
        if (args.AltKey || args.CtrlKey || args.MetaKey) return Task.CompletedTask;
        switch (args.Key)
        {
            case "+": case "=": Zoom(1.25); break;
            case "-": Zoom(.8); break;
            case "ArrowLeft": Pan(-.25); break;
            case "ArrowRight": Pan(.25); break;
            case "Home": ResetView(); break;
            case " ": case "Spacebar": return TogglePlayback.InvokeAsync();
        }
        return Task.CompletedTask;
    }
    private Task HeadKeyDown(KeyboardEventArgs args)
    {
        if (args.AltKey || args.CtrlKey || args.MetaKey) return Task.CompletedTask;
        return args.Key switch
        {
            "ArrowLeft" => SeekAt(TimeSpan.FromTicks(Math.Max(0, Elapsed.Ticks - TimeSpan.TicksPerSecond))),
            "ArrowRight" => SeekAt(TimeSpan.FromTicks(Math.Min(Math.Max(0, Session.Elapsed.Ticks), Elapsed.Ticks + Math.Min(TimeSpan.TicksPerSecond, TimeSpan.MaxValue.Ticks - Elapsed.Ticks)))),
            "Home" => SeekAt(TimeSpan.Zero), "End" => SeekAt(Session.Elapsed),
            " " or "Spacebar" => TogglePlayback.InvokeAsync(), _ => Task.CompletedTask
        };
    }
    private bool CurrentGesture(PointerGesture gesture) => !_disposed && ReferenceEquals(_gesture, gesture) && !gesture.Canceled;
    private PointerGesture? ActiveGesture(PointerEventArgs args, bool head) => _gesture is { Active: true } gesture
        && gesture.Pointer == args.PointerId && gesture.Head == head && CurrentGesture(gesture) ? gesture : null;
    private void InvalidateGesture()
    {
        if (_gesture is { } gesture) { gesture.Active = false; gesture.Canceled = true; }
        _gesture = null;
    }
    private async Task PointerDown(PointerEventArgs args, bool head)
    {
        if (_disposed || args.Button != 0 || _gesture?.Active == true) return;
        var gesture = new PointerGesture(++_pointerGeneration, args.PointerId, head, args.ClientX);
        _gesture = gesture; // Register before the first JS await: Up/Cancel may run while measuring.
        gesture.Measurement = MeasureGesture(gesture);
        await gesture.Measurement;
        if (!CurrentGesture(gesture) || !gesture.Active) return;
        await JsCall("capture", head ? _headGrip : _track, gesture.Pointer, gesture.Generation);
        if (!CurrentGesture(gesture) || !gesture.Active)
            await JsCall("release", head ? _headGrip : _track, gesture.Pointer, gesture.Generation);
    }
    private async Task<TrackBounds> MeasureGesture(PointerGesture gesture)
    {
        var bounds = await ReadBounds();
        if (CurrentGesture(gesture)) await TimelineResized(bounds.Width, bounds.Left);
        return bounds;
    }
    private async Task PointerMove(PointerEventArgs args, bool head)
    {
        if (ActiveGesture(args, head) is not { } gesture) return;
        gesture.X = args.ClientX;
        if (!head && !gesture.Dragged && Math.Abs(gesture.X - gesture.DownX) < 4) return;
        gesture.Dragged = true;
        var bounds = await gesture.Measurement;
        if (!CurrentGesture(gesture) || !gesture.Active) return;
        if (head) await SeekAt(_viewport.At((gesture.X - bounds.Left) / bounds.Width));
        else PanGesture(gesture, bounds);
    }
    private void PanGesture(PointerGesture gesture, TrackBounds bounds)
    {
        if (gesture.X == gesture.LastX) return;
        _viewport.PanPixels(gesture.LastX - gesture.X, bounds.Width); gesture.LastX = gesture.X; ChangedView();
    }
    private async Task PointerUp(PointerEventArgs args, bool head)
    {
        if (ActiveGesture(args, head) is not { } gesture) return;
        gesture.X = args.ClientX;
        gesture.Dragged |= Math.Abs(gesture.X - gesture.DownX) >= 4;
        gesture.Active = false; // Never let a late Down continuation reactivate this gesture.
        await JsCall("release", head ? _headGrip : _track, gesture.Pointer, gesture.Generation);
        var bounds = await gesture.Measurement;
        if (!CurrentGesture(gesture)) return;
        if (head || !gesture.Dragged) await SeekAt(_viewport.At((gesture.X - bounds.Left) / bounds.Width));
        else PanGesture(gesture, bounds);
    }
    private async Task PointerCancel(PointerEventArgs args, bool head, bool lostCapture = false)
    {
        if (_gesture is not { } gesture || gesture.Head != head || gesture.Pointer != args.PointerId
            || lostCapture && !gesture.Active) return; // Releasing after Up also raises lostpointercapture.
        gesture.Active = false; gesture.Canceled = true;
        if (!lostCapture) await JsCall("release", head ? _headGrip : _track, gesture.Pointer, gesture.Generation);
    }
    private Task TrackDown(PointerEventArgs args) => PointerDown(args, false);
    private Task TrackMove(PointerEventArgs args) => PointerMove(args, false);
    private Task TrackUp(PointerEventArgs args) => PointerUp(args, false);
    private Task TrackCancel(PointerEventArgs args) => PointerCancel(args, false);
    private Task TrackLostCapture(PointerEventArgs args) => PointerCancel(args, false, true);
    private Task HeadDown(PointerEventArgs args) => PointerDown(args, true);
    private Task HeadMove(PointerEventArgs args) => PointerMove(args, true);
    private Task HeadUp(PointerEventArgs args) => PointerUp(args, true);
    private Task HeadCancel(PointerEventArgs args) => PointerCancel(args, true);
    private Task HeadLostCapture(PointerEventArgs args) => PointerCancel(args, true, true);

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_attached || JavaScript is null || _disposed) return;
        try
        {
            _reference ??= DotNetObjectReference.Create(this);
            await JavaScript.InvokeVoidAsync("soulcrestLootTimeline.attach", _track, _reference);
            _attached = true; await Measure();
        }
        catch (JSException) { }
    }
    [JSInvokable] public Task TimelineResized(double width, double left) => InvokeAsync(() =>
    {
        if (_disposed || !double.IsFinite(width) || width <= 0 || !double.IsFinite(left)) return;
        _trackLeft = left;
        if (Math.Abs(width - _trackWidth) < 1) return;
        _trackWidth = width; RefreshLayout(); StateHasChanged();
    });
    private async Task Measure()
    {
        var bounds = await ReadBounds();
        if (!_disposed) await TimelineResized(bounds.Width, bounds.Left);
    }
    private async Task<TrackBounds> ReadBounds()
    {
        var fallback = new TrackBounds(_trackWidth, _trackLeft);
        if (_disposed) return fallback;
        try
        {
            if (JavaScript is not { } js) return fallback;
            var bounds = await js.InvokeAsync<double[]>("soulcrestLootTimeline.bounds", _track);
            if (bounds is { Length: 2 } && double.IsFinite(bounds[0]) && bounds[0] > 0 && double.IsFinite(bounds[1]))
                return new(bounds[0], bounds[1]);
        }
        catch (Exception error) when (error is JSException or ObjectDisposedException or InvalidOperationException) { }
        return fallback;
    }
    private async Task JsCall(string method, ElementReference element, long pointer, long generation)
    {
        try { if (JavaScript is { } js) await js.InvokeVoidAsync("soulcrestLootTimeline." + method, element, pointer, generation); }
        catch (Exception error) when (error is JSException or ObjectDisposedException or InvalidOperationException) { }
    }
    public async ValueTask DisposeAsync()
    {
        _disposed = true; InvalidateGesture();
        if (_attached && JavaScript is { } js)
        {
            try { await js.InvokeVoidAsync("soulcrestLootTimeline.detach", _track); }
            catch (Exception error) when (error is JSException or ObjectDisposedException or InvalidOperationException) { }
        }
        _reference?.Dispose();
    }

    private IReadOnlyList<ExactRow> ExactRows()
    {
        var rows = new List<ExactRow>();
        if (!_timelineSession.TimelineEvents.IsDefaultOrEmpty)
        {
            foreach (var fact in _timelineSession.TimelineEvents.Where(row => row.Elapsed >= _viewport.From && row.Elapsed <= _viewport.To))
            {
                var key = fact.Kind switch
                {
                    LootTrackerTimelineEventKind.AbyssPoints => "ap", LootTrackerTimelineEventKind.RankPoints => "rp",
                    LootTrackerTimelineEventKind.KinaBound or LootTrackerTimelineEventKind.KinaUnbound => "kina", _ => null
                };
                if (key is not null && On(key)) rows.Add(new(fact.Elapsed, null, "Punkte / Einnahmen", UiText.T(fact.Kind switch
                    { LootTrackerTimelineEventKind.KinaBound => "Gebundene Kina", LootTrackerTimelineEventKind.KinaUnbound => "Ungebundene Kina",
                        _ => _series.FirstOrDefault(row => row.Key == key)?.Label ?? key }),
                    Number(fact.Amount) + " " + (key == "ap" ? "AP" : key == "rp" ? "RP" : "Kina")));
                if (fact.Kind == LootTrackerTimelineEventKind.Availability && fact.Metric is { } metric)
                {
                    var source = metric switch { LootTrackerTimelineMetric.AbyssPoints => "ap", LootTrackerTimelineMetric.RankPoints => "rp", _ => "kina" };
                    if (On(source)) rows.Add(new(fact.Elapsed, null, "Quellenstatus", UiText.T(metric switch
                        { LootTrackerTimelineMetric.KinaBound => "Gebundene Kina", LootTrackerTimelineMetric.KinaUnbound => "Ungebundene Kina",
                            _ => _series.FirstOrDefault(row => row.Key == source)?.Label ?? source }),
                        UiText.T(fact.Available == true ? "Bestätigt" : "Unbekannt")));
                }
            }
        }
        else if (Session.TimelineEvents.IsDefaultOrEmpty && !Session.MinuteIncome.IsDefaultOrEmpty)
        {
            foreach (var bin in Session.MinuteIncome.Where(row => row.Minute >= 0))
            {
                var at = TimeSpan.FromMinutes(bin.Minute); if (at > _viewport.To || at > Session.Elapsed) continue;
                var end = TimeSpan.FromTicks(Math.Min(Session.Elapsed.Ticks, at.Add(TimeSpan.FromMinutes(1)).Ticks));
                if (end < _viewport.From) continue;
                foreach (var series in _series.Where(row => On(row.Key)))
                {
                    long? amount = series.Key switch { "ap" => bin.AbyssPoints, "rp" => bin.RankPointsGained is { } gained && bin.RankPointsLost is { } lost ? gained - lost : null, "kina" => Session.KinaBound is null ? bin.KinaUnbound : bin.KinaBound is { } bound && bin.KinaUnbound is { } unbound ? bound + unbound : null, _ => null };
                    if (series.Key is "ap" or "rp" or "kina") rows.Add(new(at, end, "Minutenaggregat", UiText.T(series.Label), Number(amount)));
                }
            }
        }
        foreach (var row in _events.Where(row => On(row.Lane) && row.Lane != "loot" && row.Elapsed >= _viewport.From && row.Elapsed <= _viewport.To))
            rows.Add(new(row.Elapsed, null, "Ereignis", row.Label, "1"));
        if (On("loot"))
            foreach (var moment in _lootMoments.Where(row => row.Elapsed >= _viewport.From && row.Elapsed <= _viewport.To))
                foreach (var item in moment.Items.Where(item => _items.Contains(item.Key)))
                    rows.Add(new(moment.Elapsed, null, "Beute", item.Name, Number(item.Quantity)));
        return rows.OrderBy(row => row.At).ThenBy(row => row.Label, StringComparer.Create(Culture, false)).ToArray();
    }
}
