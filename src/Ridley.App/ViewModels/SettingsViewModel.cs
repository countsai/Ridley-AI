using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Ridley.Core;

namespace Ridley.App.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    public static IReadOnlyList<string> LogLevels { get; } = ["Debug", "Information", "Warning", "Error"];
    public static IReadOnlyList<int> RetryChoices { get; } = [0, 1, 2, 3, 4, 5];

    private readonly ISettingsStore _store;
    private readonly ILogger<SettingsViewModel> _log;

    public SettingsViewModel(ISettingsStore store, ILogger<SettingsViewModel> log)
    {
        _store = store;
        _log = log;
    }

    [ObservableProperty] private string _pushToTalkHotkey = "Ctrl+Alt+Space";
    [ObservableProperty] private bool _alwaysOnTop = true;
    [ObservableProperty] private bool _minimiseToTray = true;
    [ObservableProperty] private bool _overlayEnabled = true;
    [ObservableProperty] private bool _allowScreenCapture;
    [ObservableProperty] private bool _speakResponses = true;
    [ObservableProperty] private int _maxRetriesPerStep = 2;
    [ObservableProperty] private string _logLevel = "Information";
    [ObservableProperty] private string? _validationMessage;
    [ObservableProperty] private string? _savedMessage;

    /// <summary>Loaded settings that are actually in force (what Save last wrote).</summary>
    public UserSettings Current { get; private set; } = new();

    // Providers are not selectable in Phase 1; show the truth instead of a fake dropdown.
    public string ProviderStatus =>
        $"Model: {Current.ModelProvider ?? "not configured"} · Speech-to-text: {Current.SpeechToTextProvider ?? "not configured"} · Text-to-speech: {Current.TextToSpeechProvider ?? "not configured"}";

    public event EventHandler<UserSettings>? Applied;

    public async Task LoadAsync(CancellationToken ct)
    {
        Apply(await _store.LoadAsync(ct));
        Applied?.Invoke(this, Current);
    }

    public UserSettings ToSettings() => Current with
    {
        PushToTalkHotkey = PushToTalkHotkey.Trim(),
        AlwaysOnTop = AlwaysOnTop,
        MinimiseToTray = MinimiseToTray,
        OverlayEnabled = OverlayEnabled,
        AllowScreenCapture = AllowScreenCapture,
        SpeakResponses = SpeakResponses,
        MaxRetriesPerStep = MaxRetriesPerStep,
        LogLevel = LogLevel,
    };

    [RelayCommand]
    private async Task SaveAsync()
    {
        SavedMessage = null;
        var candidate = ToSettings();
        var problems = candidate.Validate();
        if (problems.Count > 0)
        {
            ValidationMessage = string.Join(" ", problems);
            return;
        }

        try
        {
            await _store.SaveAsync(candidate, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Saving settings failed");
            ValidationMessage = "Could not save settings. See the log for details.";
            return;
        }

        ValidationMessage = null;
        Current = candidate;
        SavedMessage = "Saved.";
        _log.LogInformation("Settings saved");
        Applied?.Invoke(this, Current);
    }

    [RelayCommand]
    private void ResetToDefaults()
    {
        Apply(new UserSettings { ModelProvider = Current.ModelProvider, SpeechToTextProvider = Current.SpeechToTextProvider, TextToSpeechProvider = Current.TextToSpeechProvider });
        SavedMessage = "Defaults restored. Press Save to keep them.";
    }

    private void Apply(UserSettings settings)
    {
        Current = settings;
        PushToTalkHotkey = settings.PushToTalkHotkey;
        AlwaysOnTop = settings.AlwaysOnTop;
        MinimiseToTray = settings.MinimiseToTray;
        OverlayEnabled = settings.OverlayEnabled;
        AllowScreenCapture = settings.AllowScreenCapture;
        SpeakResponses = settings.SpeakResponses;
        MaxRetriesPerStep = settings.MaxRetriesPerStep;
        LogLevel = settings.LogLevel;
        ValidationMessage = null;
        OnPropertyChanged(nameof(ProviderStatus));
    }
}
