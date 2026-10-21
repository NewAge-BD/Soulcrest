using System.Text.Json;
using Soulcrest.App.Services;
using Soulcrest.Core.Network;
using Soulcrest.Core.Pets;
using Soulcrest.Core.Progress;
using Xunit;

namespace Soulcrest.App.Tests;

public sealed class ProgressPetIdentityTests
{
    private const string Primary = "modified-karnif";
    private const string Legacy = "kalnif-02";
    private const string Learned = "modifizierter-karnif";

    [RequiresFact(TestRequirement.MapData)]
    public void LoadingAndReadingAnImportedAliasPreservesAllRawData()
    {
        using var kept = KeepFiles();
        Seed();
        var profile = File.ReadAllBytes(AppPaths.ProgressFile);
        var legacyPortrait = File.ReadAllBytes(PortraitPath(Legacy));
        var primaryPortrait = File.ReadAllBytes(PortraitPath(Primary));

        var progress = new ProgressService();

        Assert.Equal(Primary, progress.Catalog.Find(Legacy)?.Id);
        Assert.Single(progress.Catalog.Pets, pet => pet.Id == Primary);
        Assert.DoesNotContain(progress.Catalog.Pets, pet => pet.Id == Legacy);
        Assert.Equal(70, progress.Souls(Primary));
        Assert.Equal(70, progress.Souls(Legacy));
        Assert.True(progress.IsFocus(Primary));
        Assert.True(progress.IsFocus(Legacy));
        Assert.Equal(Primary, Assert.Single(progress.FocusPets()).Id);
        Assert.Equal(profile, File.ReadAllBytes(AppPaths.ProgressFile));
        Assert.Equal(legacyPortrait, File.ReadAllBytes(PortraitPath(Legacy)));
        Assert.Equal(primaryPortrait, File.ReadAllBytes(PortraitPath(Primary)));
    }

    [RequiresFact(TestRequirement.MapData)]
    public void AnUnresolvedLearnedRowUsingAProvenLegacyIdCannotRecreateTheDisplayedDuplicate()
    {
        using var kept = KeepFiles();
        Seed();
        File.WriteAllText(AppPaths.LearnedPetsFile, """
            [{ "id":"kalnif-02", "en":"Kalnif 02", "genus":"unknown" }]
            """);
        var before = File.ReadAllText(AppPaths.LearnedPetsFile);

        var progress = new ProgressService();

        Assert.Equal(Primary, progress.Catalog.Find(Legacy)?.Id);
        Assert.DoesNotContain(progress.Catalog.Pets, pet => pet.Id == Legacy);
        Assert.Single(progress.Catalog.Pets, pet => pet.Id == Primary);
        Assert.True(File.Exists(PortraitPath(Primary)));
        Assert.True(File.Exists(PortraitPath(Legacy)));
        Assert.Equal(before, File.ReadAllText(AppPaths.LearnedPetsFile));
    }

    [RequiresFact(TestRequirement.MapData)]
    public void TheGermanLearnedDuplicateRepairsToOnePetWithoutLosingProgressOrFocus()
    {
        using var kept = KeepFiles();
        Seed(includeLearned: true);

        var progress = new ProgressService();

        Assert.Single(progress.Catalog.Pets, pet => pet.Id == Primary);
        Assert.DoesNotContain(progress.Catalog.Pets, pet => pet.Id is Legacy or Learned);
        Assert.Equal(Primary, progress.FindByName("Modified Karnif")?.Id);
        Assert.Equal(Primary, progress.FindByName("Modifizierter Karnif")?.Id);
        Assert.Equal(90, progress.Souls(Primary));
        Assert.True(progress.IsFocus(Primary));
        Assert.Null(progress.Catalog.Find(Learned)); // OCR names are repaired, never invented compatibility aliases.
        Assert.Empty(JsonSerializer.Deserialize<List<PetDefinition>>(File.ReadAllText(AppPaths.LearnedPetsFile))!);
        Assert.False(File.Exists(PortraitPath(Learned)));
        Assert.True(File.Exists(PortraitPath(Primary)));
        var saved = ReadProfile();
        Assert.False(saved.Pets.ContainsKey(Learned));
        Assert.Equal(70, saved.Pets[Legacy].Souls); // The separately stored imported legacy snapshot remains.
        Assert.Equal(90, saved.Pets[Primary].Souls);
        Assert.True(saved.Pets[Primary].Focus);
        Assert.Equal(new DateTimeOffset(2026, 10, 11, 3, 0, 0, TimeSpan.Zero), saved.Pets[Primary].UpdatedAt);

        var reloaded = new ProgressService();
        Assert.Equal(90, reloaded.Souls(Legacy));
        Assert.Equal(Primary, reloaded.FindByName("Modifizierter Karnif")?.Id);
        Assert.DoesNotContain(reloaded.Catalog.Pets, pet => pet.Id == Learned);
    }

    [RequiresFact(TestRequirement.MapData)]
    public void ExplicitManualAndFocusWritesNormalizeOnlyTheAddressedPet()
    {
        using var kept = KeepFiles();
        Seed();
        var progress = new ProgressService();
        var anotherAlias = progress.Catalog.Aliases.First(pair => pair.Key != Legacy).Key;
        var profile = ReadProfile();
        profile.Pets[anotherAlias] = new PetProgressEntry { Souls = 17 };
        File.WriteAllText(AppPaths.ProgressFile, JsonSerializer.Serialize(profile));
        progress = new ProgressService();

        progress.SetLevel(Legacy, 1, 5);

        var saved = ReadProfile();
        Assert.False(saved.Pets.ContainsKey(Legacy));
        Assert.Equal(progress.Thresholds.Total(1, 5), saved.Pets[Primary].Souls);
        Assert.True(saved.Pets[Primary].Focus);
        Assert.True(saved.Pets.ContainsKey(anotherAlias));
        progress.ToggleFocus(Legacy);
        Assert.False(progress.IsFocus(Primary));
        Assert.False(progress.IsFocus(Legacy));
    }

    [RequiresFact(TestRequirement.MapData)]
    public void NetworkGainAndLegacyGermanNameReuseTheCanonicalIdentity()
    {
        using var kept = KeepFiles();
        Seed();
        var progress = new ProgressService();
        var source = new PetIdEntry(1098, "Modified Karnif", "Fera", Legacy);

        var id = progress.EnsureNetworkPet(source);
        var at = new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
        Assert.True(progress.BindPetSession(new(1, 1, at)));
        Assert.True(progress.ApplyPetGain([new(1098, 2)], new(1, 2, at.AddSeconds(1))));

        Assert.Equal(Primary, id);
        Assert.Equal(Primary, progress.FindByName("Modifizierter Karnif")?.Id);
        Assert.Equal(72, progress.Souls(Primary)); // Merge alias snapshots by max, then add once.
        Assert.Equal(72, progress.Souls(Legacy));
        Assert.Equal(Primary, Assert.Single(progress.Recent()).PetId);
        Assert.Single(progress.Catalog.Pets, pet => pet.Id == Primary);
        Assert.Empty(JsonSerializer.Deserialize<List<PetDefinition>>(File.ReadAllText(AppPaths.LearnedPetsFile))!);
        Assert.False(ReadProfile().Pets.ContainsKey(Legacy));
    }

    [RequiresFact(TestRequirement.MapData)]
    public void ReportedLearnedMistakesRepairOnlyProvenIdentitiesAndKeepUnresolvedRawProgress()
    {
        var names = new[] { "Aultrossts Doppelga...", "Decomposed Lupyllini t.Z5", "Gatekeeper",
            "Modifizierter Karnif", "Pet Info", "Tamed Zajf" };
        var corrected = new Dictionary<string, string>
        {
            ["Aultrossts Doppelga..."] = "aultross-s-doppelganger",
            ["Decomposed Lupyllini t.Z5"] = "decomposed-lupyllini",
            ["Modifizierter Karnif"] = Primary,
            ["Tamed Zajf"] = "tamed-zaif",
        };
        var ids = names.Select(PetCatalog.Slug).ToArray();
        using var kept = KeepFiles(ids.Concat(corrected.Values).Select(PortraitPath));
        Seed();
        var learned = names.Select(name => new PetDefinition
            { Id = PetCatalog.Slug(name), En = name, Genus = "unknown" }).ToList();
        File.WriteAllText(AppPaths.LearnedPetsFile, JsonSerializer.Serialize(learned));
        var profile = ReadProfile();
        foreach (var pet in learned)
        {
            profile.Pets[pet.Id] = new PetProgressEntry { Souls = 90, Focus = true };
            File.WriteAllBytes(PortraitPath(pet.Id), [7, 8, 9]);
        }
        foreach (var target in corrected.Values)
            profile.Pets[target] = new PetProgressEntry { Souls = 40 };
        File.WriteAllText(AppPaths.ProgressFile, JsonSerializer.Serialize(profile));

        var progress = new ProgressService();

        foreach (var (name, id) in corrected)
        {
            var pet = Assert.Single(progress.Catalog.Pets, pet => pet.Id == id);
            Assert.NotNull(pet.Icon);
            Assert.Equal(id, progress.FindByName(name)?.Id);
            Assert.Equal(90, progress.Souls(id));
            Assert.True(progress.IsFocus(id));
            Assert.True(File.Exists(PortraitPath(id)));
        }
        Assert.DoesNotContain(progress.Catalog.Pets, pet => ids.Contains(pet.Id));
        foreach (var name in corrected.Keys)
            Assert.False(File.Exists(PortraitPath(PetCatalog.Slug(name))));
        var saved = ReadProfile();
        foreach (var name in new[] { "Gatekeeper", "Pet Info" })
        {
            var id = PetCatalog.Slug(name);
            Assert.Equal(90, saved.Pets[id].Souls);
            Assert.True(saved.Pets[id].Focus);
            Assert.True(File.Exists(PortraitPath(id)));
        }
        var remainingLearned = JsonSerializer.Deserialize<List<PetDefinition>>(File.ReadAllText(AppPaths.LearnedPetsFile))!;
        Assert.Equal(new[] { "gatekeeper", "pet-info" }, remainingLearned.Select(pet => pet.Id).Order().ToArray());
    }

    private static void Seed(bool includeLearned = false)
    {
        Assert.StartsWith(Path.GetTempPath(), AppPaths.DataDirectory, StringComparison.OrdinalIgnoreCase);
        Directory.CreateDirectory(AppPaths.LearnedPortraitsDirectory);
        File.WriteAllText(AppPaths.LearnedNamesFile, "{}");
        File.WriteAllText(AppPaths.LearnedPetsFile, includeLearned ? """
            [{ "id":"modifizierter-karnif", "en":"Modifizierter Karnif", "de":"Modifizierter Karnif", "genus":"unknown" }]
            """ : "[]");
        var profile = new ProgressProfile();
        profile.Pets[Primary] = new PetProgressEntry { Souls = 40, UpdatedAt = new(2026, 10, 11, 1, 0, 0, TimeSpan.Zero) };
        profile.Pets[Legacy] = new PetProgressEntry { Souls = 70, Focus = true, UpdatedAt = new(2026, 10, 11, 2, 0, 0, TimeSpan.Zero) };
        if (includeLearned)
            profile.Pets[Learned] = new PetProgressEntry { Souls = 90, Focus = true, UpdatedAt = new(2026, 10, 11, 3, 0, 0, TimeSpan.Zero) };
        File.WriteAllText(AppPaths.ProgressFile, JsonSerializer.Serialize(profile));
        File.WriteAllBytes(PortraitPath(Primary), [1, 2, 3]);
        File.WriteAllBytes(PortraitPath(Legacy), [4, 5, 6]);
        if (includeLearned) File.WriteAllBytes(PortraitPath(Learned), [7, 8, 9]);
        else if (File.Exists(PortraitPath(Learned))) File.Delete(PortraitPath(Learned));
    }

    private static ProgressProfile ReadProfile() =>
        JsonSerializer.Deserialize<ProgressProfile>(File.ReadAllText(AppPaths.ProgressFile))!;

    private static string PortraitPath(string id) => Path.Combine(AppPaths.LearnedPortraitsDirectory, id + ".png");

    private static SavedFiles KeepFiles(IEnumerable<string>? additional = null) => new(new[] {
        AppPaths.ProgressFile, AppPaths.EventsFile, AppPaths.LearnedPetsFile, AppPaths.LearnedNamesFile,
        PortraitPath(Primary), PortraitPath(Legacy), PortraitPath(Learned) }.Concat(additional ?? []).Distinct());

    private sealed class SavedFiles(IEnumerable<string> paths) : IDisposable
    {
        private readonly Dictionary<string, byte[]?> _kept = paths.ToDictionary(path => path,
            path => File.Exists(path) ? File.ReadAllBytes(path) : null);

        public void Dispose()
        {
            foreach (var (path, content) in _kept)
            {
                if (content is null)
                {
                    if (File.Exists(path)) File.Delete(path);
                }
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.WriteAllBytes(path, content);
                }
            }
        }
    }
}
