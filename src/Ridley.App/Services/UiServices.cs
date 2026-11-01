using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Ridley.Core;

namespace Ridley.App.Services;

/// <summary>
/// The single door to the WPF UI thread. Anything that may run off-thread (overlay updates, capture status,
/// progress events) goes through here; nothing else touches visuals directly.
/// </summary>
public interface IUiDispatcher
{
    void Post(Action action);
    Task InvokeAsync(Action action, CancellationToken ct);
}

public sealed class WpfUiDispatcher(Dispatcher dispatcher) : IUiDispatcher
{
    public void Post(Action action) => dispatcher.BeginInvoke(action);

    public async Task InvokeAsync(Action action, CancellationToken ct) =>
        await dispatcher.InvokeAsync(action, DispatcherPriority.Normal, ct);
}

/// <summary>Runs inline. For unit tests.</summary>
public sealed class ImmediateUiDispatcher : IUiDispatcher
{
    public void Post(Action action) => action();

    public Task InvokeAsync(Action action, CancellationToken ct)
    {
        action();
        return Task.CompletedTask;
    }
}

/// <summary>
/// Drives the "screen is being captured" badge. Any capture code must open a scope via <see cref="Begin"/>, so the
/// badge is visible for exactly as long as pixels are being read, and says where they go.
/// </summary>
public sealed partial class CaptureIndicatorService(IUiDispatcher ui) : ObservableObject, ICaptureIndicator
{
    private int _active;
    private readonly object _gate = new();
    private readonly List<string> _descriptions = [];

    [ObservableProperty]
    private bool _isCapturing;

    [ObservableProperty]
    private string _description = string.Empty;

    public IDisposable Begin(string purpose, string destination)
    {
        var text = $"{purpose} → {destination}";
        lock (_gate)
        {
            _active++;
            _descriptions.Add(text);
        }

        Refresh();
        return new Scope(this, text);
    }

    private void End(string text)
    {
        lock (_gate)
        {
            _active--;
            _descriptions.Remove(text);
        }

        Refresh();
    }

    private void Refresh()
    {
        bool capturing;
        string description;
        lock (_gate)
        {
            capturing = _active > 0;
            description = string.Join("; ", _descriptions);
        }

        ui.Post(() =>
        {
            IsCapturing = capturing;
            Description = description;
        });
    }

    private sealed class Scope(CaptureIndicatorService owner, string text) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.End(text);
            }
        }
    }
}

/// <summary>Real confirmation dialog for consequential actions. The model has no way to answer it.</summary>
public sealed class MessageBoxConfirmationPrompt(IUiDispatcher ui) : IConfirmationPrompt
{
    public async Task<bool> ConfirmAsync(ConfirmationRequest request, CancellationToken ct)
    {
        var approved = false;
        await ui.InvokeAsync(() =>
        {
            var answer = MessageBox.Show(
                request.Detail,
                "Ridley needs your approval: " + request.Title,
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No); // default button is No
            approved = answer == MessageBoxResult.Yes;
        }, ct);
        return approved && !ct.IsCancellationRequested;
    }
}

/// <summary>
/// Honest stand-in for the planner until Phase 3. It never pretends to understand or act; it says what is missing.
/// </summary>
public sealed class UnavailableTurnHandler : ITurnHandler
{
    public Task<AssistantReply> HandleAsync(UserRequest request, CancellationToken ct) =>
        Task.FromResult(new AssistantReply(
            "I can't act on that yet. The planner and model are not connected in this build (Phase 1: desktop foundation only).",
            IsError: false));
}
