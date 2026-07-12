using WindowLive.Core.Config;
using WindowLive.Core.Geometry;
using Xunit;

namespace WindowLive.Core.Tests;

/// <summary>
/// Coverage for <see cref="AppConfig"/> persistence, focused on the game-mode
/// rects (<see cref="AppConfig.GamePanelRect"/>, <see cref="AppConfig.GameChatRegion"/>)
/// added for panel placement (see docs/window-live-design.md, "Config").
/// </summary>
public class AppConfigTests
{
    private static string TempConfigPath() =>
        Path.Combine(Path.GetTempPath(), $"windowlive-config-test-{Guid.NewGuid():N}.json");

    [Fact]
    public void SaveThenLoadOrDefault_RoundTripsGameRectsExactly()
    {
        string path = TempConfigPath();
        try
        {
            var config = new AppConfig
            {
                GamePanelRect = new PixelRect(10, 20, 300, 150),
                GameChatRegion = new PixelRect(-5, 15, 640, 480),
            };
            config.Save(path);

            var loaded = AppConfig.LoadOrDefault(path);

            Assert.Equal(config.GamePanelRect, loaded.GamePanelRect);
            Assert.Equal(config.GameChatRegion, loaded.GameChatRegion);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void LoadOrDefault_MissingPath_ReturnsDefaultsWithZeroSizedGamePanelRectAndCreatesFile()
    {
        string path = TempConfigPath();
        Assert.False(File.Exists(path));
        try
        {
            var loaded = AppConfig.LoadOrDefault(path);

            Assert.Equal(new PixelRect(0, 0, 0, 0), loaded.GamePanelRect);
            Assert.True(loaded.GamePanelRect.IsEmpty);
            Assert.True(File.Exists(path));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void GamePanelRect_DefaultsToEmpty()
    {
        var config = new AppConfig();

        Assert.True(config.GamePanelRect.IsEmpty);
    }
}
