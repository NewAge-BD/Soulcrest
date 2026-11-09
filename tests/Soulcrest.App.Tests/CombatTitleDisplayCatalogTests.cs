using System.Globalization;
using Soulcrest.App.Components;
using Soulcrest.Core.Combat;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class CombatTitleDisplayCatalogTests
{
    private const string Entry = "{\"id\":42,\"nameDe\":\"Beispieltitel\",\"nameEn\":\"Example Title\",\"equipCategory\":\"None\"}";
    private static string Document(string entries) => "{\"schema\":2,\"metadataBuild\":26425393,\"presentationOnly\":true,"
        + "\"doesNotEstablishOwnershipOrEffectApplicability\":true,\"titles\":[" + entries + "]}";

    [Fact]
    public void EmbeddedPresentationLabelsCoverExistingEffectIdsWithoutGrantingOwnershipOrAvailability()
    {
        var catalog = CombatTitleDisplayCatalog.Default;
        Assert.Equal(26425393, catalog.MetadataBuild);
        Assert.Equal(1517, catalog.Count);
        Assert.Equal("Draped in Sky", catalog.Name(11010001));
        Assert.Equal("Himmelsbekleidet", catalog.Name(11010001, "de"));
        Assert.Null(catalog.Name(0)); Assert.Null(catalog.Name(999999));
        Assert.Equal("Quest", catalog.Find(11010001)!.Origin);
        Assert.Equal("Common", catalog.Find(11010001)!.Grade);
        Assert.Equal("icons/combat/titles/offensive-emblem.png", catalog.Find(11010001)!.IconPath);
        Assert.Equal("FixingDamage", Assert.Single(catalog.Find(11010001)!.EquippedEffects!).StatId);
        Assert.Equal("Accuracy", Assert.Single(catalog.Find(11010001)!.OwnedEffects!).StatId);
        var sourceIds = CombatEffectCatalog.Default.SourceEffects
            .Where(source => source.SourceKind is CombatSourceKind.TitleOwned or CombatSourceKind.TitleEquipped)
            .SelectMany(source => source.Aliases.Prepend(source.SourceId))
            .Select(id => int.Parse(id, CultureInfo.InvariantCulture)).Distinct();
        Assert.All(sourceIds, id => Assert.NotNull(catalog.Find(id)));
    }

    [Fact]
    public void EffectArraysAreFrozenAndUnqualifiedMetadataStaysDistinctFromAnExplicitEmptyArray()
    {
        var entry = Entry.Replace("\"equipCategory\":\"None\"", "\"equipCategory\":\"None\",\"equippedEffects\":[],\"ownedEffects\":["
            + "{\"statId\":\"HPMax\",\"labelDe\":\"LP\",\"labelEn\":\"HP\",\"value\":0,\"unit\":\"Points\"}]", StringComparison.Ordinal);
        var catalog = CombatTitleDisplayCatalog.FromJson(Document(entry));
        var title = catalog.Find(42)!;
        Assert.Empty(title.EquippedEffects!);
        Assert.Equal(0m, Assert.Single(title.OwnedEffects!).Value);
        var mutableInterface = Assert.IsAssignableFrom<IList<CombatTitleDisplayEffect>>(title.OwnedEffects);
        Assert.Throws<NotSupportedException>(() => mutableInterface.Clear());
        Assert.Null(CombatTitleDisplayCatalog.FromJson(Document(Entry)).Find(42)!.EquippedEffects);
    }

    [Fact]
    public void NoneCategoryIsValidAndLabelsAreIndependentImmutableFacts()
    {
        var catalog = CombatTitleDisplayCatalog.FromJson(Document(Entry));
        var title = Assert.IsType<CombatTitleDisplayDefinition>(catalog.Find(42));
        Assert.Equal("None", title.EquipCategory);
        var changed = title with { NameEn = "Different" };
        Assert.NotEqual(changed, catalog.Find(42));
        Assert.Equal("Example Title", catalog.Name(42));
        Assert.Null(catalog.Find(43));
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("zero-id")]
    [InlineData("negative-id")]
    [InlineData("missing-name")]
    [InlineData("blank-name")]
    [InlineData("markup-name")]
    [InlineData("unknown-category")]
    [InlineData("extra-field")]
    [InlineData("schema")]
    [InlineData("missing-build")]
    [InlineData("not-presentation")]
    [InlineData("grants-effects")]
    [InlineData("unknown-origin")]
    [InlineData("unknown-grade")]
    [InlineData("external-icon")]
    [InlineData("traversal-icon")]
    [InlineData("bad-unit")]
    public void MalformedMetadataCannotEnterThePresentationLookup(string variant)
    {
        var json = Document(Entry);
        json = variant switch
        {
            "duplicate" => Document(Entry + "," + Entry),
            "zero-id" => json.Replace("\"id\":42", "\"id\":0", StringComparison.Ordinal),
            "negative-id" => json.Replace("\"id\":42", "\"id\":-1", StringComparison.Ordinal),
            "missing-name" => json.Replace("\"nameEn\":\"Example Title\",", "", StringComparison.Ordinal),
            "blank-name" => json.Replace("Example Title", " ", StringComparison.Ordinal),
            "markup-name" => json.Replace("Example Title", "<b>Example</b>", StringComparison.Ordinal),
            "unknown-category" => json.Replace("\"None\"", "\"Future\"", StringComparison.Ordinal),
            "extra-field" => json.Replace("\"id\":42", "\"ownership\":true,\"id\":42", StringComparison.Ordinal),
            "schema" => json.Replace("\"schema\":2", "\"schema\":3", StringComparison.Ordinal),
            "missing-build" => json.Replace("\"metadataBuild\":26425393,", "", StringComparison.Ordinal),
            "not-presentation" => json.Replace("\"presentationOnly\":true", "\"presentationOnly\":false", StringComparison.Ordinal),
            "grants-effects" => json.Replace("\"doesNotEstablishOwnershipOrEffectApplicability\":true", "\"doesNotEstablishOwnershipOrEffectApplicability\":false", StringComparison.Ordinal),
            "unknown-origin" => json.Replace("\"id\":42", "\"origin\":\"Future\",\"id\":42", StringComparison.Ordinal),
            "unknown-grade" => json.Replace("\"id\":42", "\"grade\":\"Future\",\"id\":42", StringComparison.Ordinal),
            "external-icon" => json.Replace("\"id\":42", "\"iconPath\":\"https://example.invalid/icon.png\",\"id\":42", StringComparison.Ordinal),
            "traversal-icon" => json.Replace("\"id\":42", "\"iconPath\":\"icons/combat/titles/../icon.png\",\"id\":42", StringComparison.Ordinal),
            "bad-unit" => json.Replace("\"id\":42", "\"ownedEffects\":[{\"statId\":\"HPMax\",\"labelDe\":\"LP\",\"labelEn\":\"HP\",\"value\":0,\"unit\":\"Future\"}],\"id\":42", StringComparison.Ordinal),
            _ => throw new ArgumentOutOfRangeException(nameof(variant))
        };
        Assert.Throws<InvalidDataException>(() => CombatTitleDisplayCatalog.FromJson(json));
    }
}
