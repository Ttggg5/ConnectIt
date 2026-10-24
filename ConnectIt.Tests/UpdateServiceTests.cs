using ConnectIt.Wpf.Services;

namespace ConnectIt.Tests;

public class UpdateServiceTests
{
    [Theory]
    [InlineData("v1.0.42", "1.0.42.0")]
    [InlineData("1.0.7", "1.0.7.0")]
    [InlineData(" V2.1 ", "2.1.0.0")]
    public void TryParseTagVersion_ParsesReleaseTags(string tag, string expected)
    {
        Assert.True(UpdateService.TryParseTagVersion(tag, out var version));
        Assert.Equal(Version.Parse(expected), version);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("latest")]
    [InlineData("v1.0.x")]
    public void TryParseTagVersion_RejectsInvalidTags(string? tag)
    {
        Assert.False(UpdateService.TryParseTagVersion(tag, out _));
    }

    [Fact]
    public void ParsedTagVersion_ComparesAgainstCurrentVersion()
    {
        // 本機建置版本是 1.0.0;CI 的版本 1.0.{建置編號} 一定比它新,比較時欄位要補齊才不會出錯。
        UpdateService.TryParseTagVersion("v1.0.1", out var newer);
        UpdateService.TryParseTagVersion("v1.0.0", out var same);
        Assert.True(newer > same);
        Assert.Equal(same, new Version(1, 0, 0, 0));
    }
}
