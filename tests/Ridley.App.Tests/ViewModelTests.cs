using Microsoft.Extensions.Logging;
using Ridley.App.Services;
using Ridley.App.ViewModels;
using Ridley.Core;

namespace Ridley.App.Tests;

internal sealed class CapturingLogger<T> : ILogger<T>
{
    public List<string> Lines { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Lines.Add(formatter(state, exception) + (exception is null ? "" : " | " + exception.Message));
}

internal sealed class FakeHandler(Func<UserRequest, CancellationToken, Task<AssistantReply>> impl) : ITurnHandler
{
    public Task<AssistantReply> HandleAsync(UserRequest request, CancellationToken ct) => impl(request, ct);
}

internal sealed class InMemorySettingsStore : ISettingsStore
{
    public UserSettings Stored { get; private set; } = new();
    public int Saves { get; private set; }
    public Task<UserSettings> LoadAsync(CancellationToken ct) => Task.FromResult(Stored);

    public Task SaveAsync(UserSettings settings, CancellationToken ct)
    {
        Stored = settings;
        Saves++;
        return Task.CompletedTask;
    }
}

public class MainViewModelTests
{
    private static (MainViewModel vm, CapturingLogger<MainViewModel> log, InMemorySettingsStore store) Create(ITurnHandler handler)
    {
        var store = new InMemorySettingsStore();
        var settings = new SettingsViewModel(store, new CapturingLogger<SettingsViewModel>());
        var log = new CapturingLogger<MainViewModel>();
        return (new MainViewModel(handler, settings, new CaptureIndicatorService(new ImmediateUiDispatcher()), log), log, store);
    }

    private static FakeHandler Reply(string text, bool error = false) =>
        new((_, _) => Task.FromResult(new AssistantReply(text, error)));

    [Fact]
    public void Starts_idle_with_an_honest_welcome_and_disabled_voice()
    {
        var (vm, _, _) = Create(Reply("x"));
        Assert.Equal(AssistantState.Idle, vm.State);
        Assert.Equal("Ready", vm.StatusText);
        Assert.False(vm.VoiceAvailable);
        Assert.Single(vm.Messages);
        Assert.Equal(ChatMessageKind.System, vm.Messages[0].Kind);
    }

    [Fact]
    public async Task Submit_adds_both_messages_and_returns_to_idle()
    {
        var (vm, _, _) = Create(Reply("hello back"));
        vm.InputText = "hello";
        await vm.SubmitCommand.ExecuteAsync(null);

        Assert.Equal(AssistantState.Idle, vm.State);
        Assert.Equal("", vm.InputText);
        Assert.Equal(["hello", "hello back"], vm.Messages.Skip(1).Select(m => m.Text));
    }

    [Fact]
    public void Submit_is_disabled_for_blank_input()
    {
        var (vm, _, _) = Create(Reply("x"));
        Assert.False(vm.SubmitCommand.CanExecute(null));
        vm.InputText = "   ";
        Assert.False(vm.SubmitCommand.CanExecute(null));
        vm.InputText = "go";
        Assert.True(vm.SubmitCommand.CanExecute(null));
    }

    [Fact]
    public async Task State_is_processing_while_handler_runs_and_stop_is_only_then_enabled()
    {
        var release = new TaskCompletionSource<AssistantReply>();
        var (vm, _, _) = Create(new FakeHandler((_, _) => release.Task));
        Assert.False(vm.StopCommand.CanExecute(null));

        vm.InputText = "work";
        var running = vm.SubmitCommand.ExecuteAsync(null);
        Assert.Equal(AssistantState.Processing, vm.State);
        Assert.True(vm.IsBusy);
        Assert.True(vm.StopCommand.CanExecute(null));
        Assert.False(vm.SubmitCommand.CanExecute(null)); // no second turn while one is running

        release.SetResult(new AssistantReply("done"));
        await running;
        Assert.Equal(AssistantState.Idle, vm.State);
        Assert.False(vm.StopCommand.CanExecute(null));
    }

    [Fact]
    public async Task Stop_cancels_the_running_turn_and_reports_it_honestly()
    {
        CancellationToken seen = default;
        var (vm, _, _) = Create(new FakeHandler(async (_, ct) =>
        {
            seen = ct;
            await Task.Delay(Timeout.Infinite, ct);
            return new AssistantReply("never");
        }));

        vm.InputText = "long task";
        var running = vm.SubmitCommand.ExecuteAsync(null);
        vm.StopCommand.Execute(null);
        await running;

        Assert.True(seen.IsCancellationRequested);
        Assert.Equal(AssistantState.Idle, vm.State);
        Assert.Contains("Stopped", vm.Messages[^1].Text);
        Assert.DoesNotContain(vm.Messages, m => m.Text == "never");
    }

    [Fact]
    public async Task Result_that_arrives_after_stop_is_discarded()
    {
        var release = new TaskCompletionSource<AssistantReply>();
        var (vm, _, _) = Create(new FakeHandler((_, _) => release.Task));
        vm.InputText = "x";
        var running = vm.SubmitCommand.ExecuteAsync(null);
        vm.StopCommand.Execute(null);
        release.SetResult(new AssistantReply("I did the thing"));
        await running;

        Assert.DoesNotContain(vm.Messages, m => m.Text.Contains("I did the thing"));
        Assert.Contains("Stopped", vm.Messages[^1].Text);
    }

    [Fact]
    public async Task Handler_exception_moves_to_error_state_without_leaking_details_to_the_chat()
    {
        var (vm, log, _) = Create(new FakeHandler((_, _) => throw new InvalidOperationException("secret internal detail")));
        vm.InputText = "boom";
        await vm.SubmitCommand.ExecuteAsync(null);

        Assert.Equal(AssistantState.Error, vm.State);
        Assert.NotNull(vm.ErrorBanner);
        Assert.DoesNotContain(vm.Messages, m => m.Text.Contains("secret internal detail"));
        Assert.Contains(log.Lines, l => l.Contains("secret internal detail")); // but it is in the log
        vm.DismissErrorCommand.Execute(null);
        Assert.Null(vm.ErrorBanner);
    }

    [Fact]
    public async Task Error_reply_shows_as_error_and_user_can_try_again()
    {
        var (vm, _, _) = Create(Reply("could not do that", error: true));
        vm.InputText = "a";
        await vm.SubmitCommand.ExecuteAsync(null);
        Assert.Equal(AssistantState.Error, vm.State);
        Assert.Equal(ChatMessageKind.Error, vm.Messages[^1].Kind);

        vm.InputText = "b";
        Assert.True(vm.SubmitCommand.CanExecute(null));
    }

    [Fact]
    public async Task What_the_user_says_is_never_written_to_the_log()
    {
        var (vm, log, _) = Create(Reply("ok"));
        vm.InputText = "my private sentence about passwords";
        await vm.SubmitCommand.ExecuteAsync(null);
        Assert.DoesNotContain(log.Lines, l => l.Contains("private sentence"));
    }

    [Fact]
    public async Task Default_handler_never_claims_to_have_done_anything()
    {
        var (vm, _, _) = Create(new UnavailableTurnHandler());
        vm.InputText = "open chrome";
        await vm.SubmitCommand.ExecuteAsync(null);
        var reply = vm.Messages[^1].Text;
        Assert.Contains("can't act", reply);
        Assert.DoesNotContain("opened", reply, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Saving_settings_updates_window_behaviour()
    {
        var (vm, _, store) = Create(Reply("x"));
        vm.Settings.AlwaysOnTop = false;
        vm.Settings.MinimiseToTray = false;
        await vm.Settings.SaveCommand.ExecuteAsync(null);

        Assert.False(vm.AlwaysOnTop);
        Assert.False(vm.MinimiseToTray);
        Assert.False(store.Stored.AlwaysOnTop);
        Assert.Equal(1, store.Saves);
    }
}

public class SettingsViewModelTests
{
    private static (SettingsViewModel vm, InMemorySettingsStore store) Create()
    {
        var store = new InMemorySettingsStore();
        return (new SettingsViewModel(store, new CapturingLogger<SettingsViewModel>()), store);
    }

    [Fact]
    public async Task Invalid_values_show_a_message_and_are_not_saved()
    {
        var (vm, store) = Create();
        vm.PushToTalkHotkey = "  ";
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.NotNull(vm.ValidationMessage);
        Assert.Equal(0, store.Saves);
    }

    [Fact]
    public async Task Load_populates_fields_from_store()
    {
        var (vm, store) = Create();
        await store.SaveAsync(new UserSettings { AllowScreenCapture = true, LogLevel = "Warning" }, default);
        await vm.LoadAsync(default);
        Assert.True(vm.AllowScreenCapture);
        Assert.Equal("Warning", vm.LogLevel);
    }

    [Fact]
    public void Provider_status_states_the_truth()
    {
        var (vm, _) = Create();
        Assert.Contains("not configured", vm.ProviderStatus);
    }

    [Fact]
    public async Task Reset_restores_defaults_but_only_persists_on_save()
    {
        var (vm, store) = Create();
        vm.AlwaysOnTop = false;
        vm.ResetToDefaultsCommand.Execute(null);
        Assert.True(vm.AlwaysOnTop);
        Assert.Equal(0, store.Saves);
        await Task.CompletedTask;
    }
}

public class CaptureIndicatorTests
{
    [Fact]
    public void Badge_is_on_exactly_while_a_capture_scope_is_open_and_names_the_destination()
    {
        var indicator = new CaptureIndicatorService(new ImmediateUiDispatcher());
        Assert.False(indicator.IsCapturing);

        var scope = indicator.Begin("Explain this page", "local OCR");
        Assert.True(indicator.IsCapturing);
        Assert.Contains("local OCR", indicator.Description);

        scope.Dispose();
        scope.Dispose(); // idempotent
        Assert.False(indicator.IsCapturing);
    }

    [Fact]
    public void Overlapping_captures_keep_the_badge_until_all_finish()
    {
        var indicator = new CaptureIndicatorService(new ImmediateUiDispatcher());
        var first = indicator.Begin("a", "x");
        var second = indicator.Begin("b", "y");
        first.Dispose();
        Assert.True(indicator.IsCapturing);
        second.Dispose();
        Assert.False(indicator.IsCapturing);
    }
}

public class DotEnvTests
{
    [Fact]
    public void Loads_values_skips_blanks_comments_and_never_overrides_real_environment()
    {
        var file = Path.Combine(Path.GetTempPath(), "ridley-dotenv-" + Guid.NewGuid().ToString("N") + ".env");
        const string fresh = "RIDLEY_TEST_FRESH_VAR";
        const string blank = "RIDLEY_TEST_BLANK_VAR";
        const string existing = "RIDLEY_TEST_EXISTING_VAR";
        try
        {
            Environment.SetEnvironmentVariable(existing, "real");
            File.WriteAllText(file, $"# comment\n{fresh}=\"hello\"\n{blank}=\n{existing}=fromfile\nnot a pair\n");

            var applied = DotEnv.Load(file);

            Assert.Equal(1, applied);
            Assert.Equal("hello", Environment.GetEnvironmentVariable(fresh));
            Assert.Null(Environment.GetEnvironmentVariable(blank));
            Assert.Equal("real", Environment.GetEnvironmentVariable(existing));
        }
        finally
        {
            File.Delete(file);
            foreach (var v in new[] { fresh, blank, existing }) { Environment.SetEnvironmentVariable(v, null); }
        }
    }

    [Fact]
    public void Missing_file_is_fine()
    {
        Assert.Equal(0, DotEnv.Load(Path.Combine(Path.GetTempPath(), "does-not-exist.env")));
    }
}
