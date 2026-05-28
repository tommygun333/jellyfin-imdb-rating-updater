using System;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.ImdbRatings.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    private int _minimumVotes = 1;
    private int _omdbRequestDelayMs = 250;
    private int _flatFileCacheHours = 12;

    public int MinimumVotes
    {
        get => _minimumVotes;
        set => _minimumVotes = Math.Clamp(value, 1, 1_000_000);
    }

    public bool IncludeMovies { get; set; } = true;

    public bool IncludeSeries { get; set; } = true;

    public bool EnableItemDebugLogging { get; set; } = false;

    public string OmdbApiKey { get; set; } = string.Empty;

    public bool EnableOmdbFallback { get; set; } = false;

    public int OmdbRequestDelayMs
    {
        get => _omdbRequestDelayMs;
        set => _omdbRequestDelayMs = Math.Clamp(value, 0, 5_000);
    }

    public int FlatFileCacheHours
    {
        get => _flatFileCacheHours;
        set => _flatFileCacheHours = Math.Clamp(value, 1, 24);
    }
}
