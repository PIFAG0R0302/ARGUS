using Argus.App.Themes;
using Argus.Core.Configuration;
using Xunit;

namespace Argus.App.Tests;

public sealed class ThemeManagerTests
{
    [Fact]
    public void Themes_ContainsDefaultAndEightNewThemes_WithValidResourceDictionaries()
    {
        Assert.Equal(9, ArgusThemeManager.Themes.Count);
        Assert.Equal(3, ArgusThemeManager.Themes.Count(static t => t.IsLight));
        Assert.Equal(6, ArgusThemeManager.Themes.Count(static t => !t.IsLight));
        Assert.Contains(ArgusThemeManager.Themes, static t => t.Id == ArgusSettings.DefaultThemeId);

        foreach (var theme in ArgusThemeManager.Themes)
        {
            var dict = ArgusThemeManager.BuildResourceDictionary(theme);
            Assert.True(dict.ContainsKey("ArgusBgCanvasBrush"));
            Assert.True(dict.ContainsKey("ArgusBgPanelBrush"));
            Assert.True(dict.ContainsKey("ArgusTextPrimaryBrush"));
            Assert.True(dict.ContainsKey("ArgusVioletBrush"));
            Assert.True(dict.ContainsKey("ArgusCyanBrush"));
            Assert.True(dict.ContainsKey("ArgusWorkspaceBackdrop"));
        }
    }

    [Fact]
    public void ApplyTheme_SwitchesCurrentTheme_AndRaisesThemeChanged()
    {
        var raisedIds = new List<string>();
        void Handler(object? _, ArgusThemeDefinition def)
        {
            lock (raisedIds)
            {
                raisedIds.Add(def.Id);
            }
        }

        ArgusThemeManager.ThemeChanged += Handler;
        try
        {
            var applied = ArgusThemeManager.ApplyTheme("arctic-lab");
            Assert.Equal("arctic-lab", applied.Id);
            lock (raisedIds)
            {
                Assert.Contains("arctic-lab", raisedIds);
            }

            // Unknown ID falls back to default
            var fallback = ArgusThemeManager.ApplyTheme("unknown-theme-xyz");
            Assert.Equal(ArgusSettings.DefaultThemeId, fallback.Id);
        }
        finally
        {
            ArgusThemeManager.ThemeChanged -= Handler;
            ArgusThemeManager.ApplyTheme(ArgusSettings.DefaultThemeId);
        }
    }

    [Fact]
    public void ArgusSettings_Normalize_SanitizesThemeId()
    {
        var empty = new ArgusSettings { ThemeId = "   " }.Normalize();
        Assert.Equal(ArgusSettings.DefaultThemeId, empty.ThemeId);

        var upper = new ArgusSettings { ThemeId = "  Nord-Slate " }.Normalize();
        Assert.Equal("nord-slate", upper.ThemeId);
    }
}
