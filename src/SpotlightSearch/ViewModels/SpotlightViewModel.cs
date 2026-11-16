using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Threading;
using SpotlightSearch.Interop;
using SpotlightSearch.Models;
using SpotlightSearch.Search;

namespace SpotlightSearch.ViewModels;

/// <summary>
/// Port of ViewModels/SpotlightViewModel.cs without the CommunityToolkit dependency.
///
/// The diff-based publish matters: replacing the whole collection on every streamed
/// update would destroy and rebuild every row (and its icon) several times per query.
/// </summary>
internal sealed class SpotlightViewModel : INotifyPropertyChanged, IDisposable
{
    private const int MaxIconConcurrency = 2;

    // Shared across query generations: cancellation cannot interrupt a shell icon
    // call that has already started.
    private static readonly SemaphoreSlim IconSlots = new(MaxIconConcurrency, MaxIconConcurrency);

    private readonly SearchService _search;
    private readonly UsageStore _usage;
    private readonly Dispatcher _dispatcher;
    private readonly HashSet<(string Id, string? Path)> _requestedIcons = new();

    private CancellationTokenSource? _searchCts;
    private long _queryGeneration;
    private bool _disposed;

    public SpotlightViewModel(SearchService search, UsageStore usage, Dispatcher dispatcher)
    {
        _search = search;
        _usage = usage;
        _dispatcher = dispatcher;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? ResultsPublished;

    public ObservableCollection<SpotlightSearchItem> Results { get; } = [];

    public string Query { get; private set; } = string.Empty;
    public bool IsSearching { get; private set; }
    public bool HasNoResults { get; private set; }
    public bool IsShowingRecents { get; private set; }

    public UsageStore Usage => _usage;

    public async Task SearchAsync(string query, int limit)
    {
        if (_disposed) return;

        Query = query;
        CancelPendingSearch();
        _searchCts = new CancellationTokenSource();
        CancellationToken token = _searchCts.Token;
        long generation = Interlocked.Increment(ref _queryGeneration);

        if (string.IsNullOrWhiteSpace(query))
        {
            IsSearching = false;
            HasNoResults = false;
            IsShowingRecents = true;
            try
            {
                var recents = await Task.Run(() => _usage.GetRecentItems(limit), token).ConfigureAwait(true);
                if (_disposed || generation != Interlocked.Read(ref _queryGeneration)) return;
                Publish(recents, markNoResults: false);
                StartIconHydration(Results.ToArray(), generation, token);
            }
            catch (OperationCanceledException)
            {
                // Expected when the query changes.
            }
            return;
        }

        IsShowingRecents = false;
        IsSearching = true;
        HasNoResults = false;
        try
        {
            await foreach (var update in _search.SearchAsync(query, limit, token).ConfigureAwait(true))
            {
                token.ThrowIfCancellationRequested();
                if (_disposed || generation != Interlocked.Read(ref _queryGeneration)) return;

                // Keep the previous rows until a provider answers; only a completed
                // query is allowed to declare that there is nothing to show.
                if (update.Results.Count == 0 && !update.IsComplete) continue;

                Publish(update.Results, markNoResults: update.IsComplete);
                StartIconHydration(update.Results, generation, token);
            }
        }
        catch (OperationCanceledException)
        {
            // Superseded.
        }
        catch (Exception)
        {
            if (!_disposed && generation == Interlocked.Read(ref _queryGeneration))
                HasNoResults = Results.Count == 0;
        }
        finally
        {
            if (!_disposed && generation == Interlocked.Read(ref _queryGeneration) && !token.IsCancellationRequested)
            {
                IsSearching = false;
                Raise(nameof(IsSearching));
            }
        }
    }

    public void CancelPendingSearch()
    {
        _requestedIcons.Clear();
        Interlocked.Increment(ref _queryGeneration);
        var cts = Interlocked.Exchange(ref _searchCts, null);
        if (cts == null) return;
        cts.Cancel();
        cts.Dispose();
        IsSearching = false;
        Raise(nameof(IsSearching));
    }

    public void RemoveResult(SpotlightSearchItem item)
    {
        if (!Results.Remove(item)) return;
        if (Results.Count == 0 && !string.IsNullOrWhiteSpace(Query)) HasNoResults = true;
        ResultsPublished?.Invoke(this, EventArgs.Empty);
    }

    public void Reset()
    {
        CancelPendingSearch();
        Query = string.Empty;
        Results.Clear();
        HasNoResults = false;
        IsShowingRecents = false;
        ResultsPublished?.Invoke(this, EventArgs.Empty);
    }

    private void Publish(IReadOnlyList<SpotlightSearchItem> results, bool markNoResults)
    {
        ApplyDiff(results);
        if (markNoResults) HasNoResults = Results.Count == 0;
        Raise(nameof(HasNoResults));
        ResultsPublished?.Invoke(this, EventArgs.Empty);
    }

    private void ApplyDiff(IReadOnlyList<SpotlightSearchItem> ordered)
    {
        for (int i = Results.Count - 1; i >= 0; i--)
        {
            string id = Results[i].Id;
            if (!ordered.Any(item => item.Id == id)) Results.RemoveAt(i);
        }

        for (int target = 0; target < ordered.Count; target++)
        {
            SpotlightSearchItem incoming = ordered[target];
            int existing = -1;
            for (int i = target; i < Results.Count; i++)
            {
                if (Results[i].Id == incoming.Id)
                {
                    existing = i;
                    break;
                }
            }

            if (existing < 0)
            {
                Results.Insert(target, incoming);
                continue;
            }

            // Reuse the already-decoded bitmap for the same path.
            if (incoming.Icon == null && Results[existing].Icon != null
                && string.Equals(incoming.IconPath, Results[existing].IconPath, StringComparison.OrdinalIgnoreCase))
            {
                incoming.Icon = Results[existing].Icon;
            }

            if (existing != target) Results.Move(existing, target);
            if (!VisuallyEqual(Results[target], incoming)) Results[target] = incoming;
        }

        while (Results.Count > ordered.Count) Results.RemoveAt(Results.Count - 1);
    }

    private static bool VisuallyEqual(SpotlightSearchItem current, SpotlightSearchItem incoming) =>
        current.Title == incoming.Title
        && current.Target == incoming.Target
        && current.Subtitle == incoming.Subtitle
        && current.Kind == incoming.Kind
        && current.IsRecent == incoming.IsRecent
        && current.IconPath == incoming.IconPath
        && (current.Icon == null) == (incoming.Icon == null);

    private void StartIconHydration(
        IReadOnlyList<SpotlightSearchItem> items, long generation, CancellationToken cancellationToken)
    {
        var toHydrate = items
            .Where(item => item.Icon == null && !string.IsNullOrEmpty(item.IconPath))
            .Where(item => _requestedIcons.Add((item.Id, item.IconPath)))
            .ToList();

        if (toHydrate.Count == 0) return;

        _ = Task.Run(async () =>
        {
            var options = new ParallelOptions
            {
                MaxDegreeOfParallelism = MaxIconConcurrency,
                CancellationToken = cancellationToken
            };

            try
            {
                await Parallel.ForEachAsync(toHydrate, options, async (item, ct) =>
                {
                    if (Interlocked.Read(ref _queryGeneration) != generation) return;

                    ImageSource? icon;
                    await IconSlots.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        icon = FileIconProvider.GetIcon(item.IconPath);
                        if (icon == null) return;
                        if (!icon.IsFrozen)
                        {
                            if (!icon.CanFreeze) return;
                            icon.Freeze();
                        }
                    }
                    finally
                    {
                        IconSlots.Release();
                    }

                    if (Interlocked.Read(ref _queryGeneration) != generation || ct.IsCancellationRequested) return;

                    if (_dispatcher.CheckAccess()) ApplyLoadedIcon(item, icon, generation);
                    else await _dispatcher.InvokeAsync(() => ApplyLoadedIcon(item, icon, generation),
                        DispatcherPriority.Background, ct);
                });
            }
            catch (OperationCanceledException)
            {
                // Expected when the query changes.
            }
            catch (Exception)
            {
                // A missing icon is not worth surfacing.
            }
        }, cancellationToken);
    }

    private void ApplyLoadedIcon(SpotlightSearchItem item, ImageSource icon, long generation)
    {
        if (_disposed || Interlocked.Read(ref _queryGeneration) != generation) return;

        var matching = Results.FirstOrDefault(candidate => candidate.Id == item.Id);
        if (matching != null && string.Equals(matching.IconPath, item.IconPath, StringComparison.OrdinalIgnoreCase))
        {
            matching.Icon = icon;
        }
        item.Icon = icon;
    }

    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CancelPendingSearch();
    }
}
