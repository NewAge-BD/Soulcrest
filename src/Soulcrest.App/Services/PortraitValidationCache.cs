using System.Security.Cryptography;
using System.Text;
using Soulcrest.Ocr.PetWindow;

namespace Soulcrest.App.Services;

/// <summary>Only the expensive image match is cached; current catalog repair rules still run every time.</summary>
internal sealed class PortraitValidationCache
{
    internal sealed class Document
    {
        public string Context { get; set; } = "";
        public Dictionary<string, PortraitMatch> Matches { get; set; } = [];
    }

    private readonly string _path;
    private readonly Document _document;
    private readonly object _gate = new();
    private readonly HashSet<string> _used = [];
    private int _hits, _misses;
    public int Hits => _hits;
    public int Misses => _misses;

    public PortraitValidationCache(string path, string context)
    {
        _path = path;
        Document loaded;
        try { loaded = JsonFile.Load<Document>(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { loaded = new(); }
        _document = loaded.Context == context && loaded.Matches is not null ? loaded : new() { Context = context };
    }

    internal static string ImageHash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    internal static string ContextFor(IEnumerable<(string PetId, string Path)> references, string revision)
    {
        // Filenames identify colour variants in PortraitMatcher.ArtKey, so they belong to the context too.
        var lines = references.OrderBy(r => r.PetId, StringComparer.Ordinal).ThenBy(r => r.Path, StringComparer.Ordinal)
            .Select(r => $"{r.PetId}\t{Path.GetFileName(r.Path)}\t{(File.Exists(r.Path) ? ImageHash(r.Path) : "missing")}");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(revision + "\n" + string.Join("\n", lines))));
    }

    public PortraitMatch GetOrMatch(string petId, string hash, Func<PortraitMatch> match)
    {
        var key = petId + ":" + hash;
        lock (_gate)
        {
            _used.Add(key);
            if (_document.Matches.TryGetValue(key, out var cached) && cached is not null)
            {
                _hits++;
                return cached;
            }
            _misses++;
        }
        var result = match();
        lock (_gate) _document.Matches[key] = result;
        return result;
    }

    public void Save()
    {
        lock (_gate)
        {
            foreach (var key in _document.Matches.Keys.Where(k => !_used.Contains(k)).ToArray())
                _document.Matches.Remove(key);
            try { JsonFile.Save(_path, _document); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                System.Diagnostics.Trace.TraceWarning("Portrait validation cache could not be saved: {0}", e.Message);
            }
        }
    }
}
