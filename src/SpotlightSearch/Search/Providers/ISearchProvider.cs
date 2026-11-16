using SpotlightSearch.Models;

namespace SpotlightSearch.Search.Providers;

internal interface ISearchProvider
{
    /// <summary>True for in-memory providers that can answer without touching the disk.</summary>
    bool IsInstant => false;

    Task<IReadOnlyList<SpotlightSearchItem>> SearchAsync(string query, int limit, CancellationToken cancellationToken);
}
