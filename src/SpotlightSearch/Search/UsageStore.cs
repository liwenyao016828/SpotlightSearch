using System.IO;
using System.Text.Json;
using SpotlightSearch.Models;

namespace SpotlightSearch.Search;

/// <summary>
/// Port of Services/Spotlight/SpotlightUsageStore.cs (pins/aliases/exclusions removed).
///
/// Two things make Spotlight feel smart: frequently launched items float up, and a
/// query you already used to launch something brings that item straight back to the
/// top the next time you type the same words.
/// </summary>
internal sealed class UsageStore
{
    private const int MaxEntries = 100;
    private const double CountBoostCap = 90.0;
    private const double CountBoostFactor = 22.0;

    internal sealed class Entry
    {
        public List<string> Queries { get; set; } = [];
        public SpotlightResultKind Kind { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Subtitle { get; set; } = string.Empty;
        public string Target { get; set; } = string.Empty;
        public string? IconPath { get; set; }
        public int Count { get; set; }
        public DateTime LastLaunchedUtc { get; set; }
    }

    private readonly object _gate = new();
    private readonly string _path;
    private Dictionary<string, Entry>? _entries;
    private bool _saveScheduled;

    /// <summary>History can be switched off in settings; nothing is recorded then.</summary>
    public bool Enabled { get; set; } = true;

    public UsageStore()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SpotlightSearch", "usage.json"))
    {
    }

    internal UsageStore(string path) => _path = path;

    public void RecordLaunch(SpotlightSearchItem item, string query)
    {
        if (!Enabled) return;
        if (item.Kind is SpotlightResultKind.Calculation or SpotlightResultKind.SystemAction) return;

        lock (_gate)
        {
            var entries = LoadEntries();
            entries.TryGetValue(item.Id, out Entry? existing);
            string normalizedQuery = SearchText.Normalize(query);

            var queries = existing?.Queries != null ? new List<string>(existing.Queries) : [];
            if (normalizedQuery.Length > 0)
            {
                foreach (Entry other in entries.Values) other.Queries.Remove(normalizedQuery);
                queries.Remove(normalizedQuery);
                queries.Insert(0, normalizedQuery.Length > 256 ? normalizedQuery[..256] : normalizedQuery);
                if (queries.Count > 20) queries.RemoveRange(20, queries.Count - 20);
            }

            entries[item.Id] = new Entry
            {
                Queries = queries,
                Kind = item.Kind,
                Title = item.Title,
                Subtitle = item.Subtitle,
                Target = item.Target,
                IconPath = item.IconPath,
                Count = (existing?.Count ?? 0) + 1,
                LastLaunchedUtc = DateTime.UtcNow
            };

            if (entries.Count > MaxEntries)
            {
                foreach (var pair in entries.OrderBy(pair => pair.Value.LastLaunchedUtc)
                             .Take(entries.Count - MaxEntries).ToArray())
                {
                    entries.Remove(pair.Key);
                }
            }
        }

        ScheduleSave();
    }

    public double GetBoost(string id, string normalizedQuery)
    {
        if (!Enabled) return 0;

        Entry? entry;
        bool remembered;
        lock (_gate)
        {
            LoadEntries().TryGetValue(id, out entry);
            remembered = entry?.Queries != null
                         && normalizedQuery.Length > 0
                         && entry.Queries.Contains(normalizedQuery);
        }

        if (entry == null) return 0;

        int count = Math.Max(0, entry.Count);
        double countBoost = Math.Min(CountBoostCap, CountBoostFactor * Math.Log2(1.0 + count));

        double recencyBoost = 0;
        if (count > 0)
        {
            double ageDays = DateTime.UtcNow > entry.LastLaunchedUtc
                ? (DateTime.UtcNow - entry.LastLaunchedUtc).TotalDays
                : 0;
            recencyBoost = ageDays switch
            {
                < 1 => 35.0,
                < 3 => 25.0,
                < 7 => 15.0,
                < 30 => 5.0,
                _ => 0.0
            };
        }

        return countBoost + recencyBoost + (remembered ? 350.0 : 0.0);
    }

    public IReadOnlyList<SpotlightSearchItem> GetRememberedItems(string query)
    {
        if (!Enabled || string.IsNullOrWhiteSpace(query)) return [];

        string normalized = SearchText.Normalize(query);
        if (normalized.Length == 0) return [];

        List<KeyValuePair<string, Entry>> matched = [];
        lock (_gate)
        {
            foreach (var pair in LoadEntries())
            {
                if (pair.Value.Queries.Contains(normalized)) matched.Add(pair);
            }
        }

        var output = new List<SpotlightSearchItem>(matched.Count);
        foreach (var pair in matched)
        {
            var item = ToItem(pair.Key, pair.Value);
            item = item with { Score = Math.Max(1500, Ranker.Score(item, query)) };
            if (Launcher.IsValidTarget(item)) output.Add(item);
        }

        return output;
    }

    public IReadOnlyList<SpotlightSearchItem> GetRecentItems(int limit)
    {
        if (!Enabled || limit <= 0) return [];

        KeyValuePair<string, Entry>[] snapshot;
        lock (_gate)
        {
            var entries = LoadEntries();
            if (entries.Count == 0) return [];
            snapshot = entries.ToArray();
        }

        var output = new List<SpotlightSearchItem>(Math.Min(limit, snapshot.Length));
        foreach (var pair in snapshot.OrderByDescending(pair => pair.Value.LastLaunchedUtc))
        {
            if (output.Count >= limit) break;

            var item = ToItem(pair.Key, pair.Value) with { IsRecent = true };
            if (item.Kind == SpotlightResultKind.SystemAction) continue;
            if (!Launcher.IsValidTarget(item)) continue;
            output.Add(item);
        }

        return output;
    }

    public void Clear()
    {
        lock (_gate)
        {
            _entries = [];
            try
            {
                if (File.Exists(_path)) File.Delete(_path);
            }
            catch (IOException)
            {
                // Nothing actionable: the in-memory history is cleared either way.
            }
        }
    }

    private static SpotlightSearchItem ToItem(string id, Entry entry) =>
        new(id, entry.Kind, entry.Title, entry.Subtitle, entry.Target, entry.IconPath);

    private Dictionary<string, Entry> LoadEntries()
    {
        if (_entries != null) return _entries;

        try
        {
            if (File.Exists(_path))
            {
                _entries = JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(_path));
            }
        }
        catch (Exception)
        {
            // A corrupt history file must never block searching.
        }

        return _entries ??= [];
    }

    private void ScheduleSave()
    {
        lock (_gate)
        {
            if (_saveScheduled) return;
            _saveScheduled = true;
        }

        _ = Task.Run(() =>
        {
            string json;
            lock (_gate)
            {
                _saveScheduled = false;
                try
                {
                    json = JsonSerializer.Serialize(LoadEntries());
                }
                catch (Exception)
                {
                    return;
                }
            }

            try
            {
                string? directory = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                string temp = $"{_path}.{Guid.NewGuid():N}.tmp";
                File.WriteAllText(temp, json);
                File.Move(temp, _path, overwrite: true);
            }
            catch (Exception)
            {
                // History is a convenience; losing it is not worth surfacing an error.
            }
        });
    }
}
