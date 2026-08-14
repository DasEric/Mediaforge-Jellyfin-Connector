using System.Net;
using System.Text.Json;

namespace Jellyfin.Plugin.MediaForge.Services;

/// <summary>Strictly parses MediaForge episode responses before queue planning.</summary>
internal static class MediaForgeEpisodeParser
{
    private const int FetchAttempts = 2;

    internal static async Task<IReadOnlyList<MediaForgeEpisode>> FetchCompleteAsync(
        Func<CancellationToken, Task<JsonElement>> fetch,
        Action<JsonElement> observe,
        int? fallbackSeasonNumber,
        int expectedCount,
        CancellationToken cancellationToken)
    {
        var actualCount = 0;
        for (var attempt = 0; attempt < FetchAttempts; attempt++)
        {
            var response = await fetch(cancellationToken).ConfigureAwait(false);
            observe(response);

            IReadOnlyList<MediaForgeEpisode> episodes;
            try
            {
                episodes = Parse(response, fallbackSeasonNumber);
            }
            catch (MediaForgeException) when (attempt + 1 < FetchAttempts)
            {
                continue;
            }

            actualCount = episodes.Count;
            if (SatisfiesExpectedCount(expectedCount, actualCount))
            {
                return episodes;
            }
        }

        throw new MediaForgeException(
            HttpStatusCode.BadGateway,
            $"MediaForge hat nur {actualCount} von {expectedCount} erwarteten Episoden geliefert. Es wurde nichts eingereiht; bitte erneut versuchen.");
    }

    internal static IReadOnlyList<MediaForgeEpisode> Parse(JsonElement response, int? fallbackSeasonNumber)
    {
        if (response.ValueKind != JsonValueKind.Object
            || !response.TryGetProperty("episodes", out var episodes)
            || episodes.ValueKind != JsonValueKind.Array)
        {
            throw InvalidEpisodeList();
        }

        var parsed = new List<MediaForgeEpisode>(episodes.GetArrayLength());
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var episode in episodes.EnumerateArray())
        {
            if (episode.ValueKind != JsonValueKind.Object
                || !episode.TryGetProperty("url", out var urlValue)
                || urlValue.ValueKind != JsonValueKind.String
                || !MediaAccessGrantStore.TryNormalizeUrl(urlValue.GetString() ?? string.Empty, out var url)
                || !seen.Add(url))
            {
                throw InvalidEpisodeList();
            }

            var episodeNumber = ReadOptionalInt(episode, "episode_number");
            var seasonNumber = ReadOptionalInt(episode, "season_number") ?? fallbackSeasonNumber;
            var languages = new HashSet<string>(StringComparer.Ordinal);
            if (episode.TryGetProperty("languages", out var languageValues))
            {
                if (languageValues.ValueKind != JsonValueKind.Array)
                {
                    throw InvalidEpisodeList();
                }

                foreach (var languageValue in languageValues.EnumerateArray().Take(32))
                {
                    if (languageValue.ValueKind != JsonValueKind.String)
                    {
                        throw InvalidEpisodeList();
                    }

                    var language = languageValue.GetString()?.Trim() ?? string.Empty;
                    if (language.Length is > 0 and <= 100 && !language.Any(char.IsControl))
                    {
                        languages.Add(language);
                    }
                }
            }

            parsed.Add(new MediaForgeEpisode(url, seasonNumber, episodeNumber, languages));
        }

        return parsed;
    }

    internal static bool SatisfiesExpectedCount(int? expectedCount, int actualCount)
        => !expectedCount.HasValue || expectedCount.Value <= 0 || actualCount >= expectedCount.Value;

    private static int? ReadOptionalInt(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var numeric))
        {
            return numeric;
        }

        return value.ValueKind == JsonValueKind.String
            && int.TryParse(value.GetString(), out numeric)
            ? numeric
            : null;
    }

    private static MediaForgeException InvalidEpisodeList()
        => new(
            HttpStatusCode.BadGateway,
            "MediaForge hat eine unvollst\u00e4ndige oder ung\u00fcltige Episodenliste geliefert. Es wurde nichts eingereiht.");
}

internal sealed record MediaForgeEpisode(
    string Url,
    int? SeasonNumber,
    int? EpisodeNumber,
    IReadOnlySet<string> Languages);
