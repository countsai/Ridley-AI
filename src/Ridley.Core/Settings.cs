namespace Ridley.Core;

/// <summary>
/// User-controlled settings. Note what is NOT here: there is no switch to disable confirmation of consequential
/// actions. That policy is fixed in code.
/// </summary>
public sealed record UserSettings
{
    public const int MaxRetryLimit = 5;

    public string PushToTalkHotkey { get; init; } = "Ctrl+Alt+Space";
    public bool AlwaysOnTop { get; init; } = true;
    public bool MinimiseToTray { get; init; } = true;
    public bool OverlayEnabled { get; init; } = true;

    /// <summary>Screen capture is off until the user turns it on.</summary>
    public bool AllowScreenCapture { get; init; }

    public bool SpeakResponses { get; init; } = true;
    public int MaxRetriesPerStep { get; init; } = 2;
    public string LogLevel { get; init; } = "Information";

    /// <summary>Selected provider names; null means "not configured".</summary>
    public string? ModelProvider { get; init; }
    public string? SpeechToTextProvider { get; init; }
    public string? TextToSpeechProvider { get; init; }

    /// <summary>Returns the list of problems; empty means valid.</summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(PushToTalkHotkey))
        {
            problems.Add("Push-to-talk hotkey cannot be empty.");
        }

        if (MaxRetriesPerStep is < 0 or > MaxRetryLimit)
        {
            problems.Add($"Retries per step must be between 0 and {MaxRetryLimit}.");
        }

        if (!new[] { "Debug", "Information", "Warning", "Error" }.Contains(LogLevel, StringComparer.OrdinalIgnoreCase))
        {
            problems.Add("Log level must be Debug, Information, Warning or Error.");
        }

        return problems;
    }
}
