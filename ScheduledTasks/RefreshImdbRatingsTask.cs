using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.ImdbRatings.Configuration;
using Jellyfin.Plugin.ImdbRatings.Providers;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ImdbRatings.ScheduledTasks;

public class RefreshImdbRatingsTask : IScheduledTask
{
    // Shokofin stores TMDb IDs under "TheMovieDb" for series and "Tmdb" for movies/episodes.
    // We check both so items from Shoko VFS libraries are discovered regardless of which key was used.
    private static readonly string[] TmdbProviderKeys = ["TheMovieDb", "Tmdb"];

    private readonly ILibraryManager _libraryManager;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ImdbGraphqlClient _imdbGraphqlClient;
    private readonly TmdbExternalIdsClient _tmdbExternalIdsClient;
    private readonly ILogger<RefreshImdbRatingsTask> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly string _dataPath;

    public RefreshImdbRatingsTask(
        ILibraryManager libraryManager,
        IHttpClientFactory httpClientFactory,
        ImdbGraphqlClient imdbGraphqlClient,
        TmdbExternalIdsClient tmdbExternalIdsClient,
        ILogger<RefreshImdbRatingsTask> logger,
        ILoggerFactory loggerFactory,
        MediaBrowser.Common.Configuration.IApplicationPaths applicationPaths)
    {
        _libraryManager = libraryManager;
        _httpClientFactory = httpClientFactory;
        _imdbGraphqlClient = imdbGraphqlClient;
        _tmdbExternalIdsClient = tmdbExternalIdsClient;
        _logger = logger;
        _loggerFactory = loggerFactory;
        _dataPath = applicationPaths.DataPath;
    }

    public string Name => "Refresh IMDb Ratings";

    public string Key => "RefreshImdbRatings";

    public string Description => "Downloads the IMDb ratings flat file and updates CommunityRating on all library items with an IMDb ID.";

    public string Category => "IMDb Ratings";

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return new[]
        {
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.DailyTrigger,
                TimeOfDayTicks = TimeSpan.FromHours(3).Ticks
            }
        };
    }

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var cacheMaxAge = TimeSpan.FromHours(config.FlatFileCacheHours);

        _logger.LogInformation(
            "Starting IMDb ratings refresh (minVotes={MinVotes}, movies={Movies}, series={Series}, otherLibraries={OtherLibraries}, tmdbResolution={TmdbResolution})",
            config.MinimumVotes, config.IncludeMovies, config.IncludeSeries, config.IncludeOtherLibraries, config.EnableTmdbIdResolution);

        // Step 1: Query library items.
        progress.Report(0);
        var items = GetLibraryItems(config);
        if (items.Count == 0)
        {
            _logger.LogInformation("Found 0 library items eligible for IMDb rating updates");
            progress.Report(100);
            return;
        }

        // Step 1b: For items without an IMDb ID, try to resolve one via TMDb if enabled.
        // Maps item.Id → resolved IMDb ID for use throughout this task run.
        var tmdbResolvedImdbIds = new Dictionary<Guid, string>();
        if (config.EnableTmdbIdResolution && !string.IsNullOrWhiteSpace(config.TmdbApiKey))
        {
            await ResolveTmdbImdbIdsAsync(items, config, tmdbResolvedImdbIds, cancellationToken).ConfigureAwait(false);
        }

        // Build a distinct set of IMDb IDs from items (own or TMDb-resolved).
        var libraryImdbIds = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < items.Count; i++)
        {
            var imdbId = GetEffectiveImdbId(items[i], tmdbResolvedImdbIds);
            if (!string.IsNullOrWhiteSpace(imdbId))
            {
                libraryImdbIds.Add(imdbId);
            }
        }

        _logger.LogInformation(
            "Found {ItemCount} library items ({DistinctIdCount} distinct IMDb IDs, {TmdbResolved} resolved from TMDb)",
            items.Count,
            libraryImdbIds.Count,
            tmdbResolvedImdbIds.Count);

        if (libraryImdbIds.Count == 0)
        {
            _logger.LogWarning(
                "No valid IMDb IDs found on selected library items — nothing to update. " +
                "If using Shoko Server, enable 'TMDb ID Resolution' in plugin settings and provide a TMDb API key.");
            progress.Report(100);
            return;
        }

        progress.Report(5);

        // Step 2: Download/cache the ratings file, Step 3: Parse ratings (filtered to library IMDb IDs)
        var downloader = new ImdbFlatFileDownloader(
            _httpClientFactory,
            _loggerFactory.CreateLogger<ImdbFlatFileDownloader>(),
            _dataPath);
        var parser = new ImdbRatingsParser(_loggerFactory.CreateLogger<ImdbRatingsParser>());

        var ratings = await DownloadAndParseWithRetryAsync(
            downloader,
            parser,
            libraryImdbIds,
            cacheMaxAge,
            progress,
            cancellationToken).ConfigureAwait(false);
        progress.Report(30);
        int lastScanProgressBucket = 30;

        // Step 4: Identify items that need rating updates (without mutating in-memory state)
        var pendingUpdates = new List<(BaseItem Item, BaseItem? Parent, float? OldRating, float NewRating)>();
        int skippedMissingImdbId = 0;
        int skippedBelowMinimumVotes = 0;
        int skippedUnchanged = 0;
        int notFound = 0;
        var fallbackItems = new List<(BaseItem Item, BaseItem? Parent, string ImdbId)>();
        int fallbackFound = 0;
        int fallbackNotFound = 0;
        int fallbackBelowMinimumVotes = 0;
        int fallbackUnchanged = 0;
        const int debugSampleLimitPerCategory = 10;
        bool enableItemDebugLogging = config.EnableItemDebugLogging && _logger.IsEnabled(LogLevel.Debug);
        int loggedNotFoundDebugSamples = 0;
        int loggedBelowMinimumDebugSamples = 0;

        for (int i = 0; i < items.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var item = items[i];
            var imdbId = GetEffectiveImdbId(item, tmdbResolvedImdbIds);

            if (string.IsNullOrEmpty(imdbId))
            {
                skippedMissingImdbId++;
            }
            else if (!ratings.TryGetValue(imdbId, out var ratingData))
            {
                if (enableItemDebugLogging && loggedNotFoundDebugSamples < debugSampleLimitPerCategory)
                {
                    loggedNotFoundDebugSamples++;
                    _logger.LogDebug("IMDb ID {ImdbId} not found in ratings file for \"{Name}\"", imdbId, item.Name);
                }
                notFound++;
                fallbackItems.Add((item, item.GetParent(), imdbId));
            }
            else if (ratingData.Votes < config.MinimumVotes)
            {
                if (enableItemDebugLogging && loggedBelowMinimumDebugSamples < debugSampleLimitPerCategory)
                {
                    loggedBelowMinimumDebugSamples++;
                    _logger.LogDebug("Skipping \"{Name}\" — {Votes} votes below minimum {MinVotes}", item.Name, ratingData.Votes, config.MinimumVotes);
                }
                skippedBelowMinimumVotes++;
            }
            else
            {
                var newRating = ratingData.Rating;
                if (item.CommunityRating.HasValue && Math.Abs(item.CommunityRating.Value - newRating) < 0.01f)
                {
                    skippedUnchanged++;
                }
                else
                {
                    pendingUpdates.Add((item, item.GetParent(), item.CommunityRating, newRating));
                }
            }

            double progressPercent = 30 + (60.0 * (i + 1) / items.Count);
            int progressBucket = (int)progressPercent;
            if (progressBucket > lastScanProgressBucket)
            {
                lastScanProgressBucket = progressBucket;
                progress.Report(progressPercent);
            }
        }

        if (enableItemDebugLogging)
        {
            var suppressedNotFoundDebugLines = notFound - loggedNotFoundDebugSamples;
            if (suppressedNotFoundDebugLines > 0)
            {
                _logger.LogDebug(
                    "Suppressed {Count} additional per-item debug logs for IMDb IDs not found in ratings data (sample limit {SampleLimit})",
                    suppressedNotFoundDebugLines,
                    debugSampleLimitPerCategory);
            }

            var suppressedBelowMinimumDebugLines = skippedBelowMinimumVotes - loggedBelowMinimumDebugSamples;
            if (suppressedBelowMinimumDebugLines > 0)
            {
                _logger.LogDebug(
                    "Suppressed {Count} additional per-item debug logs for items below minimum votes (sample limit {SampleLimit})",
                    suppressedBelowMinimumDebugLines,
                    debugSampleLimitPerCategory);
            }
        }

        if (config.EnableImdbFallback)
        {
            _logger.LogInformation("Looking up {Count} not-found items via IMDb fallback", fallbackItems.Count);

            for (int i = 0; i < fallbackItems.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (i > 0 && config.ImdbFallbackRequestDelayMs > 0)
                {
                    await Task.Delay(config.ImdbFallbackRequestDelayMs, cancellationToken).ConfigureAwait(false);
                }

                var fallbackItem = fallbackItems[i];
                var imdbRating = await _imdbGraphqlClient
                    .FetchRatingAsync(fallbackItem.ImdbId, cancellationToken)
                    .ConfigureAwait(false);

                if (!imdbRating.HasValue)
                {
                    fallbackNotFound++;
                    continue;
                }

                if (imdbRating.Value.Votes < config.MinimumVotes)
                {
                    fallbackBelowMinimumVotes++;
                    continue;
                }

                if (fallbackItem.Item.CommunityRating.HasValue
                    && Math.Abs(fallbackItem.Item.CommunityRating.Value - imdbRating.Value.Rating) < 0.01f)
                {
                    fallbackUnchanged++;
                    continue;
                }

                pendingUpdates.Add((fallbackItem.Item, fallbackItem.Parent, fallbackItem.Item.CommunityRating, imdbRating.Value.Rating));
                fallbackFound++;
            }

            _logger.LogInformation(
                "IMDb fallback complete: {Found} found, {NotFound} not found, {BelowMinimum} below minimum votes, {Unchanged} unchanged",
                fallbackFound,
                fallbackNotFound,
                fallbackBelowMinimumVotes,
                fallbackUnchanged);
        }

        progress.Report(90);

        // Step 5: Apply ratings and batch save, grouped by parent and chunked
        if (pendingUpdates.Count > 0)
        {
            _logger.LogInformation("Batch saving {Count} updated ratings to database", pendingUpdates.Count);

            const int batchSize = 500;
            var byParent = pendingUpdates.GroupBy(p => p.Parent?.Id ?? Guid.Empty);
            int saved = 0;
            int lastSaveProgressBucket = 90;

            foreach (var group in byParent)
            {
                var parent = group.First().Parent;

                foreach (var chunk in group.Chunk(batchSize))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (parent is null)
                    {
                        // Preserve prior semantics for root/null-parent items.
                        for (int j = 0; j < chunk.Length; j++)
                        {
                            chunk[j].Item.CommunityRating = chunk[j].NewRating;
                            try
                            {
                                await _libraryManager.UpdateItemAsync(
                                    chunk[j].Item,
                                    chunk[j].Parent!, // Preserve prior behavior for root items with no parent.
                                    ItemUpdateType.MetadataEdit,
                                    cancellationToken).ConfigureAwait(false);
                            }
                            catch
                            {
                                chunk[j].Item.CommunityRating = chunk[j].OldRating;
                                throw;
                            }
                        }
                    }
                    else
                    {
                        // Apply ratings immediately before persisting this chunk.
                        var chunkItems = new BaseItem[chunk.Length];
                        for (int j = 0; j < chunk.Length; j++)
                        {
                            chunk[j].Item.CommunityRating = chunk[j].NewRating;
                            chunkItems[j] = chunk[j].Item;
                        }

                        try
                        {
                            await _libraryManager.UpdateItemsAsync(chunkItems, parent, ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
                        }
                        catch
                        {
                            // Revert this chunk's in-memory mutations if the batch save fails/cancels.
                            for (int j = 0; j < chunk.Length; j++)
                            {
                                chunk[j].Item.CommunityRating = chunk[j].OldRating;
                            }

                            throw;
                        }
                    }

                    saved += chunk.Length;

                    double saveProgress = 90 + (10.0 * saved / pendingUpdates.Count);
                    int saveProgressBucket = (int)saveProgress;
                    if (saveProgressBucket > lastSaveProgressBucket)
                    {
                        lastSaveProgressBucket = saveProgressBucket;
                        progress.Report(saveProgress);
                    }
                }
            }
        }

        progress.Report(100);
        var skippedTotal = skippedMissingImdbId + skippedBelowMinimumVotes + skippedUnchanged;
        _logger.LogInformation(
            "IMDb ratings refresh complete: {Updated} updated, {Skipped} skipped ({Unchanged} unchanged, {BelowMinimum} below minimum votes, {MissingImdbId} missing IMDb ID), {NotFound} not found in IMDb ratings",
            pendingUpdates.Count,
            skippedTotal,
            skippedUnchanged,
            skippedBelowMinimumVotes,
            skippedMissingImdbId,
            notFound);
    }

    /// <summary>
    /// Returns the effective IMDb ID for an item: its own ID if set, otherwise a TMDb-resolved one.
    /// </summary>
    private static string? GetEffectiveImdbId(BaseItem item, IReadOnlyDictionary<Guid, string> tmdbResolvedImdbIds)
    {
        var ownId = item.GetProviderId(MetadataProvider.Imdb);
        if (!string.IsNullOrEmpty(ownId))
        {
            return ownId;
        }

        tmdbResolvedImdbIds.TryGetValue(item.Id, out var resolved);
        return resolved;
    }

    /// <summary>
    /// For every item in <paramref name="items"/> that has no IMDb ID but has a TMDb ID, calls the
    /// TMDb external IDs API to resolve an IMDb ID and stores it in <paramref name="resolvedMap"/>.
    /// </summary>
    private async Task ResolveTmdbImdbIdsAsync(
        IReadOnlyList<BaseItem> items,
        PluginConfiguration config,
        Dictionary<Guid, string> resolvedMap,
        CancellationToken cancellationToken)
    {
        // Collect candidates: items that need resolution and have at least one TMDb key.
        var candidates = new List<(BaseItem Item, string TmdbId, bool IsTvShow)>();
        foreach (var item in items)
        {
            if (!string.IsNullOrEmpty(item.GetProviderId(MetadataProvider.Imdb)))
            {
                continue; // Already has IMDb ID — skip.
            }

            string? tmdbId = null;
            foreach (var key in TmdbProviderKeys)
            {
                tmdbId = item.GetProviderId(key);
                if (!string.IsNullOrEmpty(tmdbId))
                {
                    break;
                }
            }

            if (string.IsNullOrEmpty(tmdbId))
            {
                continue; // No TMDb ID — cannot resolve.
            }

            // Episodes rarely have IMDb IDs in TMDb; skip them to avoid unnecessary API calls.
            bool isTvShow = item is MediaBrowser.Controller.Entities.TV.Series
                            || item is MediaBrowser.Controller.Entities.TV.Season;
            bool isMovie = item is MediaBrowser.Controller.Entities.Movies.Movie;

            if (!isTvShow && !isMovie)
            {
                continue;
            }

            candidates.Add((item, tmdbId, isTvShow));
        }

        if (candidates.Count == 0)
        {
            return;
        }

        _logger.LogInformation("Resolving IMDb IDs from TMDb for {Count} item(s) without IMDb IDs", candidates.Count);

        int resolved = 0;
        int notResolved = 0;

        for (int i = 0; i < candidates.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (i > 0 && config.TmdbRequestDelayMs > 0)
            {
                await Task.Delay(config.TmdbRequestDelayMs, cancellationToken).ConfigureAwait(false);
            }

            var (item, tmdbId, isTvShow) = candidates[i];
            var imdbId = await _tmdbExternalIdsClient
                .FetchImdbIdAsync(tmdbId, isTvShow, config.TmdbApiKey, cancellationToken)
                .ConfigureAwait(false);

            if (!string.IsNullOrEmpty(imdbId))
            {
                resolvedMap[item.Id] = imdbId;
                resolved++;
            }
            else
            {
                notResolved++;
            }
        }

        _logger.LogInformation(
            "TMDb ID resolution complete: {Resolved} resolved, {NotResolved} not resolved",
            resolved,
            notResolved);
    }

    private async Task<Dictionary<string, (float Rating, int Votes)>> DownloadAndParseWithRetryAsync(
        ImdbFlatFileDownloader downloader,
        ImdbRatingsParser parser,
        IReadOnlySet<string> includeImdbIds,
        TimeSpan cacheMaxAge,
        IProgress<double> progress,
        CancellationToken cancellationToken)
    {
        try
        {
            var filePath = await GetRatingsFilePathWithTransientRetryAsync(downloader, cacheMaxAge, cancellationToken).ConfigureAwait(false);
            progress.Report(10);
            return await parser.ParseFilteredAsync(filePath, includeImdbIds, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidDataException ex)
        {
            // Bad data on disk — invalidate cache and re-download.
            _logger.LogWarning(ex,
                "IMDb ratings data failed validation on first attempt; invalidating cache and retrying");

            downloader.InvalidateCache();
            return await RetryDownloadAndParseAsync(
                downloader,
                parser,
                includeImdbIds,
                cacheMaxAge,
                progress,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<Dictionary<string, (float Rating, int Votes)>> RetryDownloadAndParseAsync(
        ImdbFlatFileDownloader downloader,
        ImdbRatingsParser parser,
        IReadOnlySet<string> includeImdbIds,
        TimeSpan cacheMaxAge,
        IProgress<double> progress,
        CancellationToken cancellationToken)
    {
        try
        {
            var filePath = await downloader.GetRatingsFilePathAsync(cacheMaxAge, cancellationToken).ConfigureAwait(false);
            progress.Report(10);
            return await parser.ParseFilteredAsync(filePath, includeImdbIds, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidDataException retryEx)
        {
            _logger.LogError(retryEx, "IMDb ratings data failed validation after retry");
            throw;
        }
    }

    private async Task<string> GetRatingsFilePathWithTransientRetryAsync(
        ImdbFlatFileDownloader downloader,
        TimeSpan cacheMaxAge,
        CancellationToken cancellationToken)
    {
        try
        {
            return await downloader.GetRatingsFilePathAsync(cacheMaxAge, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsTransientNetworkError(ex))
        {
            // Transient download error — try once more after a short delay, or fall back to stale cache.
            _logger.LogWarning(ex, "Transient network error downloading IMDb ratings; retrying once after delay");

            await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);

            try
            {
                return await downloader.GetRatingsFilePathAsync(cacheMaxAge, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception retryEx) when (IsTransientNetworkError(retryEx))
            {
                if (!downloader.HasCacheFile)
                {
                    _logger.LogError(retryEx, "Download failed after retry and no cached ratings file exists");
                    throw;
                }

                _logger.LogWarning(retryEx,
                    "Download failed after retry; falling back to stale cache at {Path}", downloader.CachePath);

                return downloader.CachePath;
            }
        }
    }

    private static bool IsTransientNetworkError(Exception ex)
    {
        return ex is HttpRequestException
            || (ex is IOException && ex is not InvalidDataException);
    }

    private IReadOnlyList<BaseItem> GetLibraryItems(PluginConfiguration config)
    {
        var query = new InternalItemsQuery
        {
            HasImdbId = true,
            IsVirtualItem = false,
            Recursive = true
        };

        IReadOnlyList<BaseItem> items;

        // When IncludeOtherLibraries is enabled, skip item-type filtering entirely so that
        // all libraries (Anime, Mixed, etc.) are included alongside Movies and TV Shows.
        if (config.IncludeOtherLibraries)
        {
            items = _libraryManager.GetItemList(query);
        }
        else
        {
            var includeTypes = new List<BaseItemKind>();
            if (config.IncludeMovies)
            {
                includeTypes.Add(BaseItemKind.Movie);
            }

            if (config.IncludeSeries)
            {
                includeTypes.Add(BaseItemKind.Series);
                includeTypes.Add(BaseItemKind.Episode);
            }

            if (includeTypes.Count == 0)
            {
                _logger.LogWarning("No library types selected — nothing to update");
                return Array.Empty<BaseItem>();
            }

            query.IncludeItemTypes = includeTypes.ToArray();
            items = _libraryManager.GetItemList(query);
        }

        // When TMDb ID resolution is enabled, also retrieve items that have a TMDb ID but no IMDb ID.
        // Shokofin uses "TheMovieDb" for series and "Tmdb" for movies, so we query both keys.
        if (config.EnableTmdbIdResolution && !string.IsNullOrWhiteSpace(config.TmdbApiKey))
        {
            var tmdbOnlyItems = GetTmdbOnlyItems(config);
            if (tmdbOnlyItems.Count > 0)
            {
                var existingIds = new HashSet<Guid>(items.Select(i => i.Id));
                var combined = new List<BaseItem>(items);
                foreach (var tmdbItem in tmdbOnlyItems)
                {
                    if (existingIds.Add(tmdbItem.Id))
                    {
                        combined.Add(tmdbItem);
                    }
                }

                return combined;
            }
        }

        return items;
    }

    /// <summary>
    /// Queries items that have a TMDb provider ID (either "TheMovieDb" or "Tmdb") but no IMDb ID.
    /// These are typically items managed by Shoko/Shokofin in a VFS library.
    /// </summary>
    private List<BaseItem> GetTmdbOnlyItems(PluginConfiguration config)
    {
        BaseItemKind[]? typeFilter = null;
        if (!config.IncludeOtherLibraries)
        {
            var types = new List<BaseItemKind>();
            if (config.IncludeMovies) types.Add(BaseItemKind.Movie);
            if (config.IncludeSeries)
            {
                types.Add(BaseItemKind.Series);
                types.Add(BaseItemKind.Episode);
            }

            if (types.Count > 0)
            {
                typeFilter = types.ToArray();
            }
        }

        var result = new List<BaseItem>();
        var seenIds = new HashSet<Guid>();

        foreach (var tmdbKey in TmdbProviderKeys)
        {
            var tmdbQuery = new InternalItemsQuery
            {
                HasAnyProviderId = new Dictionary<string, string> { { tmdbKey, string.Empty } },
                IsVirtualItem = false,
                Recursive = true
            };

            if (typeFilter is not null)
            {
                tmdbQuery.IncludeItemTypes = typeFilter;
            }

            var tmdbItems = _libraryManager.GetItemList(tmdbQuery);
            foreach (var item in tmdbItems)
            {
                // Only add items that don't already have an IMDb ID (those are already in the main list).
                if (seenIds.Add(item.Id) && string.IsNullOrEmpty(item.GetProviderId(MetadataProvider.Imdb)))
                {
                    result.Add(item);
                }
            }
        }

        return result;
    }
}
