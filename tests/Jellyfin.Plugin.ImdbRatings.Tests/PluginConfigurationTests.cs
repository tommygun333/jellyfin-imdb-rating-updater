using Jellyfin.Plugin.ImdbRatings.Configuration;

namespace Jellyfin.Plugin.ImdbRatings.Tests;

public class PluginConfigurationTests
{
    [Fact]
    public void NewSettings_AreClampedAndDefaulted()
    {
        var config = new PluginConfiguration();

        Assert.Equal(string.Empty, config.OmdbApiKey);
        Assert.False(config.EnableOmdbFallback);
        Assert.Equal(250, config.OmdbRequestDelayMs);
        Assert.Equal(12, config.FlatFileCacheHours);

        config.OmdbRequestDelayMs = -1;
        Assert.Equal(0, config.OmdbRequestDelayMs);

        config.OmdbRequestDelayMs = 9000;
        Assert.Equal(5000, config.OmdbRequestDelayMs);

        config.FlatFileCacheHours = 0;
        Assert.Equal(1, config.FlatFileCacheHours);

        config.FlatFileCacheHours = 48;
        Assert.Equal(24, config.FlatFileCacheHours);
    }
}
