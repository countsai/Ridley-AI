using Ridley.Core;

namespace Ridley.Core.Tests;

public class ResultAndSettingsTests
{
    [Fact]
    public void Success_carries_value_and_no_error()
    {
        var result = Result<int>.Success(7);
        Assert.True(result.Ok);
        Assert.Equal(7, result.Value);
        Assert.Equal(ErrorKind.None, result.Kind);
    }

    [Fact]
    public void Failure_requires_a_real_error_kind()
    {
        Assert.Throws<ArgumentException>(() => Result<int>.Fail(ErrorKind.None, "x"));
        var failed = Result<int>.Fail(ErrorKind.Unsupported, "nope");
        Assert.False(failed.Ok);
        Assert.Equal("nope", failed.Error);
    }

    [Fact]
    public void Defaults_are_valid_and_privacy_first()
    {
        var defaults = new UserSettings();
        Assert.Empty(defaults.Validate());
        Assert.False(defaults.AllowScreenCapture);
        Assert.Null(defaults.ModelProvider);
    }

    [Theory]
    [InlineData("", 2, "Information")]
    [InlineData("Ctrl+Alt+Space", -1, "Information")]
    [InlineData("Ctrl+Alt+Space", 6, "Information")]
    [InlineData("Ctrl+Alt+Space", 2, "Chatty")]
    public void Invalid_settings_are_reported(string hotkey, int retries, string level)
    {
        var settings = new UserSettings { PushToTalkHotkey = hotkey, MaxRetriesPerStep = retries, LogLevel = level };
        Assert.Single(settings.Validate());
    }

    [Fact]
    public void There_is_no_setting_that_disables_confirmation_of_consequential_actions()
    {
        var names = typeof(UserSettings).GetProperties().Select(p => p.Name);
        Assert.DoesNotContain(names, n => n.Contains("Confirm", StringComparison.OrdinalIgnoreCase));
    }
}
