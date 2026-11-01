using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Ridley.App.Services;
using Ridley.Core;

namespace Ridley.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly ITurnHandler _turns;
    private readonly ILogger<MainViewModel> _log;
    private CancellationTokenSource? _currentTurn;

    public MainViewModel(
        ITurnHandler turns,
        SettingsViewModel settings,
        CaptureIndicatorService capture,
        ILogger<MainViewModel> log)
    {
        _turns = turns;
        _log = log;
        Settings = settings;
        Capture = capture;
        Settings.Applied += (_, s) =>
        {
            AlwaysOnTop = s.AlwaysOnTop;
            MinimiseToTray = s.MinimiseToTray;
        };
        Messages.Add(new ChatMessage(
            ChatMessageKind.System,
            "Ridley is running. Voice, the model and computer control arrive in later phases; typed messages are accepted but not acted on yet.",
            DateTimeOffset.Now));
    }

    public SettingsViewModel Settings { get; }
    public CaptureIndicatorService Capture { get; }
    public ObservableCollection<ChatMessage> Messages { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SubmitCommand))]
    private string _inputText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy), nameof(StatusText))]
    [NotifyCanExecuteChangedFor(nameof(SubmitCommand), nameof(StopCommand))]
    private AssistantState _state = AssistantState.Idle;

    [ObservableProperty] private bool _isSettingsOpen;
    [ObservableProperty] private bool _alwaysOnTop = true;
    [ObservableProperty] private bool _minimiseToTray = true;
    [ObservableProperty] private string? _errorBanner;

    public bool IsBusy => State is AssistantState.Listening or AssistantState.Processing or AssistantState.Speaking;

    /// <summary>Voice input is a Phase 2 feature. The button exists but is disabled, and says why.</summary>
    public bool VoiceAvailable => false;
    public string MicrophoneToolTip => "Voice input arrives in Phase 2.";

    public string StatusText => State switch
    {
        AssistantState.Listening => "Listening",
        AssistantState.Processing => "Working…",
        AssistantState.Speaking => "Speaking",
        AssistantState.Error => "Something went wrong",
        _ => "Ready",
    };

    private bool CanSubmit() => !IsBusy && !string.IsNullOrWhiteSpace(InputText);
    private bool CanStop() => IsBusy;

    [RelayCommand(CanExecute = nameof(CanSubmit))]
    private async Task SubmitAsync()
    {
        var text = InputText.Trim();
        InputText = string.Empty;
        ErrorBanner = null;
        Messages.Add(new ChatMessage(ChatMessageKind.User, text, DateTimeOffset.Now));

        using var turn = new CancellationTokenSource();
        _currentTurn = turn;
        State = AssistantState.Processing;
        _log.LogInformation("Turn started ({Length} chars)", text.Length); // length only: never log what the user said
        try
        {
            var reply = await _turns.HandleAsync(new UserRequest(text, RequestSource.Text, DateTimeOffset.Now), turn.Token);
            if (turn.IsCancellationRequested)
            {
                // Stopped while the handler was finishing: discard the result rather than present it as done.
                Messages.Add(new ChatMessage(ChatMessageKind.System, "Stopped. Nothing further was done.", DateTimeOffset.Now));
                State = AssistantState.Idle;
                return;
            }

            Messages.Add(new ChatMessage(reply.IsError ? ChatMessageKind.Error : ChatMessageKind.Assistant, reply.Text, DateTimeOffset.Now));
            State = reply.IsError ? AssistantState.Error : AssistantState.Idle;
        }
        catch (OperationCanceledException)
        {
            Messages.Add(new ChatMessage(ChatMessageKind.System, "Stopped. Nothing further was done.", DateTimeOffset.Now));
            State = AssistantState.Idle;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Turn failed");
            ErrorBanner = "That didn't work. Details were written to the log.";
            Messages.Add(new ChatMessage(ChatMessageKind.Error, "That request failed unexpectedly.", DateTimeOffset.Now));
            State = AssistantState.Error;
        }
        finally
        {
            _currentTurn = null;
        }
    }

    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop()
    {
        _log.LogInformation("Stop requested");
        _currentTurn?.Cancel();
    }

    [RelayCommand]
    private void ToggleSettings() => IsSettingsOpen = !IsSettingsOpen;

    [RelayCommand]
    private void DismissError() => ErrorBanner = null;
}
