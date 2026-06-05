using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ImdbRatings.Providers;

/// <summary>
/// A single cached entry mapping an AniDB entity (series or episode) to its resolved IMDb ID
/// and the resolved TMDB IDs used to obtain it.
/// </summary>
public sealed class AnimeIdCacheEntry
{
    /// <summary>
    /// Cache key — "S:{anidbSeriesId}" for series/season/movie items, "E:{anidbEpisodeId}" for episode items.
    /// </summary>
    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    /// <summary>
    /// The AniDB ID as stored in Jellyfin's provider metadata.
    /// </summary>
    [JsonPropertyName("anidbId")]
    public string AniDbId { get; set; } = string.Empty;

    /// <summary>
    /// Resolved IMDb ID (e.g. "tt1234567"), or null if resolution failed.
    /// </summary>
    [JsonPropertyName("imdbId")]
    public string? ImdbId { get; set; }

    /// <summary>
    /// Detected content type: "series", "movie", or "episode".
    /// </summary>
    [JsonPropertyName("contentType")]
    public string? ContentType { get; set; }

    /// <summary>
    /// TMDB show IDs returned by Shoko for this entry.
    /// </summary>
    [JsonPropertyName("tmdbShowIds")]
    public int[] TmdbShowIds { get; set; } = Array.Empty<int>();

    /// <summary>
    /// TMDB movie IDs returned by Shoko for this entry.
    /// </summary>
    [JsonPropertyName("tmdbMovieIds")]
    public int[] TmdbMovieIds { get; set; } = Array.Empty<int>();

    /// <summary>
    /// UTC timestamp when this entry was last resolved.
    /// </summary>
    [JsonPropertyName("resolvedAt")]
    public DateTimeOffset ResolvedAt { get; set; }
}

/// <summary>
/// Persistent JSON cache that stores AniDB → IMDb ID mappings for anime items resolved via
/// the Shoko → TMDB pipeline.  Loading and saving are explicit operations; the caller is
/// responsible for calling <see cref="LoadAsync"/> before resolution and
/// <see cref="SaveAsync"/> after resolution.
/// </summary>
public sealed class AnimeIdCache
{
    private const string CacheFileName = "imdb-anime-cache.json";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _cachePath;
    private readonly ILogger _logger;
    private Dictionary<string, AnimeIdCacheEntry> _entries;

    public AnimeIdCache(string dataPath, ILogger logger)
    {
        _cachePath = Path.Combine(dataPath, CacheFileName);
        _logger = logger;
        _entries = new Dictionary<string, AnimeIdCacheEntry>(StringComparer.Ordinal);
    }

    /// <summary>Number of entries currently in memory.</summary>
    public int Count => _entries.Count;

    /// <summary>Full path to the cache file on disk.</summary>
    public string CachePath => _cachePath;

    /// <summary>
    /// Returns the cache key for a series/season/movie AniDB ID.
    /// </summary>
    public static string SeriesKey(string anidbId) => $"S:{anidbId}";

    /// <summary>
    /// Returns the cache key for an episode AniDB ID.
    /// </summary>
    public static string EpisodeKey(string anidbId) => $"E:{anidbId}";

    /// <summary>
    /// Loads the cache from disk.  If the file does not exist or cannot be read the cache
    /// starts empty without throwing.
    /// </summary>
    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_cachePath))
        {
            _logger.LogDebug("Anime ID cache file not found at {Path}; starting with empty cache", _cachePath);
            return;
        }

        try
        {
            await using var stream = File.OpenRead(_cachePath);
            var data = await JsonSerializer
                .DeserializeAsync<CacheFile>(stream, JsonOptions, cancellationToken)
                .ConfigureAwait(false);

            _entries = data?.Entries
                ?? new Dictionary<string, AnimeIdCacheEntry>(StringComparer.Ordinal);

            _logger.LogInformation("Anime ID cache loaded: {Count} entries from {Path}", _entries.Count, _cachePath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load anime ID cache from {Path}; starting with empty cache", _cachePath);
            _entries = new Dictionary<string, AnimeIdCacheEntry>(StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// Saves the current cache to disk atomically via a temp file.
    /// </summary>
    public async Task SaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            var data = new CacheFile { Entries = _entries };
            var tempPath = _cachePath + ".tmp";

            await using (var stream = File.Create(tempPath))
            {
                await JsonSerializer.SerializeAsync(stream, data, JsonOptions, cancellationToken).ConfigureAwait(false);
            }

            File.Move(tempPath, _cachePath, overwrite: true);
            _logger.LogInformation("Anime ID cache saved: {Count} entries to {Path}", _entries.Count, _cachePath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save anime ID cache to {Path}", _cachePath);
        }
    }

    /// <summary>
    /// Looks up an entry by cache key.  Returns <c>true</c> and sets <paramref name="entry"/>
    /// when found.
    /// </summary>
    public bool TryGetEntry(string key, out AnimeIdCacheEntry? entry)
        => _entries.TryGetValue(key, out entry);

    /// <summary>
    /// Inserts or updates a cache entry.
    /// </summary>
    public void SetEntry(AnimeIdCacheEntry entry)
        => _entries[entry.Key] = entry;

    /// <summary>
    /// Removes all entries from the in-memory cache.  Call <see cref="SaveAsync"/> to persist
    /// the cleared state to disk.
    /// </summary>
    public void Clear()
    {
        var count = _entries.Count;
        _entries.Clear();
        _logger.LogInformation("Anime ID cache cleared ({Count} entries removed)", count);
    }

    private sealed class CacheFile
    {
        [JsonPropertyName("entries")]
        public Dictionary<string, AnimeIdCacheEntry> Entries { get; set; }
            = new(StringComparer.Ordinal);
    }
}
