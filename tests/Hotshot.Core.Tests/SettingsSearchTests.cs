using Hotshot.Core.Settings;

namespace Hotshot.Core.Tests;

public sealed class SettingsSearchTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("microphone unknown")]
    public void EmptyOrUnmatchedSearchHasNoResults(string query) =>
        Assert.Equal(0, SettingsSearch.Score(query, "Record microphone", "Screen recording", "audio voice"));

    [Theory]
    [InlineData("microphone", 60)]
    [InlineData("RECORD MICROPHONE", 100)]
    [InlineData("record", 80)]
    [InlineData("voice", 40)]
    [InlineData("audio microphone", 40)]
    [InlineData("  audio\tmicrophone  ", 40)]
    public void SearchMatchesSettingTitlesAndAllKeywordTerms(string query, int score) =>
        Assert.Equal(score, SettingsSearch.Score(query, "Record microphone", "Screen recording", "audio voice"));

    [Fact]
    public void SpecificTitleRanksAheadOfCategoryKeyword()
    {
        Assert.True(SettingsSearch.Score("App theme", "App theme", "General", "appearance") >
            SettingsSearch.Score("App theme", "Start with Windows", "General", "app theme startup"));
    }
}
