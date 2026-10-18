using ConnectIt.Wpf.Services;

namespace ConnectIt.Tests;

public class SettingsValidationTests
{
    [Theory]
    [InlineData("5", 5)]
    [InlineData("30", 30)]
    [InlineData(" 300 ", 300)]
    public void ValidateSearchDuration_AcceptsValuesInRange(string text, int expected)
    {
        var result = SettingsValidation.ValidateSearchDuration(text);

        Assert.True(result.IsValid);
        Assert.Equal(expected, result.Value);
        Assert.Null(result.Error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("4")]
    [InlineData("301")]
    [InlineData("abc")]
    public void ValidateSearchDuration_RejectsOutOfRangeOrInvalid(string text)
    {
        var result = SettingsValidation.ValidateSearchDuration(text);

        Assert.False(result.IsValid);
        Assert.False(string.IsNullOrEmpty(result.Error));
    }

    [Fact]
    public void ValidatePort_EmptyMeansAutoAssign()
    {
        var result = SettingsValidation.ValidatePort("  ");

        Assert.True(result.IsValid);
        Assert.Equal(0, result.Value);
    }

    [Theory]
    [InlineData("1024", true)]
    [InlineData("65535", true)]
    [InlineData("80", false)]
    [InlineData("70000", false)]
    public void ValidatePort_RequiresNonPrivilegedPort(string text, bool expectedValid)
    {
        Assert.Equal(expectedValid, SettingsValidation.ValidatePort(text).IsValid);
    }

    [Theory]
    [InlineData("3", true)]
    [InlineData("120", true)]
    [InlineData("2", false)]
    [InlineData("121", false)]
    public void ValidateConnectTimeout_UsesServiceRange(string text, bool expectedValid)
    {
        Assert.Equal(expectedValid, SettingsValidation.ValidateConnectTimeout(text).IsValid);
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("30", true)]
    [InlineData("0", false)]
    [InlineData("31", false)]
    public void ValidateAutoplayCountdown_UsesServiceRange(string text, bool expectedValid)
    {
        Assert.Equal(expectedValid, SettingsValidation.ValidateAutoplayCountdown(text).IsValid);
    }
}
