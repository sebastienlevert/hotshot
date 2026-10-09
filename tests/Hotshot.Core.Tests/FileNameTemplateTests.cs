using Hotshot.Core;
using Hotshot.Core.Naming;

namespace Hotshot.Core.Tests;

public sealed class FileNameTemplateTests
{
    private static readonly DateTime Stamp = new(2026, 10, 9, 17, 51, 58, 448);

    private static NamingContext Ctx(CaptureKind kind = CaptureKind.Region, long counter = 7) => new()
    {
        Timestamp = Stamp,
        Kind = kind,
        AppName = "msedge",
        WindowTitle = "Inbox: Outlook / Mail",
        MonitorIndex = 2,
        Width = 1280,
        Height = 720,
        Counter = counter,
        ComputerName = "BOX",
        UserName = "seb",
    };

    [Fact]
    public void DefaultPattern_ProducesYearMonthTimestamp()
    {
        var rel = FileNameTemplate.BuildRelativePath(FileNameTemplate.DefaultPattern, Ctx());
        Assert.Equal(Path.Combine("2026", "10", "2026-10-09_17-51-58.png"), rel);
    }

    [Theory]
    [InlineData("{yyyy}-{MM}-{dd} {HH}.{mm}.{ss}.{fff}", "2026-10-09 17.51.58.448")]
    [InlineData("{yy}{MMM}{MMMM}{ddd}", "26OctOctoberFri")]
    [InlineData("{hh}{tt}", "05PM")]
    [InlineData("{date}_{time}", "2026-10-09_17-51-58")]
    [InlineData("{now:yyyyMMdd}", "20261009")]
    [InlineData("{type}-{app}-{monitor}-{width}x{height}", "region-msedge-2-1280x720")]
    [InlineData("{counter}-{counter:4}", "7-0007")]
    [InlineData("{computer}-{user}", "BOX-seb")]
    public void Expand_Tokens(string pattern, string expected)
    {
        Assert.Equal(expected, FileNameTemplate.Expand(pattern, Ctx()));
    }

    [Fact]
    public void Expand_KindTokens()
    {
        Assert.Equal("screen", FileNameTemplate.Expand("{type}", Ctx(CaptureKind.AllMonitors)));
        Assert.Equal("recording", FileNameTemplate.Expand("{type}", Ctx(CaptureKind.Recording)));
    }

    [Fact]
    public void Expand_UnknownTokensAreKeptLiterally()
    {
        Assert.Equal("{nope}-x", FileNameTemplate.Expand("{nope}-x", Ctx()));
    }

    [Fact]
    public void Rand_HasRequestedLength()
    {
        Assert.Equal(10, FileNameTemplate.Expand("{rand:10}", Ctx()).Length);
        Assert.Equal(6, FileNameTemplate.Expand("{rand}", Ctx()).Length);
        Assert.Equal(32, FileNameTemplate.Expand("{guid}", Ctx()).Length);
    }

    [Fact]
    public void FindInvalidTokens_ReportsProblems()
    {
        Assert.Empty(FileNameTemplate.FindInvalidTokens("{yyyy}/{MM}/{timestamp}_{rand:4}_{counter:3}"));
        Assert.Equal(["{bogus}", "{counter:x}"], FileNameTemplate.FindInvalidTokens("{bogus}/{counter:x}"));
        Assert.Equal(["{"], FileNameTemplate.FindInvalidTokens("abc{def"));
    }

    [Fact]
    public void Title_IsSanitized_AndSlashesDoNotCreateFolders()
    {
        var rel = FileNameTemplate.BuildRelativePath("{title}", Ctx());
        Assert.Equal("Inbox- Outlook - Mail.png", rel);
    }

    [Fact]
    public void Extension_IsAppendedOnce()
    {
        Assert.Equal("shot.png", FileNameTemplate.BuildRelativePath("shot.png", Ctx()));
        Assert.Equal("shot.mp4", FileNameTemplate.BuildRelativePath("shot.png", Ctx(CaptureKind.Recording)));
        Assert.Equal("shot.gif", FileNameTemplate.BuildRelativePath("shot", Ctx(CaptureKind.Gif)));
    }

    [Theory]
    [InlineData("../../evil", "evil.png")]
    [InlineData("a/../b", "a\\b.png")]
    [InlineData("C:/Windows/x", "C-\\Windows\\x.png")]
    [InlineData("", "2026\\10\\2026-10-09_17-51-58.png")]
    [InlineData("///", "capture.png")]
    [InlineData("con", "_con.png")]
    [InlineData("name. . .", "name.png")]
    public void BuildRelativePath_IsSafe(string pattern, string expected)
    {
        Assert.Equal(expected, FileNameTemplate.BuildRelativePath(pattern, Ctx()));
    }

    [Fact]
    public void BuildPath_StaysUnderRoot_AndAvoidsCollisions()
    {
        var root = Path.Combine(Path.GetTempPath(), "hotshot-tests-root");
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.Combine(root, "shot.png"),
            Path.Combine(root, "shot_1.png"),
        };

        var path = FileNameTemplate.BuildPath(root, "shot", Ctx(), exists: taken.Contains);
        Assert.Equal(Path.Combine(root, "shot_2.png"), path);
    }

    [Fact]
    public void LongSegments_AreTruncated()
    {
        var rel = FileNameTemplate.BuildRelativePath(new string('a', 400), Ctx());
        Assert.Equal(120 + ".png".Length, rel.Length);
    }

    [Fact]
    public void UsesCounter_Detects()
    {
        Assert.True(FileNameTemplate.UsesCounter("x{counter:3}"));
        Assert.False(FileNameTemplate.UsesCounter("{timestamp}"));
    }

    [Fact]
    public void TokenCatalog_ContainsValidInsertableTokens()
    {
        Assert.All(FileNameTemplate.Tokens, token =>
        {
            Assert.Empty(FileNameTemplate.FindInvalidTokens(token.Token));
            Assert.False(string.IsNullOrWhiteSpace(token.Example));
        });
        Assert.Equal(FileNameTemplate.Tokens.Count, FileNameTemplate.Tokens.Select(token => token.Token).Distinct().Count());
    }

    [Theory]
    [InlineData("{now:yyyyMMdd}")]
    [InlineData("{counter}")]
    [InlineData("{counter:4}")]
    public void TokenCatalog_ExamplesMatchExpansion(string token)
    {
        var item = Assert.Single(FileNameTemplate.Tokens, item => item.Token == token);
        Assert.Equal(item.Example, FileNameTemplate.Expand(item.Token, Ctx(counter: 42)));
    }
}
