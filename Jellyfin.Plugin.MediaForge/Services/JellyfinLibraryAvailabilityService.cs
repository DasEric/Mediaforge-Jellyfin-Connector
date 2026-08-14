using System.Globalization;
using System.Text;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.MediaForge.Services;

/// <summary>Checks whether requested media is already present in Jellyfin.</summary>
public sealed class JellyfinLibraryAvailabilityService
{
    private const int MaximumTitleCandidates = 200;
    private readonly ILibraryManager _libraryManager;

    public JellyfinLibraryAvailabilityService(ILibraryManager libraryManager)
    {
        _libraryManager = libraryManager;
    }

    public LibraryAvailability GetAvailability(LibraryMediaIdentity identity)
    {
        var itemType = identity.IsMovie ? BaseItemKind.Movie : BaseItemKind.Series;
        var matches = FindMatches(identity, itemType);
        if (identity.IsMovie || matches.Count == 0)
        {
            return new LibraryAvailability(matches.Count > 0, new HashSet<LibraryEpisodeKey>());
        }

        var episodes = _libraryManager.GetItemList(new InternalItemsQuery
        {
            Recursive = true,
            IncludeItemTypes = [BaseItemKind.Episode],
            AncestorIds = matches.Select(item => item.Id).Distinct().ToArray(),
            IsVirtualItem = false,
            EnableTotalRecordCount = false,
        });
        return new LibraryAvailability(true, BuildEpisodeSet(episodes.OfType<Episode>()));
    }

    internal static HashSet<LibraryEpisodeKey> BuildEpisodeSet(IEnumerable<Episode> episodes)
    {
        var output = new HashSet<LibraryEpisodeKey>();
        foreach (var episode in episodes)
        {
            if (!episode.ParentIndexNumber.HasValue || !episode.IndexNumber.HasValue)
            {
                continue;
            }

            var season = episode.ParentIndexNumber.Value;
            var first = episode.IndexNumber.Value;
            var last = episode.IndexNumberEnd.GetValueOrDefault(first);
            if (season < 0 || first < 0 || last < first || last - first > 1000)
            {
                continue;
            }

            for (var number = first; number <= last; number++)
            {
                output.Add(new LibraryEpisodeKey(season, number));
            }
        }

        return output;
    }

    internal static bool ProviderIdsMatch(
        IReadOnlyDictionary<string, string> expected,
        IReadOnlyDictionary<string, string> actual)
        => expected.Any(pair => actual.TryGetValue(pair.Key, out var value)
            && string.Equals(pair.Value, value, StringComparison.OrdinalIgnoreCase));

    internal static string NormalizeTitle(string value)
    {
        var decomposed = (value ?? string.Empty).Normalize(NormalizationForm.FormD);
        var output = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(character))
            {
                output.Append(char.ToLowerInvariant(character));
            }
        }

        return output.ToString();
    }

    private IReadOnlyList<BaseItem> FindMatches(LibraryMediaIdentity identity, BaseItemKind itemType)
    {
        if (identity.ProviderIds.Count > 0)
        {
            var byProvider = _libraryManager.GetItemList(new InternalItemsQuery
            {
                Recursive = true,
                IncludeItemTypes = [itemType],
                HasAnyProviderId = identity.ProviderIds.ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value,
                    StringComparer.OrdinalIgnoreCase),
                IsVirtualItem = false,
                EnableTotalRecordCount = false,
                Limit = MaximumTitleCandidates,
            });
            var exact = byProvider
                .Where(item => ProviderIdsMatch(identity.ProviderIds, item.ProviderIds))
                .ToArray();
            if (exact.Length > 0)
            {
                return exact;
            }
        }

        if (string.IsNullOrWhiteSpace(identity.Title))
        {
            return Array.Empty<BaseItem>();
        }

        var normalizedTitle = NormalizeTitle(identity.Title);
        var byTitle = _libraryManager.GetItemList(new InternalItemsQuery
        {
            Recursive = true,
            IncludeItemTypes = [itemType],
            NameContains = identity.Title.Trim(),
            IsVirtualItem = false,
            EnableTotalRecordCount = false,
            Limit = MaximumTitleCandidates,
        });
        var candidates = byTitle.Where(item =>
        {
            if (!string.Equals(NormalizeTitle(item.Name), normalizedTitle, StringComparison.Ordinal))
            {
                return false;
            }

            if (identity.Year.HasValue && item.ProductionYear != identity.Year)
            {
                return false;
            }

            // A conflicting provider id is stronger evidence than a matching
            // title. Items without those ids may still use the conservative
            // title/year fallback.
            return !identity.ProviderIds.Any(pair => item.ProviderIds.TryGetValue(pair.Key, out var value)
                && !string.Equals(pair.Value, value, StringComparison.OrdinalIgnoreCase));
        }).ToArray();

        if (identity.Year.HasValue || candidates.Length == 1)
        {
            return candidates;
        }

        // Without a year or provider id, duplicate titles are ambiguous and
        // must not suppress a legitimate download.
        return Array.Empty<BaseItem>();
    }
}

public sealed record LibraryMediaIdentity(
    string Title,
    int? Year,
    bool IsMovie,
    IReadOnlyDictionary<string, string> ProviderIds);

public sealed record LibraryAvailability(
    bool ItemExists,
    IReadOnlySet<LibraryEpisodeKey> Episodes);

public readonly record struct LibraryEpisodeKey(int SeasonNumber, int EpisodeNumber);
