using System.Text.Json;
using Soulcrest.Core.Daevanion;

namespace Soulcrest.App.Services;

public sealed record DaevanionSavedRoute
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; init; } = "";
    public string ClassId { get; init; } = "";
    public DaevanionOptimizationRequest Request { get; init; } = new();
    public DaevanionOptimizationResult Result { get; init; } = new();
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public string CatalogFingerprint { get; init; } = "";
    public string RulesVersion { get; init; } = "";
}

/// <summary>Manual observations, not an inferred or universal Combat Score formula.</summary>
public sealed record DaevanionCombatScoreMeasurement
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string ClassId { get; init; } = "";
    public int BoardId { get; init; }
    public int NodeId { get; init; }
    public string GameBuild { get; init; } = "";
    public int CharacterLevel { get; init; }
    public long Before { get; init; }
    public long After { get; init; }
    public long? Restored { get; init; }
    public long? RepeatedAfter { get; init; }
    public int? SkillLevelBefore { get; init; }
    public bool? SkillEquipped { get; init; }
    public string Context { get; init; } = "";
    public DateTimeOffset RecordedAt { get; init; }
    public long Delta => After - Before;
}

public sealed record DaevanionPlanSnapshot(
    DaevanionOptimizationRequest Draft,
    DaevanionOptimizationResult? Result,
    DaevanionOptimizationProgress? Progress,
    bool IsBusy,
    string? Status,
    IReadOnlyList<DaevanionSavedRoute> Routes,
    IReadOnlyList<DaevanionCombatScoreMeasurement> Measurements)
{
    public string? ActiveRouteId { get; init; }
    public OwnSkillStateSnapshot? DetectedSkills { get; init; }
    public int ManualSkillOverrideCount { get; init; }
    public bool CanApplyDetectedSkills => DetectedSkills is { IsKnown: true, IsFresh: true } skills
        && skills.ClassId == Draft.ClassId;
    public CharacterCombatSnapshot? Character { get; init; }
    public IReadOnlyList<string> MissingRequirements { get; init; } = [];
    public bool LiveInputsRequired { get; init; }
    public bool CanCalculate => !IsBusy && (!LiveInputsRequired || MissingRequirements.Count == 0);
}

/// <summary>
/// Offline planning from the profile-bound character source; only user preferences are persisted in live drafts.
/// User decisions 2026-10-31/2026-11-02: frozen named routes and one active overlay route per class.
/// </summary>
public sealed class DaevanionPlanService : IDisposable
{
    private const int RouteLimit = 128;
    private const int MeasurementLimit = 4096;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly object _gate = new();
    private readonly string _path;
    private readonly IDaevanionOptimizer _optimizer;
    private readonly TimeProvider _time;
    private readonly int _maximumStoreBytes;
    private readonly SemaphoreSlim _solveGate = new(1, 1);
    private readonly Dictionary<DaevanionSavedRoute, bool> _compatibility = new(ReferenceEqualityComparer.Instance);
    private Store _store;
    private DaevanionOptimizationResult? _result;
    private DaevanionOptimizationProgress? _progress;
    private CancellationTokenSource? _cancellation;
    private long _revision;
    private bool _isBusy, _disposed, _readOnly;
    private string? _status;
    private OwnSkillStateService? _skillSource;
    private OwnSkillStateSnapshot? _detectedSkills;
    private CharacterCombatStateService? _characterSource;
    private CharacterCombatSnapshot? _character;

    private sealed record Store
    {
        public Store() { }
        public int Version { get; init; } = 1;
        public string SelectedClassId { get; init; } = "";
        public Dictionary<string, DaevanionOptimizationRequest> Drafts { get; init; } = [];
        public List<DaevanionSavedRoute> Routes { get; init; } = [];
        public Dictionary<string, string> ActiveRoutes { get; init; } = [];
        public List<DaevanionCombatScoreMeasurement> Measurements { get; init; } = [];
        public Dictionary<string, List<int>> ManualSkillOverrides { get; init; } = [];
    }

    public DaevanionPlanService(IDaevanionOptimizer optimizer, OwnSkillStateService? skills = null,
        CharacterCombatStateService? character = null)
        : this(Path.Combine(AppPaths.DataDirectory, "daevanion-plans.json"), optimizer)
    {
        if (skills is not null) AttachSkillSource(skills);
        if (character is not null) AttachCharacterSource(character);
    }

    internal DaevanionPlanService(string path, IDaevanionOptimizer optimizer, TimeProvider? time = null,
        int maximumStoreBytes = 16 * 1024 * 1024)
    {
        if (maximumStoreBytes < 1024) throw new ArgumentOutOfRangeException(nameof(maximumStoreBytes));
        _path = Path.GetFullPath(path);
        _optimizer = optimizer;
        _time = time ?? TimeProvider.System;
        _maximumStoreBytes = maximumStoreBytes;
        _store = new() { SelectedClassId = DaevanionCatalog.Default.Classes[0].Id };
        if (!File.Exists(_path)) return;
        try
        {
            if (new FileInfo(_path).Length > _maximumStoreBytes) throw new InvalidDataException("Planning store too large.");
            using var source = File.OpenRead(_path);
            var saved = JsonSerializer.Deserialize<Store>(source, JsonOptions) ?? throw new InvalidDataException("Missing planning store.");
            if (saved.Version != 1 || saved.Drafts is null || saved.Routes is null || saved.ActiveRoutes is null
                || saved.Measurements is null || saved.ManualSkillOverrides is null
                || saved.Routes.Count > RouteLimit || saved.Measurements.Count > MeasurementLimit)
                throw new InvalidDataException("Unsupported planning store.");
            if (!KnownClass(saved.SelectedClassId)) throw new InvalidDataException("Unknown planning class.");
            var drafts = new Dictionary<string, DaevanionOptimizationRequest>(StringComparer.Ordinal);
            foreach (var (classId, draft) in saved.Drafts)
            {
                if (draft is null || classId != draft.ClassId || !KnownClass(classId)) throw new InvalidDataException("Invalid planning draft.");
                drafts.Add(classId, Reconcile(draft));
            }
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var routes = new List<DaevanionSavedRoute>();
            foreach (var route in saved.Routes)
            {
                // Keep old catalog versions for explicit recalculation; never render their result.
                if (route is null || !ValidId(route.Id) || !ids.Add(route.Id) || !KnownClass(route.ClassId)
                    || string.IsNullOrWhiteSpace(route.Name) || route.Name.Length > 100 || route.Request is null
                    || route.Result is null || route.Request.ClassId != route.ClassId || route.Result.ClassId != route.ClassId
                    || route.Result.Boards is null || route.CatalogFingerprint is null || route.RulesVersion is null
                    || !CanReconcilePreferences(route.Request))
                    throw new InvalidDataException("Invalid saved route.");
                routes.Add(Freeze(route));
            }
            if (saved.ActiveRoutes.Any(pair => !KnownClass(pair.Key)
                || !routes.Any(route => route.Id == pair.Value && route.ClassId == pair.Key)))
                throw new InvalidDataException("Invalid active route.");
            if (saved.Measurements.Any(item => !ValidMeasurement(item))
                || saved.Measurements.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != saved.Measurements.Count)
                throw new InvalidDataException("Invalid measurements.");
            if (saved.ManualSkillOverrides.Any(pair => !KnownClass(pair.Key) || pair.Value is null
                || pair.Value.Count > 22 || pair.Value.Distinct().Count() != pair.Value.Count
                || pair.Value.Any(id => DaevanionCatalog.Default.GetSkill(id)?.ClassId != pair.Key)))
                throw new InvalidDataException("Invalid manual skill inputs.");
            // Automatic observations are RAM evidence, never a fresh source after restart.
            // Retain explicit manual inputs and named routes; clear only automatic draft levels.
            foreach (var classId in drafts.Keys.ToArray())
                drafts[classId] = Freeze(WithDetectedSkills(drafts[classId], null,
                    saved.ManualSkillOverrides.GetValueOrDefault(classId, []).ToHashSet()));
            _store = saved with { Drafts = drafts, Routes = routes, ActiveRoutes = new(saved.ActiveRoutes, StringComparer.Ordinal), Measurements = [.. saved.Measurements] };
        }
        catch (Exception error) when (StorageError(error) || error is ArgumentException or NullReferenceException)
        {
            // Preserve unsupported/unreadable bytes. No fresh empty write may replace this file.
            _readOnly = true;
            _status = "Daevanion-Pläne konnten nicht geladen werden. Die Datei bleibt unverändert.";
            System.Diagnostics.Trace.TraceWarning("Daevanion planning load: {0}", error.GetType().Name);
        }
    }

    public event Action? Changed;

    public DaevanionPlanSnapshot Snapshot
    {
        get
        {
            lock (_gate)
                return new(CurrentDraft(), _result, _progress, _isBusy, _status,
                    Array.AsReadOnly(_store.Routes.ToArray()), Array.AsReadOnly(_store.Measurements.ToArray()))
                    {
                        ActiveRouteId = GetActiveRoute(EffectiveClassId)?.Id,
                        DetectedSkills = _detectedSkills,
                        ManualSkillOverrideCount = _store.ManualSkillOverrides.GetValueOrDefault(_store.SelectedClassId)?.Count ?? 0,
                        Character = _character,
                        LiveInputsRequired = _characterSource is not null,
                        MissingRequirements = _characterSource is null ? [] : LiveResolution().MissingRequirements
                    };
        }
    }

    public bool SetClass(string classId)
    {
        if (!KnownClass(classId)) return false;
        CancellationTokenSource? stop = null;
        bool saved;
        lock (_gate)
        {
            if (_disposed) return false;
            if (_characterSource is not null && _character?.ClassId is { } ownClass && classId != ownClass) return false;
            if (_store.SelectedClassId == classId) return true;
            saved = Commit(_store with { SelectedClassId = classId });
            if (saved) stop = Invalidate();
        }
        Stop(stop); Raise(); return saved;
    }

    public bool UpdateDraft(DaevanionOptimizationRequest request)
    {
        if (!ValidRequest(request)) return false;
        request = Freeze(request);
        CancellationTokenSource? stop = null;
        bool saved;
        lock (_gate)
        {
            if (_disposed) return false;
            var current = CurrentDraft();
            if (_characterSource is not null)
            {
                if (request.ClassId != EffectiveClassId) return false;
                if (DaevanionPoolSettings.Equivalent(current, request)) return true;
                var stored = _store.Drafts.GetValueOrDefault(request.ClassId) ?? DaevanionPlanningDefaults.CreateRequest(request.ClassId);
                var savedPreferences = UserPreferencesOnly(request, stored);
                var preferences = new Dictionary<string, DaevanionOptimizationRequest>(_store.Drafts, StringComparer.Ordinal)
                    { [request.ClassId] = Freeze(savedPreferences) };
                saved = Commit(_store with { SelectedClassId = request.ClassId, Drafts = preferences });
                if (saved) stop = Invalidate();
            }
            else
            {
            if (_store.SelectedClassId == request.ClassId && DaevanionPoolSettings.Equivalent(current, request)) return true;
            var drafts = new Dictionary<string, DaevanionOptimizationRequest>(_store.Drafts, StringComparer.Ordinal) { [request.ClassId] = request };
            var overrides = CopySkillOverrides();
            var previous = current.ClassId == request.ClassId ? current : _store.Drafts.GetValueOrDefault(request.ClassId)
                ?? DaevanionPlanningDefaults.CreateRequest(request.ClassId);
            var previousSkills = (previous.CombatContext?.Skills ?? []).ToDictionary(item => item.SkillId);
            var manual = overrides.GetValueOrDefault(request.ClassId, []).ToHashSet();
            foreach (var skill in request.CombatContext?.Skills ?? [])
            {
                previousSkills.TryGetValue(skill.SkillId, out var old);
                if (old?.CurrentLevel != skill.CurrentLevel || old?.CurrentBoardBonus != skill.CurrentBoardBonus)
                    manual.Add(skill.SkillId);
            }
            overrides[request.ClassId] = manual.Order().ToList();
            saved = Commit(_store with { SelectedClassId = request.ClassId, Drafts = drafts, ManualSkillOverrides = overrides });
            if (saved) stop = Invalidate();
            }
        }
        Stop(stop); Raise(); return saved;
    }

    internal void AttachSkillSource(OwnSkillStateService source)
    {
        if (_skillSource is not null) throw new InvalidOperationException("Skill source already attached.");
        _skillSource = source;
        source.Changed += SkillSourceChanged;
        ObserveDetectedSkills(source.Snapshot);
    }

    internal void AttachCharacterSource(CharacterCombatStateService source)
    {
        if (_characterSource is not null) throw new InvalidOperationException("Character source already attached.");
        _characterSource = source;
        source.Changed += CharacterSourceChanged;
        ObserveCharacter(source.Snapshot);
    }

    private void CharacterSourceChanged()
    {
        if (_characterSource is { } source) ObserveCharacter(source.Snapshot);
    }

    internal void ObserveCharacter(CharacterCombatSnapshot character)
    {
        CancellationTokenSource? stop = null;
        lock (_gate)
        {
            if (_disposed || ReferenceEquals(_character, character)) return;
            var previous = CurrentDraft();
            var wasReady = _character is not null && LiveResolution().Ready;
            _character = character;
            var next = CurrentDraft();
            if (!DaevanionPoolSettings.Equivalent(previous, next) || wasReady != LiveResolution().Ready)
            {
                stop = Invalidate();
                _status = "Aktuelle Charakterwerte für die Planung aktualisiert.";
            }
        }
        Stop(stop); Raise();
    }

    private void SkillSourceChanged()
    {
        if (_skillSource is { } source) ObserveDetectedSkills(source.Snapshot);
    }

    internal void ObserveDetectedSkills(OwnSkillStateSnapshot detected)
    {
        CancellationTokenSource? stop = null;
        lock (_gate)
        {
            if (_disposed) return;
            var previous = CurrentDraft();
            _detectedSkills = detected;
            // Live evidence is a RAM projection, independent of persistence. A disk failure must
            // never keep stale automatic levels or an old in-flight result authoritative.
            if (!DaevanionPoolSettings.Equivalent(previous, CurrentDraft()))
            {
                stop = Invalidate();
                _status = detected is { IsKnown: true, IsFresh: true } && detected.ClassId == _store.SelectedClassId
                    ? "Erkannte Skillstufen übernommen."
                    : "Skillstufen sind nach dem Charakterwechsel oder Verbindungsverlust noch unbekannt.";
            }
        }
        Stop(stop); Raise();
    }

    /// <summary>Explicitly replaces manual level overrides with this character's fresh, qualified snapshot.</summary>
    public bool ApplyDetectedSkills()
    {
        if (_characterSource is not null) return false;
        CancellationTokenSource? stop = null;
        bool saved;
        lock (_gate)
        {
            if (_disposed || _detectedSkills is not { IsKnown: true, IsFresh: true } detected
                || detected.ClassId != _store.SelectedClassId) return false;
            var request = Freeze(WithDetectedSkills(CurrentDraft(), detected, new HashSet<int>()));
            var overrides = CopySkillOverrides(); overrides.Remove(request.ClassId);
            var drafts = new Dictionary<string, DaevanionOptimizationRequest>(_store.Drafts, StringComparer.Ordinal) { [request.ClassId] = request };
            var changed = !DaevanionPoolSettings.Equivalent(CurrentDraft(), request);
            saved = Commit(_store with { Drafts = drafts, ManualSkillOverrides = overrides }, "Erkannte Skillstufen übernommen.");
            if (saved && changed) stop = Invalidate();
        }
        Stop(stop); Raise(); return saved;
    }

    /// <summary>Returns level inputs to the automatic source, retaining targets, stats and saved routes.</summary>
    public bool ClearSkillOverrides()
    {
        if (_characterSource is not null) return false;
        CancellationTokenSource? stop = null;
        bool saved;
        lock (_gate)
        {
            if (_disposed) return false;
            var request = Freeze(WithDetectedSkills(CurrentDraft(), _detectedSkills, new HashSet<int>()));
            var overrides = CopySkillOverrides(); overrides.Remove(request.ClassId);
            var drafts = new Dictionary<string, DaevanionOptimizationRequest>(_store.Drafts, StringComparer.Ordinal) { [request.ClassId] = request };
            var changed = !DaevanionPoolSettings.Equivalent(CurrentDraft(), request);
            saved = Commit(_store with { Drafts = drafts, ManualSkillOverrides = overrides }, "Manuelle Skillstufen zurückgesetzt.");
            if (saved && changed) stop = Invalidate();
        }
        Stop(stop); Raise(); return saved;
    }

    private Dictionary<string, List<int>> CopySkillOverrides() => _store.ManualSkillOverrides
        .ToDictionary(pair => pair.Key, pair => pair.Value.ToList(), StringComparer.Ordinal);

    private static DaevanionOptimizationRequest WithDetectedSkills(DaevanionOptimizationRequest request,
        OwnSkillStateSnapshot? detected, IReadOnlySet<int> manual)
    {
        var defaults = DaevanionCombatScoreModel.CreateContext(request.ClassId);
        var context = request.CombatContext ?? defaults;
        var existing = context.Skills.ToDictionary(item => item.SkillId);
        var matches = detected is { IsKnown: true, IsFresh: true } && detected.ClassId == request.ClassId;
        var skills = defaults.Skills.Select(item =>
        {
            var skill = existing.GetValueOrDefault(item.SkillId) ?? item;
            if (manual.Contains(skill.SkillId)) return skill;
            if (matches && detected!.Skills.TryGetValue(skill.SkillId, out var observed))
                return skill with { CurrentLevel = observed.TotalLevel, CurrentBoardBonus = observed.AdditionalLevels?.Daevanion };
            return skill with { CurrentLevel = null, CurrentBoardBonus = null };
        }).ToArray();
        if (request.CombatContext is null && skills.All(item => item.CurrentLevel is null && item.CurrentBoardBonus is null)) return request;
        return request with { CombatContext = context with { Skills = skills } };
    }

    public async Task CalculateAsync()
    {
        DaevanionOptimizationRequest request = null!;
        CancellationTokenSource cancellation = null!;
        long revision = 0;
        var blocked = false;
        lock (_gate)
        {
            if (_disposed || _isBusy) return;
            if (_characterSource is not null && LiveResolution() is { Ready: false } missing)
            {
                _status = string.Join(" ", missing.MissingRequirements);
                _result = null; _progress = null;
                blocked = true;
            }
            else
            {
                request = CurrentDraft();
                cancellation = new();
                _cancellation = cancellation;
                revision = ++_revision;
                _isBusy = true; _result = null; _progress = null;
                _status = "Daevanion-Route wird berechnet …";
            }
        }
        Raise();
        if (blocked) return;
        var entered = false;
        try
        {
            await _solveGate.WaitAsync(cancellation.Token).ConfigureAwait(false);
            entered = true;
            var result = await _optimizer.OptimizeAsync(request,
                new CallbackProgress<DaevanionOptimizationProgress>(value => PublishProgress(revision, value)), cancellation.Token).ConfigureAwait(false);
            lock (_gate)
            {
                if (_disposed || revision != _revision) return;
                if (!Enum.IsDefined(result.Status)
                    || ((result.Status == DaevanionOptimizationStatus.Optimal
                        || result.Status == DaevanionOptimizationStatus.Cancelled && result.Boards.Count != 0)
                        && DaevanionPlanValidator.Validate(request, result) is not null)
                    || (result.Status is not (DaevanionOptimizationStatus.Optimal or DaevanionOptimizationStatus.Cancelled) && result.Boards.Count != 0))
                    throw new InvalidDataException("Invalid optimization result.");
                _result = Freeze(result);
                _status = result.Status switch
                {
                    DaevanionOptimizationStatus.Optimal when request.Mode == DaevanionOptimizationMode.CombatScore => "Optimale Route für die CP-Schätzung gefunden.",
                    DaevanionOptimizationStatus.Optimal => "Optimale Daevanion-Route gefunden.",
                    DaevanionOptimizationStatus.Cancelled when result.Boards.Count == 0 => "Berechnung abgebrochen. Es liegt noch keine vollständige Route vor.",
                    DaevanionOptimizationStatus.Cancelled => "Berechnung abgebrochen. Ein Zwischenstand ist nicht als optimal bestätigt.",
                    DaevanionOptimizationStatus.Infeasible => "Die Punkte reichen für mindestens einen Pool nicht für alle must-have-Nodes und ihre Verbindungen. Bitte Budget oder Pflichtauswahl anpassen.",
                    DaevanionOptimizationStatus.Unavailable when request.Mode == DaevanionOptimizationMode.Priority => "Die gewählten Skillziele benötigen bekannte aktuelle Stufen und Boardboni.",
                    DaevanionOptimizationStatus.Unavailable => "Für die CP-Schätzung fehlen noch Ausgangswerte oder Gewichte.",
                    _ => "Die Daevanion-Route konnte nicht berechnet werden."
                };
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            lock (_gate)
                if (!_disposed && revision == _revision)
                    _status = _progress is { Boards.Count: 6 }
                        ? "Berechnung abgebrochen. Ein Zwischenstand ist nicht als optimal bestätigt."
                        : "Berechnung abgebrochen. Es liegt noch keine vollständige Route vor.";
        }
        catch (Exception error)
        {
            // A failed native solver must not terminate the Blazor event loop or alter saved routes.
            lock (_gate)
                if (!_disposed && revision == _revision)
                { _result = null; _status = "Die Daevanion-Route konnte nicht berechnet werden."; }
            System.Diagnostics.Trace.TraceWarning("Daevanion optimization: {0}", error);
        }
        finally
        {
            if (entered) _solveGate.Release();
            lock (_gate)
                if (revision == _revision)
                { _isBusy = false; _cancellation = null; }
            cancellation.Dispose();
            Raise();
        }
    }

    public void Cancel()
    {
        CancellationTokenSource? stop;
        lock (_gate) stop = _cancellation;
        Stop(stop);
    }

    public bool SaveRoute(string name, string? overwriteId = null)
    {
        name = name.Trim();
        if (name.Length is < 1 or > 100) return Fail("Bitte einen Routennamen mit höchstens 100 Zeichen eingeben.");
        bool saved;
        lock (_gate)
        {
            if (_disposed) return false;
            var request = CurrentDraft();
            if (_isBusy || _result is null || _result.Boards.Count != 6
                || _result.Status is not (DaevanionOptimizationStatus.Optimal or DaevanionOptimizationStatus.Cancelled)
                || DaevanionPlanValidator.Validate(request, _result) is not null)
                return false;
            var existing = overwriteId is null ? null : _store.Routes.FirstOrDefault(route => route.Id == overwriteId && route.ClassId == request.ClassId);
            if (overwriteId is not null && existing is null) return false;
            if (existing is null && _store.Routes.Count >= RouteLimit)
            { _status = "Es können höchstens 128 Daevanion-Routen gespeichert werden."; saved = false; }
            else
            {
                var now = _time.GetUtcNow();
                var route = new DaevanionSavedRoute
                {
                    Id = existing?.Id ?? Guid.NewGuid().ToString("N"), Name = name, ClassId = request.ClassId,
                    Request = request, Result = _result, CreatedAt = existing?.CreatedAt ?? now, UpdatedAt = now,
                    CatalogFingerprint = DaevanionRuleCatalog.Default.CatalogFingerprint, RulesVersion = DaevanionRuleCatalog.Default.Version
                };
                var routes = _store.Routes.Where(item => item.Id != route.Id).Append(route).ToList();
                saved = Commit(_store with { Routes = routes }, "Daevanion-Route gespeichert.");
            }
        }
        Raise(); return saved;
    }

    public bool LoadRoute(string id)
    {
        CancellationTokenSource? stop = null;
        bool saved;
        lock (_gate)
        {
            if (_disposed) return false;
            var route = _store.Routes.FirstOrDefault(item => item.Id == id);
            if (route is null) return false;
            if (_characterSource is not null && _character?.ClassId is { } ownClass && ownClass != route.ClassId) return false;
            // Reconcile only the editable priorities; preserve the original saved route until an explicit update.
            var request = Reconcile(route.Request);
            if (_characterSource is not null)
                request = UserPreferencesOnly(request, _store.Drafts.GetValueOrDefault(route.ClassId)
                    ?? DaevanionPlanningDefaults.CreateRequest(route.ClassId));
            var drafts = new Dictionary<string, DaevanionOptimizationRequest>(_store.Drafts, StringComparer.Ordinal) { [route.ClassId] = request };
            // Loading a saved plan explicitly restores its frozen calculation inputs.
            var overrides = CopySkillOverrides();
            overrides[route.ClassId] = DaevanionCatalog.Default.Skills.Where(skill => skill.ClassId == route.ClassId)
                .Select(skill => skill.Id).Order().ToList();
            saved = Commit(_store with { SelectedClassId = route.ClassId, Drafts = drafts, ManualSkillOverrides = overrides });
            if (saved)
            {
                stop = Invalidate();
                if (_characterSource is not null) _status = "Routenpräferenzen geladen. Die Berechnung nutzt aktuelle Charakterwerte.";
                else if (Compatible(route)) { _result = route.Result; _status = "Daevanion-Route geladen."; }
                else _status = "Diese Route muss mit den aktuellen Boarddaten neu berechnet werden.";
            }
        }
        Stop(stop); Raise(); return saved;
    }

    public bool DeleteRoute(string id)
    {
        bool saved;
        lock (_gate)
        {
            if (_disposed || !_store.Routes.Any(route => route.Id == id)) return false;
            var active = _store.ActiveRoutes.Where(pair => pair.Value != id).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            saved = Commit(_store with { Routes = _store.Routes.Where(route => route.Id != id).ToList(), ActiveRoutes = active }, "Daevanion-Route gelöscht.");
        }
        Raise(); return saved;
    }

    public bool ActivateRoute(string id)
    {
        bool saved;
        lock (_gate)
        {
            if (_disposed) return false;
            var route = _store.Routes.FirstOrDefault(item => item.Id == id);
            if (route is null || !Compatible(route)) return false;
            var active = new Dictionary<string, string>(_store.ActiveRoutes, StringComparer.Ordinal) { [route.ClassId] = id };
            saved = Commit(_store with { ActiveRoutes = active }, "Daevanion-Route für das Overlay aktiviert.");
        }
        Raise(); return saved;
    }

    public bool DeactivateRoute(string classId)
    {
        bool saved;
        lock (_gate)
        {
            if (_disposed || !_store.ActiveRoutes.ContainsKey(classId)) return false;
            var active = new Dictionary<string, string>(_store.ActiveRoutes, StringComparer.Ordinal);
            active.Remove(classId);
            saved = Commit(_store with { ActiveRoutes = active }, "Daevanion-Route für das Overlay ausgeschaltet.");
        }
        Raise(); return saved;
    }

    public DaevanionSavedRoute? GetActiveRoute(string classId)
    {
        lock (_gate)
        {
            if (_disposed || !_store.ActiveRoutes.TryGetValue(classId, out var id)) return null;
            var route = _store.Routes.FirstOrDefault(item => item.Id == id && item.ClassId == classId);
            return route is not null && Compatible(route) ? route : null;
        }
    }

    public bool AddMeasurement(DaevanionCombatScoreMeasurement measurement)
    {
        if (!ValidMeasurement(measurement)) return Fail("Bitte Klasse, Knoten, Build, Level und gültige Combat-Score-Werte eintragen.");
        bool saved;
        lock (_gate)
        {
            if (_disposed) return false;
            if (_store.Measurements.Count >= MeasurementLimit || _store.Measurements.Any(item => item.Id == measurement.Id))
            { _status = "Die Messung ist bereits vorhanden oder das Limit von 4096 Messungen ist erreicht."; saved = false; }
            else
            {
                measurement = measurement with { RecordedAt = _time.GetUtcNow() };
                saved = Commit(_store with { Measurements = [.. _store.Measurements, measurement] }, "Combat-Score-Messung gespeichert.");
            }
        }
        Raise(); return saved;
    }

    public bool DeleteMeasurement(string id)
    {
        bool saved;
        lock (_gate)
        {
            if (_disposed || !_store.Measurements.Any(item => item.Id == id)) return false;
            saved = Commit(_store with { Measurements = _store.Measurements.Where(item => item.Id != id).ToList() }, "Combat-Score-Messung gelöscht.");
        }
        Raise(); return saved;
    }

    public bool IsCompatible(DaevanionSavedRoute route) { lock (_gate) return Compatible(route); }

    private void PublishProgress(long revision, DaevanionOptimizationProgress progress)
    {
        lock (_gate)
        {
            if (_disposed || !_isBusy || revision != _revision) return;
            _progress = progress with { Boards = Array.AsReadOnly(progress.Boards.Select(Freeze).ToArray()) };
        }
        Raise();
    }

    private bool Compatible(DaevanionSavedRoute route)
    {
        // Immutable saved snapshots can be checked once, rather than re-scoring 690 nodes
        // on every overlay pointer tick. Reference identity keeps explicit replacements separate.
        if (_compatibility.TryGetValue(route, out var known)) return known;
        // v2/v3 use the same board costs and adjacency. Revalidate their frozen inputs
        // against every current invariant without rewriting the stored historical result.
        var readableLegacy = route.RulesVersion is "2026-10-31-v2" or "2026-11-02-v3"
            && route.Request.CharacterLevel is null && route.Request.CombatContext?.SourceGraph is null;
        var versionKnown = route.RulesVersion == DaevanionRuleCatalog.Default.Version || readableLegacy;
        var comparable = readableLegacy ? route.Result with { RulesVersion = DaevanionRuleCatalog.Default.Version } : route.Result;
        var valid = route.CatalogFingerprint == DaevanionRuleCatalog.Default.CatalogFingerprint
            && versionKnown && route.Result.RulesVersion == route.RulesVersion && ValidRequest(route.Request)
            && route.Result.Status is DaevanionOptimizationStatus.Optimal or DaevanionOptimizationStatus.Cancelled
            && route.Result.Boards.Count == 6 && DaevanionPlanValidator.Validate(route.Request, comparable) is null;
        if (_compatibility.Count >= RouteLimit * 2) _compatibility.Clear();
        _compatibility.Add(route, valid);
        return valid;
    }

    private bool Commit(Store next, string? status = null)
    {
        if (_readOnly) { _status = "Daevanion-Pläne konnten nicht geladen werden. Die Datei bleibt unverändert."; return false; }
        try
        {
            // Never write a valid store which our own bounded loader could no longer read.
            if (JsonSerializer.SerializeToUtf8Bytes(next, JsonOptions).Length > _maximumStoreBytes)
            { _status = "Der Daevanion-Planspeicher ist voll. Bitte Routen oder Messungen löschen."; return false; }
            JsonFile.Save(_path, next); _store = next; _status = status; return true;
        }
        catch (Exception error) when (StorageError(error))
        {
            _status = "Daevanion-Pläne konnten nicht gespeichert werden.";
            System.Diagnostics.Trace.TraceWarning("Daevanion planning save: {0}", error.GetType().Name);
            return false;
        }
    }

    private CancellationTokenSource? Invalidate()
    {
        ++_revision;
        var stop = _cancellation;
        _cancellation = null; _isBusy = false; _result = null; _progress = null;
        return stop;
    }

    private DaevanionOptimizationRequest CurrentDraft()
    {
        if (_characterSource is not null) return LiveResolution().Request;
        var request = _store.Drafts.GetValueOrDefault(_store.SelectedClassId)
            ?? DaevanionPlanningDefaults.CreateRequest(_store.SelectedClassId);
        return Freeze(WithDetectedSkills(request, _detectedSkills,
            _store.ManualSkillOverrides.GetValueOrDefault(request.ClassId, []).ToHashSet()));
    }

    private string EffectiveClassId => _characterSource is not null && _character?.ClassId is { } ownClass && KnownClass(ownClass)
        ? ownClass : _store.SelectedClassId;

    private static DaevanionOptimizationRequest UserPreferencesOnly(DaevanionOptimizationRequest incoming,
        DaevanionOptimizationRequest history)
    {
        var defaults = DaevanionCombatScoreModel.CreateContext(incoming.ClassId);
        var context = history.CombatContext ?? defaults;
        var existing = context.Skills.ToDictionary(skill => skill.SkillId);
        var targets = (incoming.CombatContext?.Skills ?? []).ToDictionary(skill => skill.SkillId, skill => skill.TargetLevel);
        return incoming with
        {
            CharacterLevel = history.CharacterLevel, Budgets = history.Budgets,
            CombatContext = context with { Skills = Array.AsReadOnly(defaults.Skills.Select(skill =>
                (existing.GetValueOrDefault(skill.SkillId) ?? skill) with { TargetLevel = targets.GetValueOrDefault(skill.SkillId) }).ToArray()) }
        };
    }

    private DaevanionLiveInputResolution LiveResolution() => DaevanionLiveInputResolver.Resolve(
        _store.Drafts.GetValueOrDefault(EffectiveClassId) ?? DaevanionPlanningDefaults.CreateRequest(EffectiveClassId), _character);

    private static bool ValidRequest(DaevanionOptimizationRequest request)
    {
        if (DaevanionPoolSettings.Validate(request) is not null) return false;
        var expected = DaevanionPlanningDefaults.CreateRequest(request.ClassId).Priorities.Select(item => (item.Kind, item.Key)).ToHashSet();
        return request.Priorities.All(item => item is not null && expected.Contains((item.Kind, item.Key)))
            && request.Priorities.Select(item => (item.Kind, item.Key)).Distinct().Count() == expected.Count
            && request.Priorities.Count == expected.Count;
    }

    private static DaevanionOptimizationRequest Reconcile(DaevanionOptimizationRequest request)
    {
        if (!CanReconcilePreferences(request)) throw new InvalidDataException("Invalid planning preferences.");
        var next = DaevanionPoolSettings.Reconcile(request);
        return Freeze(ValidRequest(next) ? next : DaevanionPlanningDefaults.CreateRequest(request.ClassId));
    }

    private static bool CanReconcilePreferences(DaevanionOptimizationRequest request)
    {
        // Retired or newly added rows can be reconciled. Invalid scalar settings must
        // preserve the original store instead of resetting unrelated pool choices.
        static bool ConflictingFlags(IEnumerable<DaevanionPriority>? rows) =>
            rows?.Any(row => row is not null && row.Required && row.Ignored) == true;
        if (ConflictingFlags(request.Priorities)
            || request.PoolPreferences?.Any(item => item is not null && ConflictingFlags(item.Priorities)) == true) return false;
        var shape = request with
        {
            Priorities = [],
            PoolPreferences = request.PoolPreferences?.Select(item => item is null ? null!
                : item with { Priorities = [] }).ToArray()!
        };
        return DaevanionPoolSettings.Validate(shape) is null;
    }

    private static bool ValidMeasurement(DaevanionCombatScoreMeasurement? item)
    {
        if (item is null || !ValidId(item.Id) || !KnownClass(item.ClassId) || string.IsNullOrWhiteSpace(item.GameBuild)
            || item.GameBuild.Length > 64 || item.CharacterLevel is < 1 or > 1000 || item.Before < 0 || item.After < 0
            || item.Restored < 0 || item.RepeatedAfter < 0 || item.SkillLevelBefore < 0 || item.Context is null || item.Context.Length > 4096)
            return false;
        return DaevanionCatalog.Default.Boards.Any(board => board.Id == item.BoardId && board.ClassId == item.ClassId
            && board.Nodes.Any(node => node.Id == item.NodeId && node.Kind != DaevanionNodeKind.Start));
    }

    private static bool KnownClass(string classId) => DaevanionCatalog.Default.Classes.Any(item => item.Id == classId);
    private static bool ValidId(string id) => id is { Length: > 0 and <= 64 } && id.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
    private static bool StorageError(Exception error) => error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or NotSupportedException;
    private bool Fail(string status) { lock (_gate) { if (_disposed) return false; _status = status; } Raise(); return false; }
    private void Raise() { if (!_disposed) SafeEvent.Raise(Changed, "Daevanion-Pläne"); }
    private static void Stop(CancellationTokenSource? cancellation) { try { cancellation?.Cancel(); } catch (ObjectDisposedException) { } }

    private static DaevanionOptimizationRequest Freeze(DaevanionOptimizationRequest request) => DaevanionPoolSettings.Freeze(request);
    private static DaevanionBoardPlan Freeze(DaevanionBoardPlan board) => board with
    {
        NodeIds = Array.AsReadOnly(board.NodeIds.ToArray()), Edges = Array.AsReadOnly(board.Edges.Select(edge => edge with { }).ToArray()),
        ActivationOrder = Array.AsReadOnly(board.ActivationOrder.ToArray())
    };
    private static DaevanionOptimizationResult Freeze(DaevanionOptimizationResult result) => result with
        { Boards = Array.AsReadOnly(result.Boards.Select(Freeze).ToArray()) };
    private static DaevanionSavedRoute Freeze(DaevanionSavedRoute route) => route with { Request = Freeze(route.Request), Result = Freeze(route.Result) };

    public void Dispose()
    {
        CancellationTokenSource? stop;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true; stop = Invalidate();
        }
        if (_skillSource is { } source) source.Changed -= SkillSourceChanged;
        if (_characterSource is { } character) character.Changed -= CharacterSourceChanged;
        Stop(stop);
        // The semaphore lives until an in-flight operation releases it.
    }

    private sealed class CallbackProgress<T>(Action<T> callback) : IProgress<T>
    { public void Report(T value) => callback(value); }
}
