using System;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.ImdbRatings.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    private int _minimumVotes = 1;
    private int _imdbFallbackRequestDelayMs = 250;
    private int _flatFileCacheHours = 12;
    private int _tmdbRequestDelayMs = 250;

    public int MinimumVotes
    {
        get => _minimumVotes;
        set => _minimumVotes = Math.Clamp(value, 1, 1_000_000);
    }

    public bool IncludeMovies { get; set; } = true;

    public bool IncludeSeries { get; set; } = true;

    public bool IncludeOtherLibraries { get; set; } = false;

    public bool EnableItemDebugLogging { get; set; } = false;

    public bool EnableImdbFallback { get; set; } = false;

    public int ImdbFallbackRequestDelayMs
    {
        get => _imdbFallbackRequestDelayMs;
        set => _imdbFallbackRequestDelayMs = Math.Clamp(value, 0, 5_000);
    }

    public int FlatFileCacheHours
    {
        get => _flatFileCacheHours;
        set => _flatFileCacheHours = Math.Clamp(value, 1, 24);
    }

    // Shoko / TMDB integration

    public bool EnableShokoResolution { get; set; } = false;

    public string ShokoServerUrl { get; set; } = "http://localhost:8111";

    public string ShokoApiKey { get; set; } = string.Empty;

    public string TmdbApiKey { get; set; } = string.Empty;

    /// <summary>
    /// When true, resolved IMDb IDs are written back to the item's provider IDs in the database
    /// so future scans pick them up automatically. Disable during testing to avoid persisting
    /// incorrectly resolved IDs.
    /// </summary>
    public bool SaveResolvedIdsToMetadata { get; set; } = false;

    public int TmdbRequestDelayMs
    {
        get => _tmdbRequestDelayMs;
        set => _tmdbRequestDelayMs = Math.Clamp(value, 0, 5_000);
    }
}
