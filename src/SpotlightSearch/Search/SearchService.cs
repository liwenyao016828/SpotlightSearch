using System.Runtime.CompilerServices;
using SpotlightSearch.Models;
using SpotlightSearch.Search.Providers;

namespace SpotlightSearch.Search;

internal readonly record struct SearchUpdate(
    IReadOnlyList<SpotlightSearchItem> Results,
    bool IsComplete);

/// <summary>
/// Port of Services/Spotlight/SpotlightSearchService.cs.
///
/// The important behaviour is that results stream: instant providers (calculator,
/// catalog, app index) answer in microseconds, the disk walk answers after its
/// debounce. The window re-renders on every update, so the list fills in instead of
/// waiting for the slowest provider.
/// </summary>
internal sealed class SearchService
{
    private const int MaxResults = 50;
    private const int ProviderDebounceMs = 75;

    private readonly ISearchProvider[] _providers;
    private readonly UsageStore _usage;
    private readonly AppSearchProvider _apps;

    public SearchService(UsageStore usage, Settings.SettingsStore settingsStore)
    {
        _usage = usage;
        _apps = new AppSearchProvider();
        _providers =
        [
            new CalculatorProvider(),
            new SystemCommandProvider(),
            _apps,
            new FileSearchProvider(() => settingsStore.Current)
        ];
    }

    public Task WarmupAsync()
    {
        // The full Windows Settings index parses in the background; system searches
        // deepen automatically once it is ready.
        WindowsSettingsCatalog.LoadInBackground();
        return _apps.WarmupAsync();
    }

    /// <summary>Most recently launched items, shown when the query box is empty.</summary>
    public IReadOnlyList<SpotlightSearchItem> GetRecents(int limit) => _usage.GetRecentItems(limit);

    public async IAsyncEnumerable<SearchUpdate> SearchAsync(
        string query,
        int limit,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string normalizedQuery = SearchText.Normalize(query);
        if (normalizedQuery.Length == 0 || limit <= 0)
        {
            yield return new SearchUpdate([], true);
            yield break;
        }

        var tasks = new Task<IReadOnlyList<SpotlightSearchItem>>[_providers.Length];
        for (int i = 0; i < _providers.Length; i++)
        {
            var provider = _providers[i];
            tasks[i] = RunProviderAsync(provider, query, provider.IsInstant ? 0 : ProviderDebounceMs, cancellationToken);
        }

        // Personalization is its own source: an item you already launched with these
        // exact words should come back even if no provider finds it again.
        var remembered = Task.Run(() => _usage.GetRememberedItems(query), cancellationToken);

        var groups = new IReadOnlyList<SpotlightSearchItem>[_providers.Length + 1];
        Array.Fill(groups, (IReadOnlyList<SpotlightSearchItem>)[]);

        var remaining = new List<Task>(tasks) { remembered };

        try
        {
            while (remaining.Count > 0)
            {
                await Task.WhenAny(remaining).WaitAsync(cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();

                // Collect everything that finished together into a single UI update,
                // so two providers answering at once do not cause two repaints.
                for (int i = 0; i < tasks.Length; i++)
                {
                    if (!tasks[i].IsCompleted || !remaining.Remove(tasks[i])) continue;
                    groups[i] = ApplyUsageBoost(await tasks[i].ConfigureAwait(false), normalizedQuery);
                }

                if (remembered.IsCompleted && remaining.Remove(remembered))
                {
                    groups[^1] = await remembered.ConfigureAwait(false);
                }

                yield return new SearchUpdate(Merge(groups, limit), remaining.Count == 0);
            }
        }
        finally
        {
            // A superseded query may still be inside a disk walk. Observe its
            // eventual failure so it cannot surface as an unobserved task exception.
            _ = Task.WhenAll(tasks.Cast<Task>().Append(remembered)).ContinueWith(
                completed => _ = completed.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    private static async Task<IReadOnlyList<SpotlightSearchItem>> RunProviderAsync(
        ISearchProvider provider, string query, int debounceMilliseconds, CancellationToken cancellationToken)
    {
        try
        {
            if (debounceMilliseconds > 0)
                await Task.Delay(debounceMilliseconds, cancellationToken).ConfigureAwait(false);

            // Never run a provider's synchronous prefix on the caller's UI thread.
            return await Task.Run(() => provider.SearchAsync(query, MaxResults, cancellationToken), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return [];
        }
    }

    private IReadOnlyList<SpotlightSearchItem> ApplyUsageBoost(
        IReadOnlyList<SpotlightSearchItem> results, string normalizedQuery)
    {
        if (results.Count == 0) return results;

        var output = new List<SpotlightSearchItem>(results.Count);
        foreach (var item in results)
        {
            double boost = _usage.GetBoost(item.Id, normalizedQuery);
            output.Add(boost > 0 ? item with { Score = item.Score + boost } : item);
        }

        return output;
    }

    internal static IReadOnlyList<SpotlightSearchItem> Merge(
        IEnumerable<IReadOnlyList<SpotlightSearchItem>> providerResults, int limit)
    {
        if (limit <= 0) return [];

        var bestByTarget = new Dictionary<string, SpotlightSearchItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in providerResults)
        {
            foreach (var item in group)
            {
                if (!bestByTarget.TryGetValue(item.Target, out var best) || item.Score > best.Score)
                    bestByTarget[item.Target] = item;
            }
        }

        if (bestByTarget.Count == 0) return [];

        var buffer = new SpotlightSearchItem[bestByTarget.Count];
        bestByTarget.Values.CopyTo(buffer, 0);
        Array.Sort(buffer, CompareScoreThenTitle);

        if (buffer.Length <= limit) return buffer;

        var trimmed = new SpotlightSearchItem[limit];
        Array.Copy(buffer, 0, trimmed, 0, limit);
        return trimmed;
    }

    private static int CompareScoreThenTitle(SpotlightSearchItem a, SpotlightSearchItem b)
    {
        int byScore = b.Score.CompareTo(a.Score);
        if (byScore != 0) return byScore;

        int byTitle = string.Compare(a.Title, b.Title, StringComparison.CurrentCultureIgnoreCase);
        return byTitle != 0 ? byTitle : string.CompareOrdinal(a.Id, b.Id);
    }
}
