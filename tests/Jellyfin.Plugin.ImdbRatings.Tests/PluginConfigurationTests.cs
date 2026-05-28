using Jellyfin.Plugin.ImdbRatings.Configuration;

namespace Jellyfin.Plugin.ImdbRatings.Tests;

public class PluginConfigurationTests
{
    [Fact]
    public void NewSettings_AreClampedAndDefaulted()
    {
        var config = new PluginConfiguration();

        Assert.False(config.EnableImdbFallback);
        Assert.Equal(250, config.ImdbFallbackRequestDelayMs);
        Assert.Equal(12, config.FlatFileCacheHours);

        config.ImdbFallbackRequestDelayMs = -1;
        Assert.Equal(0, config.ImdbFallbackRequestDelayMs);

        config.ImdbFallbackRequestDelayMs = 9000;
        Assert.Equal(5000, config.ImdbFallbackRequestDelayMs);

        config.FlatFileCacheHours = 0;
        Assert.Equal(1, config.FlatFileCacheHours);

        config.FlatFileCacheHours = 48;
        Assert.Equal(24, config.FlatFileCacheHours);
    }
}
