using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.JellyCAP.Models;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.JellyCAP.Services;

/// <summary>
/// Discovers collections and builds provider-safe context from their linked members.
/// </summary>
public sealed class CollectionContextService
{
    private readonly ILibraryManager _libraryManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="CollectionContextService"/> class.
    /// </summary>
    /// <param name="libraryManager">Jellyfin library manager.</param>
    public CollectionContextService(ILibraryManager libraryManager)
    {
        _libraryManager = libraryManager;
    }

    /// <summary>
    /// Lists collections by name.
    /// </summary>
    /// <param name="search">Optional case-insensitive name filter.</param>
    /// <param name="startIndex">Zero-based index of the first result.</param>
    /// <param name="limit">Maximum number of results to return.</param>
    /// <returns>A bounded page of matching collections.</returns>
    public PagedResult<CollectionSummary> ListCollections(string? search, int startIndex, int limit)
    {
        var query = new InternalItemsQuery
        {
            Recursive = true,
            IncludeItemTypes = [BaseItemKind.BoxSet],
            GroupByPresentationUniqueKey = false,
            DtoOptions = DtoOptions.StoredColumnsOnly
        };

        var matching = _libraryManager.GetItemList(query)
            .OfType<BoxSet>()
            .Where(boxSet => string.IsNullOrWhiteSpace(search)
                || (boxSet.Name ?? string.Empty).Contains(search.Trim(), StringComparison.OrdinalIgnoreCase))
            .OrderBy(boxSet => boxSet.SortName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var boundedStartIndex = Math.Max(0, startIndex);
        var boundedLimit = Math.Clamp(limit, 1, 100);

        var items = matching
            .Skip(boundedStartIndex)
            .Take(boundedLimit)
            .Select(boxSet => new CollectionSummary
            {
                Id = boxSet.Id,
                Name = boxSet.Name ?? string.Empty,
                SortTitle = boxSet.ForcedSortName ?? string.Empty,
                ContentRating = boxSet.OfficialRating ?? string.Empty,
                CommunityRating = boxSet.CommunityRating,
                Overview = boxSet.Overview ?? string.Empty,
                Genres = Normalize(boxSet.Genres),
                Tags = Normalize(boxSet.Tags),
                LinkedItemCount = boxSet.LinkedChildren.Length
            })
            .ToArray();

        return new PagedResult<CollectionSummary>
        {
            Items = items,
            TotalCount = matching.Length,
            StartIndex = boundedStartIndex,
            PageSize = boundedLimit
        };
    }

    /// <summary>
    /// Gets a collection by identifier.
    /// </summary>
    /// <param name="collectionId">Collection identifier.</param>
    /// <returns>The collection.</returns>
    /// <exception cref="KeyNotFoundException">Thrown when the collection does not exist.</exception>
    public BoxSet GetCollection(Guid collectionId)
    {
        return _libraryManager.GetItemById(collectionId) as BoxSet
            ?? throw new KeyNotFoundException($"Collection '{collectionId}' was not found.");
    }

    /// <summary>
    /// Builds bounded context for generation.
    /// Series are terminal items: their seasons and episodes are never traversed or included.
    /// Nested collections are flattened with a cycle guard.
    /// </summary>
    /// <param name="collectionId">Collection identifier.</param>
    /// <param name="maximumItems">Maximum eligible items to include.</param>
    /// <returns>Sanitized context.</returns>
    public CollectionContext BuildContext(Guid collectionId, int maximumItems)
    {
        var collection = GetCollection(collectionId);
        var eligibleItems = new Dictionary<Guid, BaseItem>();
        AddCollectionItems(collection, eligibleItems, new HashSet<Guid>());

        var ordered = eligibleItems.Values
            .OrderBy(item => item.ProductionYear ?? int.MaxValue)
            .ThenBy(item => item.SortName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var boundedMaximum = Math.Clamp(maximumItems, 1, 1000);
        var communityRatings = ordered
            .Select(item => item.CommunityRating)
            .Where(rating => rating is >= 0 and <= 10)
            .Select(rating => rating!.Value)
            .ToArray();

        return new CollectionContext
        {
            CollectionName = LimitText(collection.Name, 500),
            CurrentSortTitle = LimitText(collection.ForcedSortName, 500),
            CurrentContentRating = LimitText(collection.OfficialRating, 100),
            CurrentCommunityRating = collection.CommunityRating,
            AverageCommunityRating = communityRatings.Length == 0
                ? null
                : (float)Math.Round(communityRatings.Average(), 1, MidpointRounding.AwayFromZero),
            CommunityRatingItemCount = communityRatings.Length,
            CurrentOverview = LimitText(collection.Overview, 5000),
            CurrentGenres = Normalize(collection.Genres),
            CurrentTags = Normalize(collection.Tags),
            Items = ordered
                .Take(boundedMaximum)
                .Select(ToContextItem)
                .ToArray(),
            OmittedItemCount = Math.Max(0, ordered.Length - boundedMaximum)
        };
    }

    private static void AddCollectionItems(
        BoxSet collection,
        IDictionary<Guid, BaseItem> output,
        ISet<Guid> visitedCollections)
    {
        if (!visitedCollections.Add(collection.Id))
        {
            return;
        }

        foreach (var child in collection.GetLinkedChildren(DtoOptions.StoredColumnsOnly))
        {
            switch (child)
            {
                case BoxSet nestedCollection:
                    AddCollectionItems(nestedCollection, output, visitedCollections);
                    break;
                case Episode:
                case Season:
                    // TV context is intentionally series-level. Never send episode or season metadata.
                    break;
                case Series:
                    output.TryAdd(child.Id, child);
                    break;
                default:
                    // Other folders are containers rather than collection content and are skipped.
                    if (!child.IsFolder)
                    {
                        output.TryAdd(child.Id, child);
                    }

                    break;
            }
        }
    }

    private static CollectionContextItem ToContextItem(BaseItem item)
    {
        return new CollectionContextItem
        {
            Type = item.GetBaseItemKind().ToString(),
            Name = LimitText(item.Name, 500),
            SortTitle = LimitText(item.ForcedSortName, 500),
            ContentRating = LimitText(item.OfficialRating, 100),
            CommunityRating = item.CommunityRating,
            Year = item.ProductionYear,
            Overview = LimitText(item.Overview, 3000),
            Genres = Normalize(item.Genres),
            Tags = Normalize(item.Tags)
        };
    }

    private static string LimitText(string? value, int maximumLength)
    {
        var normalized = value?.Trim() ?? string.Empty;
        return normalized.Length <= maximumLength
            ? normalized
            : normalized[..maximumLength].TrimEnd();
    }

    private static string[] Normalize(IEnumerable<string>? values)
    {
        return (values ?? [])
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => LimitText(value, 200))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .Take(100)
            .ToArray();
    }
}
