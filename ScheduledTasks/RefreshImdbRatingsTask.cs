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
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ImdbRatings.ScheduledTasks;

public class RefreshImdbRatingsTask : IScheduledTask
{
    private readonly ILibraryManager _libraryManager;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ImdbGraphqlClient _imdbGraphqlClient;
    private readonly ShokoClient _shokoClient;
    private readonly TmdbExternalIdsClient _tmdbExternalIdsClient;
    private readonly ILogger<RefreshImdbRatingsTask> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly string _dataPath;

    public RefreshImdbRatingsTask(
        ILibraryManager libraryManager,
        IHttpClientFactory httpClientFactory,
        ImdbGraphqlClient imdbGraphqlClient,
        ShokoClient shokoClient,
        TmdbExternalIdsClient tmdbExternalIdsClient,
        ILogger<RefreshImdbRatingsTask> logger,
        ILoggerFactory loggerFactory,
        MediaBrowser.Common.Configuration.IApplicationPaths applicationPaths)
    {
        _libraryManager = libraryManager;
        _httpClientFactory = httpClientFactory;
        _imdbGraphqlClient = imdbGraphqlClient;
        _shokoClient = shokoClient;
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

        _logger.LogInformation("Starting IMDb ratings refresh (minVotes={MinVotes}, movies={Movies}, series={Series}, otherLibraries={OtherLibraries})",
            config.MinimumVotes, config.IncludeMovies, config.IncludeSeries, config.IncludeOtherLibraries);

        // Step 1: Query library items and build a distinct IMDb ID filter set.
        progress.Report(0);
        var items = GetLibraryItems(config);

        // Step 1.5: Shoko resolution — find anime items with AniDB IDs but no IMDb IDs and
        // resolve them via Shoko Server → TMDB → IMDb.
        var shokoResolvedItems = new List<(BaseItem Item, BaseItem? Parent, string ImdbId)>();
        if (config.EnableShokoResolution
            && !string.IsNullOrWhiteSpace(config.ShokoServerUrl)
            && !string.IsNullOrWhiteSpace(config.ShokoApiKey)
            && !string.IsNullOrWhiteSpace(config.TmdbApiKey))
        {
            shokoResolvedItems = await ResolveShokoItemsAsync(config, cancellationToken).ConfigureAwait(false);
        }

        if (items.Count == 0 && shokoResolvedItems.Count == 0)
        {
            _logger.LogInformation("Found 0 library items with IMDb IDs");
            progress.Report(100);
            return;
        }

        var libraryImdbIds = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < items.Count; i++)
        {
            var imdbId = items[i].GetProviderId(MediaBrowser.Model.Entities.MetadataProvider.Imdb);
            if (!string.IsNullOrWhiteSpace(imdbId))
            {
                libraryImdbIds.Add(imdbId);
            }
        }

        foreach (var (_, _, resolvedId) in shokoResolvedItems)
        {
            libraryImdbIds.Add(resolvedId);
        }

        _logger.LogInformation(
            "Found {ItemCount} library items with IMDb IDs ({DistinctIdCount} distinct IDs, {ShokoCount} resolved via Shoko)",
            items.Count + shokoResolvedItems.Count,
            libraryImdbIds.Count,
            shokoResolvedItems.Count);

        if (libraryImdbIds.Count == 0)
        {
            _logger.LogWarning("No valid IMDb IDs found on selected library items — nothing to update");
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
            var imdbId = item.GetProviderId(MediaBrowser.Model.Entities.MetadataProvider.Imdb);

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

        // Step 4.5: Process Shoko-resolved items against the flat-file ratings.
        if (shokoResolvedItems.Count > 0)
        {
            int shokoQueued = 0;
            int shokoNotFound = 0;
            int shokoSkipped = 0;

            foreach (var (shokoItem, shokoParent, shokoImdbId) in shokoResolvedItems)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!ratings.TryGetValue(shokoImdbId, out var ratingData))
                {
                    shokoNotFound++;
                    continue;
                }

                if (ratingData.Votes < config.MinimumVotes)
                {
                    shokoSkipped++;
                    continue;
                }

                var newRating = ratingData.Rating;
                if (shokoItem.CommunityRating.HasValue && Math.Abs(shokoItem.CommunityRating.Value - newRating) < 0.01f)
                {
                    shokoSkipped++;
                    continue;
                }

                pendingUpdates.Add((shokoItem, shokoParent, shokoItem.CommunityRating, newRating));
                shokoQueued++;
            }

            _logger.LogInformation(
                "Shoko-resolved items: {Queued} queued for update, {NotFound} IMDb ID not in ratings file, {Skipped} skipped",
                shokoQueued,
                shokoNotFound,
                shokoSkipped);
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
    /// Finds items that have AniDB provider IDs but no IMDb IDs, then resolves IMDb IDs via
    /// Shoko Server (AniDB → TMDB) and the TMDB external_ids API (TMDB → IMDb).
    /// If <see cref="PluginConfiguration.SaveResolvedIdsToMetadata"/> is enabled the resolved
    /// IMDb ID is persisted to the item so future scans skip this step automatically.
    /// </summary>
    private async Task<List<(BaseItem Item, BaseItem? Parent, string ImdbId)>> ResolveShokoItemsAsync(
        PluginConfiguration config,
        CancellationToken cancellationToken)
    {
        var result = new List<(BaseItem Item, BaseItem? Parent, string ImdbId)>();

        // Query all Series, Episode and Movie items that lack an IMDb ID.
        var query = new InternalItemsQuery
        {
            HasImdbId = false,
            IsVirtualItem = false,
            Recursive = true,
            IncludeItemTypes = new[] { BaseItemKind.Series, BaseItemKind.Episode, BaseItemKind.Movie }
        };

        var candidates = _libraryManager.GetItemList(query);

        // Keep only items that have an AniDB provider ID set by Shoko.
        var anidbItems = candidates
            .Where(item => !string.IsNullOrWhiteSpace(item.GetProviderId("AniDB")))
            .ToList();

        if (anidbItems.Count == 0)
        {
            _logger.LogInformation("Shoko resolution: no items found with AniDB IDs");
            return result;
        }

        _logger.LogInformation(
            "Shoko resolution: resolving IMDb IDs for {Count} items with AniDB IDs",
            anidbItems.Count);

        for (int i = 0; i < anidbItems.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var item = anidbItems[i];
            var anidbId = item.GetProviderId("AniDB")!;
            var parent = item.GetParent();

            // Apply a delay before each iteration (after the first) to respect TMDB rate limits.
            if (i > 0 && config.TmdbRequestDelayMs > 0)
            {
                await Task.Delay(config.TmdbRequestDelayMs, cancellationToken).ConfigureAwait(false);
            }

            // Step A: Shoko → TMDB
            ShokoTmdbIds? tmdbIds;
            if (item is Episode)
            {
                tmdbIds = await _shokoClient
                    .GetEpisodeTmdbIdsAsync(config.ShokoServerUrl, config.ShokoApiKey, anidbId, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                // Series and Movie items both map to a Shoko Series entry.
                tmdbIds = await _shokoClient
                    .GetSeriesTmdbIdsAsync(config.ShokoServerUrl, config.ShokoApiKey, anidbId, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (tmdbIds is null)
            {
                continue;
            }

            // Step B: TMDB → IMDb  (prefer Movie IDs for Movie items, Show IDs for everything else)
            string? imdbId = null;

            if (item is Movie)
            {
                foreach (var movieId in tmdbIds.MovieIds)
                {
                    imdbId = await _tmdbExternalIdsClient
                        .GetImdbIdForMovieAsync(movieId, config.TmdbApiKey, cancellationToken)
                        .ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(imdbId))
                    {
                        break;
                    }
                }

                if (string.IsNullOrWhiteSpace(imdbId))
                {
                    foreach (var showId in tmdbIds.ShowIds)
                    {
                        imdbId = await _tmdbExternalIdsClient
                            .GetImdbIdForShowAsync(showId, config.TmdbApiKey, cancellationToken)
                            .ConfigureAwait(false);
                        if (!string.IsNullOrWhiteSpace(imdbId))
                        {
                            break;
                        }
                    }
                }
            }
            else
            {
                foreach (var showId in tmdbIds.ShowIds)
                {
                    imdbId = await _tmdbExternalIdsClient
                        .GetImdbIdForShowAsync(showId, config.TmdbApiKey, cancellationToken)
                        .ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(imdbId))
                    {
                        break;
                    }
                }

                if (string.IsNullOrWhiteSpace(imdbId))
                {
                    foreach (var movieId in tmdbIds.MovieIds)
                    {
                        imdbId = await _tmdbExternalIdsClient
                            .GetImdbIdForMovieAsync(movieId, config.TmdbApiKey, cancellationToken)
                            .ConfigureAwait(false);
                        if (!string.IsNullOrWhiteSpace(imdbId))
                        {
                            break;
                        }
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(imdbId))
            {
                _logger.LogDebug(
                    "Shoko resolution: could not resolve IMDb ID for AniDB {AniDbId} ({Name})",
                    anidbId,
                    item.Name);
                continue;
            }

            _logger.LogDebug(
                "Shoko resolution: resolved {ImdbId} for AniDB {AniDbId} ({Name})",
                imdbId,
                anidbId,
                item.Name);

            // Step C: Optionally persist the resolved IMDb ID to the item's metadata.
            if (config.SaveResolvedIdsToMetadata)
            {
                item.SetProviderId(MetadataProvider.Imdb, imdbId);
                try
                {
                    await _libraryManager
                        .UpdateItemAsync(item, parent, ItemUpdateType.MetadataEdit, cancellationToken)
                        .ConfigureAwait(false);

                    _logger.LogDebug(
                        "Shoko resolution: saved IMDb ID {ImdbId} to metadata for {Name}",
                        imdbId,
                        item.Name);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(
                        ex,
                        "Shoko resolution: failed to save IMDb ID {ImdbId} to metadata for {Name}",
                        imdbId,
                        item.Name);
                    // Revert the in-memory change so the item is not left in a half-saved state.
                    item.ProviderIds.Remove("Imdb");
                }
            }

            result.Add((item, parent, imdbId));
        }

        _logger.LogInformation(
            "Shoko resolution complete: {Resolved} of {Total} AniDB items resolved to an IMDb ID",
            result.Count,
            anidbItems.Count);

        return result;
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

        // When IncludeOtherLibraries is enabled, skip item-type filtering entirely so that
        // all libraries (Anime, Mixed, etc.) are included alongside Movies and TV Shows.
        if (config.IncludeOtherLibraries)
        {
            return _libraryManager.GetItemList(query);
        }

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

        return _libraryManager.GetItemList(query);
    }
}
