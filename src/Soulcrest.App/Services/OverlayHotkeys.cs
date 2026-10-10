using Microsoft.AspNetCore.Components.Web;
using Soulcrest.App.Overlay;

namespace Soulcrest.App.Services;

public sealed record OverlayHotkey(int Key, uint Modifiers)
{
    public bool IsValid => (Modifiers & ~15u) == 0 && Key is >= 0x08 and <= 0xFE
        && Enum.IsDefined(typeof(Keys), Key)
        && Key is not (0x10 or 0x11 or 0x12 or 0x5B or 0x5C or 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5)
        && (Modifiers != 0 || Key is >= 0x70 and <= 0x87);

    public string Label => string.Join("+", Parts());
    private IEnumerable<string> Parts()
    {
        if ((Modifiers & 2) != 0) yield return UiText.T("Strg");
        if ((Modifiers & 1) != 0) yield return "Alt";
        if ((Modifiers & 4) != 0) yield return UiText.T("Umschalt");
        if ((Modifiers & 8) != 0) yield return "Win";
        yield return Key is >= 0x30 and <= 0x39 ? ((char)Key).ToString() : ((Keys)Key).ToString();
    }

    public static OverlayHotkey? FromKeyboard(KeyboardEventArgs e)
    {
        var key = e.Code switch
        {
            { Length: 4 } code when code.StartsWith("Key", StringComparison.Ordinal) => (int)code[3],
            { Length: 6 } code when code.StartsWith("Digit", StringComparison.Ordinal) => (int)code[5],
            { } code when Enum.TryParse<Keys>(code, out var parsed) => (int)parsed,
            "ArrowUp" => (int)Keys.Up, "ArrowDown" => (int)Keys.Down,
            "ArrowLeft" => (int)Keys.Left, "ArrowRight" => (int)Keys.Right,
            "Backspace" => (int)Keys.Back,
            "Minus" => (int)Keys.OemMinus, "Equal" => (int)Keys.Oemplus,
            "Comma" => (int)Keys.Oemcomma, "Period" => (int)Keys.OemPeriod,
            "Slash" => (int)Keys.OemQuestion, "Semicolon" => (int)Keys.OemSemicolon,
            "Quote" => (int)Keys.OemQuotes, "Backquote" => (int)Keys.Oemtilde,
            "BracketLeft" => (int)Keys.OemOpenBrackets, "BracketRight" => (int)Keys.OemCloseBrackets,
            "Backslash" or "IntlBackslash" => (int)Keys.OemBackslash,
            { Length: 7 } code when code.StartsWith("Numpad", StringComparison.Ordinal) && char.IsAsciiDigit(code[6]) => (int)Keys.NumPad0 + code[6] - '0',
            "NumpadAdd" => (int)Keys.Add, "NumpadSubtract" => (int)Keys.Subtract,
            "NumpadMultiply" => (int)Keys.Multiply, "NumpadDivide" => (int)Keys.Divide,
            "NumpadDecimal" => (int)Keys.Decimal,
            _ => 0
        };
        var result = new OverlayHotkey(key, (e.AltKey ? 1u : 0) | (e.CtrlKey ? 2u : 0)
            | (e.ShiftKey ? 4u : 0) | (e.MetaKey ? 8u : 0));
        return result.IsValid ? result : null;
    }
}

public sealed record OverlayHotkeyAction(string Id, string Overlay, bool Unlock = false);

public static class OverlayHotkeys
{
    public static IReadOnlyList<OverlayHotkeyAction> Actions { get; } = Array.AsReadOnly(new[]
    {
        new OverlayHotkeyAction("loot.toggle", "Loot / Pets"),
        new OverlayHotkeyAction("loot.unlock", "Loot / Pets", true),
        new OverlayHotkeyAction("boss.toggle", "Boss Rush"),
        new OverlayHotkeyAction("boss.unlock", "Boss Rush", true),
        new OverlayHotkeyAction("leveling.toggle", "Leveling Routes"),
        new OverlayHotkeyAction("leveling.unlock", "Leveling Routes", true),
        new OverlayHotkeyAction("map.toggle", "Karten-Overlay"),
        new OverlayHotkeyAction("pet-scan.toggle", "Pet-Scan-Markierungen"),
        new OverlayHotkeyAction("exploration-scan.toggle", "Erkundungs-Scan")
    });

    public static Dictionary<string, OverlayHotkey?> Defaults() => new(StringComparer.Ordinal)
    {
        ["loot.toggle"] = new((int)Keys.P, 3)
    };

    public static void Normalize(AppSettings settings)
    {
        settings.OverlayHotkeys ??= Defaults();
        var known = Actions.Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
        var used = new HashSet<OverlayHotkey>();
        settings.OverlayHotkeys = settings.OverlayHotkeys.Where(p => known.Contains(p.Key))
            .ToDictionary(p => p.Key, p => p.Value is { IsValid: true } gesture && used.Add(gesture) ? p.Value : null, StringComparer.Ordinal);
    }

    public static bool ToggleVisibility(AppSettings settings, string action)
    {
        switch (action)
        {
            case "loot.toggle": settings.OverlayEnabled = !settings.OverlayEnabled; break;
            case "boss.toggle": settings.BossOverlayVisible = !settings.BossOverlayVisible; break;
            case "leveling.toggle": settings.LevelingOverlayEnabled = !settings.LevelingOverlayEnabled; break;
            case "map.toggle": settings.MapOverlayEnabled = !settings.MapOverlayEnabled; break;
            case "pet-scan.toggle": settings.PetScanOverlayEnabled = !settings.PetScanOverlayEnabled; break;
            case "exploration-scan.toggle": settings.ExplorationScanOverlayEnabled = !settings.ExplorationScanOverlayEnabled; break;
            default: return false;
        }
        return true;
    }

    public static bool IsVisible(AppSettings settings, string action) => action switch
    {
        "loot.toggle" => settings.OverlayEnabled,
        "boss.toggle" => settings.BossOverlayVisible,
        "leveling.toggle" => settings.LevelingOverlayEnabled,
        "map.toggle" => settings.MapOverlayEnabled,
        "pet-scan.toggle" => settings.PetScanOverlayEnabled,
        "exploration-scan.toggle" => settings.ExplorationScanOverlayEnabled,
        _ => false
    };
}

internal interface IOverlayHotkeyRegistrar
{
    bool Register(nint window, int id, OverlayHotkey gesture);
    void Unregister(nint window, int id);
}

internal sealed class WindowsOverlayHotkeyRegistrar : IOverlayHotkeyRegistrar
{
    public bool Register(nint window, int id, OverlayHotkey gesture) =>
        NativeMethods.RegisterHotKey(window, id, gesture.Modifiers | NativeMethods.MOD_NOREPEAT, (uint)gesture.Key);
    public void Unregister(nint window, int id) => NativeMethods.UnregisterHotKey(window, id);
}

/// <summary>Owns only Soulcrest's global shortcuts; never sends keyboard input to another window.</summary>
public sealed class OverlayHotkeyService : IDisposable
{
    private const int FirstId = 0x5100;
    private readonly SettingsService _settings;
    private readonly IOverlayHotkeyRegistrar _registrar;
    private readonly Dictionary<int, string> _registered = [];
    private Dictionary<string, OverlayHotkey?>? _applied;
    private Dictionary<string, string> _errors = [];
    private Control? _owner;
    private nint _window;
    private bool _capturing, _disposed;

    public OverlayHotkeyService(SettingsService settings) : this(settings, new WindowsOverlayHotkeyRegistrar()) { }
    internal OverlayHotkeyService(SettingsService settings, IOverlayHotkeyRegistrar registrar)
    {
        _settings = settings; _registrar = registrar;
        _settings.Changed += SettingsChanged;
    }
    public event Action? Changed;
    public event Action<string>? Invoked;
    public string? Error(string action) => _errors.GetValueOrDefault(action);
    public OverlayHotkey? Binding(string action) => _settings.Current.OverlayHotkeys.GetValueOrDefault(action);

    public void Attach(Control owner)
    {
        if (_owner == owner) return;
        Detach(); _owner = owner;
        owner.HandleCreated += OwnerCreated; owner.HandleDestroyed += OwnerDestroyed;
        SetWindow(owner.Handle);
    }
    private void OwnerCreated(object? sender, EventArgs e) => SetWindow(_owner!.Handle);
    private void OwnerDestroyed(object? sender, EventArgs e) => SetWindow(0);

    internal void SetWindow(nint window)
    {
        Release(); _window = window; _applied = null; Rebind();
    }

    public void Detach()
    {
        if (_owner is { } owner)
        {
            owner.HandleCreated -= OwnerCreated; owner.HandleDestroyed -= OwnerDestroyed;
        }
        _owner = null; SetWindow(0);
    }

    public void SetCapturing(bool capturing)
    {
        if (_capturing == capturing) return;
        _capturing = capturing; _applied = null; Rebind();
    }

    public string? Assign(string action, OverlayHotkey? gesture)
    {
        if (!OverlayHotkeys.Actions.Any(a => a.Id == action)) return "Unbekannte Overlay-Aktion.";
        if (gesture is { IsValid: false }) return "Bitte eine Kombination mit Strg, Alt, Umschalt oder Win wählen; F1–F24 gehen auch allein.";
        if (gesture is not null && _settings.Current.OverlayHotkeys.Any(p => p.Key != action && p.Value == gesture))
            return "Diese Tastenkombination ist bereits einem anderen Overlay zugewiesen.";
        _settings.Update(s => s.OverlayHotkeys = new(s.OverlayHotkeys, StringComparer.Ordinal) { [action] = gesture });
        return null;
    }

    public bool ProcessHotkey(int id)
    {
        if (_disposed || _capturing || !_registered.TryGetValue(id, out var action)) return false;
        Invoked?.Invoke(action); return true;
    }

    private void SettingsChanged()
    {
        if (_disposed) return;
        if (_owner is { IsHandleCreated: true } owner && owner.InvokeRequired)
        {
            try { owner.BeginInvoke(Rebind); }
            catch (InvalidOperationException) when (_disposed || owner.IsDisposed || !owner.IsHandleCreated) { }
            return;
        }
        Rebind();
    }

    private void Rebind()
    {
        if (_disposed) return;
        if (_owner is { IsHandleCreated: true } owner && owner.InvokeRequired)
        {
            try { owner.BeginInvoke(Rebind); }
            catch (InvalidOperationException) when (_disposed || owner.IsDisposed || !owner.IsHandleCreated) { }
            return;
        }
        var desired = new Dictionary<string, OverlayHotkey?>(_settings.Current.OverlayHotkeys, StringComparer.Ordinal);
        if (_applied is not null && _applied.Count == desired.Count && desired.All(p => _applied.TryGetValue(p.Key, out var old) && old == p.Value)) return;
        Release(); _applied = desired;
        var errors = new Dictionary<string, string>();
        var used = new HashSet<OverlayHotkey>();
        if (_window != 0 && !_capturing)
            for (var index = 0; index < OverlayHotkeys.Actions.Count; index++)
            {
                var action = OverlayHotkeys.Actions[index].Id;
                if (desired.GetValueOrDefault(action) is not { } gesture) continue;
                if (!gesture.IsValid || !used.Add(gesture)) { errors[action] = "Ungültige oder doppelte Tastenkombination."; continue; }
                var id = FirstId + index;
                if (_registrar.Register(_window, id, gesture)) _registered[id] = action;
                else errors[action] = "Diese Tastenkombination ist von Windows oder einem anderen Programm belegt. Bitte eine andere wählen.";
            }
        _errors = errors; Changed?.Invoke();
    }

    private void Release()
    {
        foreach (var id in _registered.Keys) _registrar.Unregister(_window, id);
        _registered.Clear();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _settings.Changed -= SettingsChanged; Detach(); _disposed = true;
    }
}
