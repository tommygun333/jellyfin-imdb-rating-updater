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
            shokoResolvedItems = await ResolveShokoItemsAsync(config, items, cancellationToken).ConfigureAwait(false);
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
        var pendingUpdates = new List<(BaseItem Item, BaseItem? Parent, float? OldRating, float? NewRating)>();
        int skippedMissingImdbId = 0;
        int skippedBelowMinimumVotes = 0;
        int skippedUnchanged = 0;
        int notFound = 0;
        int noRatingDashApplied = 0;
        var fallbackItems = new List<(BaseItem Item, BaseItem? Parent, string ImdbId)>();
        int fallbackFound = 0;
        int fallbackNotFound = 0;
        int fallbackBelowMinimumVotes = 0;
        int fallbackUnchanged = 0;
        var itemsToApplyDash = new HashSet<Guid>();
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
                // Track this item to apply dash (null rating) if fallback also doesn't find a rating
                itemsToApplyDash.Add(item.Id);
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
                // Remove from dash set since we found a rating
                itemsToApplyDash.Remove(fallbackItem.Item.Id);
            }

            _logger.LogInformation(
                "IMDb fallback complete: {Found} found, {NotFound} not found, {BelowMinimum} below minimum votes, {Unchanged} unchanged",
                fallbackFound,
                fallbackNotFound,
                fallbackBelowMinimumVotes,
                fallbackUnchanged);
        }

        // Apply dash (null rating) to items where no IMDb rating was found
        if (itemsToApplyDash.Count > 0)
        {
            _logger.LogInformation("Applying dash rating to {Count} items with no IMDb rating found", itemsToApplyDash.Count);

            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (itemsToApplyDash.Contains(item.Id))
                {
                    // Check if the rating is already null/empty to avoid unnecessary updates
                    if (item.CommunityRating.HasValue)
                    {
                        pendingUpdates.Add((item, item.GetParent(), item.CommunityRating, null));
                        noRatingDashApplied++;
                    }
                }
            }

            if (noRatingDashApplied > 0)
            {
                _logger.LogInformation("Queued {Count} items for dash rating update", noRatingDashApplied);
            }
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
            "IMDb ratings refresh complete: {Updated} updated (including {DashApplied} with no rating), {Skipped} skipped ({Unchanged} unchanged, {BelowMinimum} below minimum votes, {MissingImdbId} missing IMDb ID), {NotFound} not found in IMDb ratings",
            pendingUpdates.Count,
            noRatingDashApplied,
            skippedTotal,
            skippedUnchanged,
            skippedBelowMinimumVotes,
            skippedMissingImdbId,
            notFound);
    }

    /// <summary>
    /// Finds anime items (those with AniDB provider IDs set by Shoko) that currently lack an
    /// IMDb ID in Jellyfin, then resolves their IMDb IDs via Shoko Server → TMDB → IMDb.
    /// <para>
    /// Resolution results are cached in a persistent JSON file so that subsequent scans skip
    /// the Shoko / TMDB round-trips for already-resolved entries.
    /// </para>
    /// <para>
    /// When <see cref="PluginConfiguration.ForceRefreshAnimeImdbIds"/> is <c>true</c> the cache
    /// is cleared, any previously saved IMDb IDs are stripped from Jellyfin metadata, and
    /// all anime items are re-resolved.  The flag is reset to <c>false</c> automatically.
    /// </para>
    /// <para>
    /// Key content-type disambiguation handled here:
    /// <list type="bullet">
    ///   <item><description>
    ///     An <see cref="Episode"/> item whose AniDB episode entry maps to a TMDB <em>movie</em>
    ///     (i.e. <c>MovieIds</c> is non-empty in the Shoko response) is treated as a movie and
    ///     resolved via the TMDB movie external-IDs endpoint, not the per-episode endpoint.
    ///     This handles Shoko's layout where standalone anime films appear as episodes inside a
    ///     season/series container.
    ///   </description></item>
    ///   <item><description>
    ///     <see cref="Season"/> items are queried and resolved the same way as <see cref="Series"/>
    ///     items — trying TMDB show IDs first and falling back to TMDB movie IDs — because Shoko
    ///     maps individual AniDB series (including films) to Jellyfin seasons.
    ///   </description></item>
    /// </list>
    /// </para>
    /// </summary>
    private async Task<List<(BaseItem Item, BaseItem? Parent, string ImdbId)>> ResolveShokoItemsAsync(
        PluginConfiguration config,
        IReadOnlyList<BaseItem> alreadyHaveImdbItems,
        CancellationToken cancellationToken)
    {
        var result = new List<(BaseItem Item, BaseItem? Parent, string ImdbId)>();

        // Load the persistent anime ID cache.
        var cache = new AnimeIdCache(_dataPath, _logger);
        await cache.LoadAsync(cancellationToken).ConfigureAwait(false);

        // ── Force-refresh: clear cache and strip any previously saved IMDb IDs ────────────────
        if (config.ForceRefreshAnimeImdbIds)
        {
            _logger.LogInformation(
                "Shoko resolution: ForceRefreshAnimeImdbIds is enabled — clearing anime ID cache and stripping saved IMDb IDs from anime items");

            cache.Clear();

            // Strip IMDb IDs from anime items (those with AniDB IDs) that already have one
            // saved in Jellyfin metadata so they are re-resolved fresh.
            var strippingQuery = new InternalItemsQuery
            {
                HasImdbId = true,
                IsVirtualItem = false,
                Recursive = true,
                IncludeItemTypes = new[]
                {
                    BaseItemKind.Series, BaseItemKind.Season, BaseItemKind.Episode, BaseItemKind.Movie
                }
            };

            var itemsWithImdb = _libraryManager.GetItemList(strippingQuery)
                .Where(item => !string.IsNullOrWhiteSpace(item.GetProviderId("AniDB")))
                .ToList();

            int strippedCount = 0;
            foreach (var stripItem in itemsWithImdb)
            {
                cancellationToken.ThrowIfCancellationRequested();
                stripItem.ProviderIds.Remove("Imdb");
                var stripParent = stripItem.GetParent();
                try
                {
                    await _libraryManager
                        .UpdateItemAsync(stripItem, stripParent!, ItemUpdateType.MetadataEdit, cancellationToken)
                        .ConfigureAwait(false);
                    strippedCount++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Shoko resolution: failed to strip IMDb ID from {Name}", stripItem.Name);
                }
            }

            _logger.LogInformation(
                "Shoko resolution: stripped IMDb IDs from {Count} anime items", strippedCount);

            // Reset the flag and persist the configuration so the next scan starts clean.
            config.ForceRefreshAnimeImdbIds = false;
            Plugin.Instance?.SaveConfiguration();
        }

        // ── Collect candidate items (those with AniDB IDs but no IMDb ID) ────────────────────
        // Include Season so that Shoko-managed seasons (individual anime series/films grouped
        // into a top-level series) are resolved in addition to Series, Episode, and Movie items.
        var query = new InternalItemsQuery
        {
            HasImdbId = false,
            IsVirtualItem = false,
            Recursive = true,
            IncludeItemTypes = new[]
            {
                BaseItemKind.Series, BaseItemKind.Season, BaseItemKind.Episode, BaseItemKind.Movie
            }
        };

        var candidates = _libraryManager.GetItemList(query);

        // Keep only items that have an AniDB provider ID set by Shoko.
        var anidbItems = candidates
            .Where(item => !string.IsNullOrWhiteSpace(item.GetProviderId("AniDB")))
            .ToList();

        if (anidbItems.Count == 0)
        {
            _logger.LogInformation("Shoko resolution: no items found with AniDB IDs");
            await cache.SaveAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }

        _logger.LogInformation(
            "Shoko resolution: resolving IMDb IDs for {Count} anime items with AniDB IDs",
            anidbItems.Count);

        // Track parent Series/Season items discovered during episode resolution so they can
        // inherit the show-level IMDb ID.  Key = item.Id.
        var parentContainersFromEpisodes =
            new Dictionary<Guid, (BaseItem Container, BaseItem? ContainerParent, int TmdbShowId)>();

        int cacheHits = 0;

        for (int i = 0; i < anidbItems.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var item = anidbItems[i];
            var anidbId = item.GetProviderId("AniDB")!;
            var parent = item.GetParent();
            bool isEpisodeItem = item is Episode;
            var cacheKey = isEpisodeItem ? AnimeIdCache.EpisodeKey(anidbId) : AnimeIdCache.SeriesKey(anidbId);

            // ── Cache check ──────────────────────────────────────────────────────────────────
            if (cache.TryGetEntry(cacheKey, out var cachedEntry) && cachedEntry is not null)
            {
                cacheHits++;

                if (!string.IsNullOrWhiteSpace(cachedEntry.ImdbId))
                {
                    _logger.LogDebug(
                        "Shoko resolution: cache hit — {ImdbId} for AniDB {AniDbId} ({Name}, {ContentType})",
                        cachedEntry.ImdbId, anidbId, item.Name, cachedEntry.ContentType);

                    if (config.SaveResolvedIdsToMetadata)
                    {
                        await PersistImdbIdAsync(item, parent, cachedEntry.ImdbId, cancellationToken).ConfigureAwait(false);
                    }

                    result.Add((item, parent, cachedEntry.ImdbId));

                    // Register non-movie episodes' parent container even on a cache hit.
                    if (isEpisodeItem && cachedEntry.ContentType != "movie" && parent is not null
                        && string.IsNullOrWhiteSpace(parent.GetProviderId(MetadataProvider.Imdb)))
                    {
                        var cachedShowId = cachedEntry.TmdbShowIds.Length > 0 ? cachedEntry.TmdbShowIds[0] : 0;
                        if (cachedShowId != 0)
                        {
                            parentContainersFromEpisodes.TryAdd(
                                parent.Id, (parent, parent.GetParent(), cachedShowId));
                        }
                    }
                }
                else
                {
                    _logger.LogDebug(
                        "Shoko resolution: cache hit (no IMDb ID resolved) for AniDB {AniDbId} ({Name})",
                        anidbId, item.Name);
                }

                continue;
            }

            // Apply a delay before each API call (after the first) to respect TMDB rate limits.
            if (i > 0 && config.TmdbRequestDelayMs > 0)
            {
                await Task.Delay(config.TmdbRequestDelayMs, cancellationToken).ConfigureAwait(false);
            }

            // ── Step A: Shoko → TMDB ─────────────────────────────────────────────────────────
            ShokoTmdbIds? tmdbIds;
            if (isEpisodeItem)
            {
                tmdbIds = await _shokoClient
                    .GetEpisodeTmdbIdsAsync(config.ShokoServerUrl, config.ShokoApiKey, anidbId, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                // Series, Season, and Movie items all map to a Shoko Series entry.
                tmdbIds = await _shokoClient
                    .GetSeriesTmdbIdsAsync(config.ShokoServerUrl, config.ShokoApiKey, anidbId, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (tmdbIds is null)
            {
                // Record a null cache entry so we don't re-query Shoko on the next scan.
                cache.SetEntry(new AnimeIdCacheEntry
                {
                    Key = cacheKey,
                    AniDbId = anidbId,
                    ImdbId = null,
                    ContentType = null,
                    TmdbShowIds = Array.Empty<int>(),
                    TmdbMovieIds = Array.Empty<int>(),
                    ResolvedAt = DateTimeOffset.UtcNow
                });
                continue;
            }

            // ── Step B: TMDB → IMDb ──────────────────────────────────────────────────────────
            string? imdbId = null;
            string contentType;

            if (item is Movie)
            {
                // Jellyfin Movie items — try movie IDs first, show IDs as fallback.
                contentType = "movie";
                foreach (var movieId in tmdbIds.MovieIds)
                {
                    imdbId = await _tmdbExternalIdsClient
                        .GetImdbIdForMovieAsync(movieId, config.TmdbApiKey, cancellationToken)
                        .ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(imdbId)) break;
                }

                if (string.IsNullOrWhiteSpace(imdbId))
                {
                    contentType = "series";
                    foreach (var showId in tmdbIds.ShowIds)
                    {
                        imdbId = await _tmdbExternalIdsClient
                            .GetImdbIdForShowAsync(showId, config.TmdbApiKey, cancellationToken)
                            .ConfigureAwait(false);
                        if (!string.IsNullOrWhiteSpace(imdbId)) break;
                    }
                }
            }
            else if (isEpisodeItem)
            {
                // Check whether this AniDB episode is actually a movie in Shoko's mapping.
                // In Shoko's layout, standalone films grouped under a parent series appear as
                // Episode items in Jellyfin.  When the Shoko response contains TMDB movie IDs,
                // the entry is a movie — resolve it via the movie endpoint, not episode cross-refs.
                if (tmdbIds.MovieIds.Length > 0)
                {
                    contentType = "movie";
                    foreach (var movieId in tmdbIds.MovieIds)
                    {
                        imdbId = await _tmdbExternalIdsClient
                            .GetImdbIdForMovieAsync(movieId, config.TmdbApiKey, cancellationToken)
                            .ConfigureAwait(false);
                        if (!string.IsNullOrWhiteSpace(imdbId)) break;
                    }
                }
                else
                {
                    // Regular episode — use per-episode TMDB cross-references to get an
                    // episode-specific IMDb ID rather than the show-level one.
                    contentType = "episode";
                    foreach (var crossRef in tmdbIds.EpisodeCrossRefs)
                    {
                        imdbId = await _tmdbExternalIdsClient
                            .GetImdbIdForEpisodeAsync(
                                crossRef.ShowId,
                                crossRef.SeasonNumber,
                                crossRef.EpisodeNumber,
                                config.TmdbApiKey,
                                cancellationToken)
                            .ConfigureAwait(false);
                        if (!string.IsNullOrWhiteSpace(imdbId)) break;
                    }

                    // Register the parent container so it can inherit a show-level IMDb ID later.
                    if (parent is not null && string.IsNullOrWhiteSpace(parent.GetProviderId(MetadataProvider.Imdb)))
                    {
                        var tmdbShowId = tmdbIds.EpisodeCrossRefs.Length > 0
                            ? tmdbIds.EpisodeCrossRefs[0].ShowId
                            : (tmdbIds.ShowIds.Length > 0 ? tmdbIds.ShowIds[0] : 0);

                        if (tmdbShowId != 0)
                        {
                            parentContainersFromEpisodes.TryAdd(
                                parent.Id, (parent, parent.GetParent(), tmdbShowId));
                        }
                    }
                }
            }
            else
            {
                // Series or Season item.  Try show IDs first; fall back to movie IDs for seasons
                // that represent standalone films in Shoko's layout.
                if (tmdbIds.ShowIds.Length > 0)
                {
                    contentType = "series";
                    foreach (var showId in tmdbIds.ShowIds)
                    {
                        imdbId = await _tmdbExternalIdsClient
                            .GetImdbIdForShowAsync(showId, config.TmdbApiKey, cancellationToken)
                            .ConfigureAwait(false);
                        if (!string.IsNullOrWhiteSpace(imdbId)) break;
                    }
                }
                else
                {
                    contentType = "movie";
                }

                if (string.IsNullOrWhiteSpace(imdbId))
                {
                    foreach (var movieId in tmdbIds.MovieIds)
                    {
                        imdbId = await _tmdbExternalIdsClient
                            .GetImdbIdForMovieAsync(movieId, config.TmdbApiKey, cancellationToken)
                            .ConfigureAwait(false);
                        if (!string.IsNullOrWhiteSpace(imdbId)) break;
                    }
                }
            }

            // ── Store result in cache (even if imdbId is null) ───────────────────────────────
            cache.SetEntry(new AnimeIdCacheEntry
            {
                Key = cacheKey,
                AniDbId = anidbId,
                ImdbId = string.IsNullOrWhiteSpace(imdbId) ? null : imdbId,
                ContentType = contentType,
                TmdbShowIds = tmdbIds.ShowIds,
                TmdbMovieIds = tmdbIds.MovieIds,
                ResolvedAt = DateTimeOffset.UtcNow
            });

            if (string.IsNullOrWhiteSpace(imdbId))
            {
                _logger.LogDebug(
                    "Shoko resolution: could not resolve IMDb ID for AniDB {AniDbId} ({Name})",
                    anidbId, item.Name);
                continue;
            }

            _logger.LogDebug(
                "Shoko resolution: resolved {ImdbId} ({ContentType}) for AniDB {AniDbId} ({Name})",
                imdbId, contentType, anidbId, item.Name);

            // ── Step C: Optionally persist the resolved IMDb ID to the item's metadata ────────
            if (config.SaveResolvedIdsToMetadata)
            {
                await PersistImdbIdAsync(item, parent, imdbId, cancellationToken).ConfigureAwait(false);
            }

            result.Add((item, parent, imdbId));
        }

        // ── Step D: Resolve show-level IMDb IDs for parent containers discovered during
        //    episode processing.  These containers lack their own AniDB IDs (or were not in the
        //    anidbItems list), so they would otherwise never get a rating.
        var alreadyResolvedIds = new HashSet<Guid>(result.Select(r => r.Item.Id));
        int containerInheritedCount = 0;

        foreach (var (containerId, (containerItem, containerParent, tmdbShowId)) in parentContainersFromEpisodes)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (alreadyResolvedIds.Contains(containerId))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(containerItem.GetProviderId(MetadataProvider.Imdb)))
            {
                continue;
            }

            // Check cache for the container's inherited entry (keyed by Jellyfin ID since there
            // may be no AniDB ID on this container).
            var containerCacheKey = $"C:{containerId}";
            if (cache.TryGetEntry(containerCacheKey, out var containerCached) && containerCached is not null)
            {
                if (!string.IsNullOrWhiteSpace(containerCached.ImdbId))
                {
                    if (config.SaveResolvedIdsToMetadata)
                    {
                        await PersistImdbIdAsync(containerItem, containerParent, containerCached.ImdbId, cancellationToken).ConfigureAwait(false);
                    }

                    result.Add((containerItem, containerParent, containerCached.ImdbId));
                    containerInheritedCount++;
                }

                continue;
            }

            if (config.TmdbRequestDelayMs > 0)
            {
                await Task.Delay(config.TmdbRequestDelayMs, cancellationToken).ConfigureAwait(false);
            }

            var showImdbId = await _tmdbExternalIdsClient
                .GetImdbIdForShowAsync(tmdbShowId, config.TmdbApiKey, cancellationToken)
                .ConfigureAwait(false);

            cache.SetEntry(new AnimeIdCacheEntry
            {
                Key = containerCacheKey,
                AniDbId = string.Empty,
                ImdbId = string.IsNullOrWhiteSpace(showImdbId) ? null : showImdbId,
                ContentType = "series",
                TmdbShowIds = new[] { tmdbShowId },
                TmdbMovieIds = Array.Empty<int>(),
                ResolvedAt = DateTimeOffset.UtcNow
            });

            if (string.IsNullOrWhiteSpace(showImdbId))
            {
                _logger.LogDebug(
                    "Shoko resolution: could not resolve show IMDb ID for parent container \"{Name}\" via TMDB show {TmdbShowId}",
                    containerItem.Name, tmdbShowId);
                continue;
            }

            _logger.LogDebug(
                "Shoko resolution: inherited show IMDb ID {ImdbId} for parent container \"{Name}\"",
                showImdbId, containerItem.Name);

            if (config.SaveResolvedIdsToMetadata)
            {
                await PersistImdbIdAsync(containerItem, containerParent, showImdbId, cancellationToken).ConfigureAwait(false);
            }

            result.Add((containerItem, containerParent, showImdbId));
            containerInheritedCount++;
        }

        _logger.LogInformation(
            "Shoko resolution complete: {Resolved} of {Total} AniDB items resolved to an IMDb ID " +
            "({CacheHits} from cache), {ContainerInherited} containers inherited show-level rating",
            result.Count - containerInheritedCount,
            anidbItems.Count,
            cacheHits,
            containerInheritedCount);

        await cache.SaveAsync(cancellationToken).ConfigureAwait(false);

        return result;
    }

    /// <summary>
    /// Persists a resolved IMDb ID to the item's provider metadata in Jellyfin.
    /// If the save fails, the in-memory change is reverted.
    /// </summary>
    private async Task PersistImdbIdAsync(
        BaseItem item,
        BaseItem? parent,
        string imdbId,
        CancellationToken cancellationToken)
    {
        item.SetProviderId(MetadataProvider.Imdb, imdbId);
        try
        {
            await _libraryManager
                .UpdateItemAsync(item, parent!, ItemUpdateType.MetadataEdit, cancellationToken)
                .ConfigureAwait(false);

            _logger.LogDebug(
                "Shoko resolution: saved IMDb ID {ImdbId} to metadata for {Name}", imdbId, item.Name);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "Shoko resolution: failed to save IMDb ID {ImdbId} to metadata for {Name}", imdbId, item.Name);
            item.ProviderIds.Remove("Imdb");
        }
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
            includeTypes.Add(BaseItemKind.Season);
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
