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

    [Fact]
    public void NewBackendFields_DefaultToPreAbstractionBehavior()
    {
        var config = new AppConfig();

        Assert.Equal("local", config.Provider);
        Assert.Equal("vision", config.OcrEngine);
        Assert.Equal("auto", config.SourceLanguage);
        Assert.Equal("en", config.TargetLanguage);
        Assert.Null(config.LocalPromptTemplate);
        Assert.Null(config.CustomPromptTemplate);
        Assert.Equal("", config.CustomEndpointUrl);
        Assert.Equal("", config.CustomApiKey);
        Assert.Equal("", config.CustomModelName);
        Assert.Equal(60, config.CustomRequestTimeoutSeconds);
    }

    [Fact]
    public void LoadOrDefault_LegacyV1Json_FillsNewFieldsWithDefaults()
    {
        // A config.json written by the pre-abstraction app: none of the new
        // provider/OCR/language fields present. Must load cleanly with the new
        // fields at their behavior-preserving defaults.
        string path = TempConfigPath();
        try
        {
            File.WriteAllText(path, """
                {
                  "desktopHotkey": "Ctrl+Shift+T",
                  "gameModeHotkey": "Ctrl+Shift+G",
                  "pollIntervalMs": 300,
                  "serverPort": 8420,
                  "gameChatRegion": { "x": 0, "y": 0, "width": 0, "height": 0 },
                  "gamePanelRect": { "x": 0, "y": 0, "width": 0, "height": 0 }
                }
                """);

            var loaded = AppConfig.LoadOrDefault(path);

            Assert.Equal("Ctrl+Shift+T", loaded.DesktopHotkey);
            Assert.Equal("local", loaded.Provider);
            Assert.Equal("vision", loaded.OcrEngine);
            Assert.Equal("auto", loaded.SourceLanguage);
            Assert.Equal("en", loaded.TargetLanguage);
            Assert.Null(loaded.LocalPromptTemplate);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void SaveThenLoadOrDefault_RoundTripsBackendFields()
    {
        string path = TempConfigPath();
        try
        {
            var config = new AppConfig
            {
                Provider = "custom",
                CustomEndpointUrl = "http://127.0.0.1:8421",
                CustomApiKey = "sk-test",
                CustomModelName = "gpt-test",
                OcrEngine = "tesseract",
                SourceLanguage = "ja",
                TargetLanguage = "de",
                LocalPromptTemplate = "custom {text}",
            };
            config.Save(path);

            var loaded = AppConfig.LoadOrDefault(path);

            Assert.Equal("custom", loaded.Provider);
            Assert.Equal("http://127.0.0.1:8421", loaded.CustomEndpointUrl);
            Assert.Equal("sk-test", loaded.CustomApiKey);
            Assert.Equal("gpt-test", loaded.CustomModelName);
            Assert.Equal("tesseract", loaded.OcrEngine);
            Assert.Equal("ja", loaded.SourceLanguage);
            Assert.Equal("de", loaded.TargetLanguage);
            Assert.Equal("custom {text}", loaded.LocalPromptTemplate);
            Assert.Null(loaded.CustomPromptTemplate); // unset stays null (= use default)
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
