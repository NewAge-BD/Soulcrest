namespace Soulcrest.App.OverlayEditorKit;

// Adapted from overlay-editor-kit 1.15.0: Overlay/OverlayInstance.cs and OverlayTemplate.cs.
public sealed record OverlayInstance
{
    public const string DefaultId = "default";
    [System.Text.Json.Serialization.JsonRequired] public string Id { get; init; } = DefaultId;
    [System.Text.Json.Serialization.JsonRequired] public string Name { get; init; } = "Overlay 1";
    [System.Text.Json.Serialization.JsonRequired] public OverlaySettings Settings { get; init; } = new();

    public static string NormalizeName(string? name)
    {
        var value = (name ?? "").Trim();
        if (value.Length is < 1 or > 60 || value.Any(char.IsControl))
            throw new ArgumentException("Bitte einen Overlay-Namen mit 1 bis 60 Zeichen eingeben.");
        return value;
    }
}

public sealed record OverlayCollectionSettings
{
    [System.Text.Json.Serialization.JsonRequired] public int Version { get; init; } = 2;
    public bool OverlaysVisible { get; init; } = true;
    [System.Text.Json.Serialization.JsonRequired] public string SelectedOverlayId { get; init; } = OverlayInstance.DefaultId;
    [System.Text.Json.Serialization.JsonRequired] public IReadOnlyList<OverlayInstance> Overlays { get; init; } = Array.AsReadOnly(new[] { new OverlayInstance() });
}

public sealed record OverlayTemplate
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; init; } = string.Empty;
    public OverlaySettings Layout { get; init; } = new();

    public OverlaySettings ApplyTo(OverlaySettings current)
    {
        ArgumentNullException.ThrowIfNull(current);
        var layout = CopyLayout(Layout);
        return current with
        {
            Width = layout.Width, Height = layout.Height, Widgets = layout.Widgets,
            Scale = layout.Scale, BackgroundOpacity = layout.BackgroundOpacity,
            ShowBorder = layout.ShowBorder, SnapToGrid = layout.SnapToGrid, AutoAlign = layout.AutoAlign,
        };
    }

    internal static OverlayTemplate Create(string name, OverlaySettings layout, string? id = null) => new()
    {
        Id = id ?? Guid.NewGuid().ToString("N"), Name = NormalizeName(name), Layout = CopyLayout(layout),
    };

    internal static string NormalizeName(string? name)
    {
        var value = name?.Trim();
        if (string.IsNullOrEmpty(value)) throw new ArgumentException("Bitte einen Namen für die Vorlage eingeben.");
        if (value.Length > 60) throw new ArgumentException("Der Vorlagenname darf höchstens 60 Zeichen enthalten.");
        if (value.Any(char.IsControl)) throw new ArgumentException("Der Vorlagenname enthält ungültige Zeichen.");
        return value;
    }

    internal static OverlaySettings CopyLayout(OverlaySettings layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        return OverlayLayout.Normalize(new()
        {
            Width = layout.Width, Height = layout.Height, Widgets = layout.Widgets,
            Scale = layout.Scale, BackgroundOpacity = layout.BackgroundOpacity,
            ShowBorder = layout.ShowBorder, SnapToGrid = layout.SnapToGrid, AutoAlign = layout.AutoAlign,
        });
    }
}
