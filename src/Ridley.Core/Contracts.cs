using System.Text.Json;

namespace Ridley.Core;

// Conventions for every contract in this file:
//  * Anything asynchronous takes a CancellationToken. No exceptions.
//  * Implementations must not block the calling thread and must not assume which thread calls them.
//  * Expected failures come back as Result/ActionOutcome with an ErrorKind; exceptions mean bugs.

// ---- Model / voice ----------------------------------------------------------
public interface IModelProvider
{
    string Name { get; }
    ModelCapabilities Capabilities { get; }
    Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken ct);
    IAsyncEnumerable<ModelDelta> StreamAsync(ModelRequest request, CancellationToken ct);
}

public interface ISpeechToTextProvider
{
    string Name { get; }
    Task<Transcript> TranscribeAsync(AudioClip audio, SttOptions options, CancellationToken ct);
}

public interface ITextToSpeechProvider
{
    string Name { get; }
    /// <summary>Completes when speech ends. Cancelling the token stops speech immediately (barge-in / Stop).</summary>
    Task SpeakAsync(string text, TtsOptions options, CancellationToken ct);
}

public interface IMicrophoneCapture
{
    Task StartAsync(CancellationToken ct);
    Task<AudioClip> StopAsync(CancellationToken ct);
    event EventHandler<float> LevelChanged;
}

// ---- Perception ---------------------------------------------------------------
/// <summary>
/// Shows the user that the screen is being captured and where the pixels are going. Every capture implementation
/// must open a scope before touching the screen and dispose it afterwards.
/// </summary>
public interface ICaptureIndicator
{
    IDisposable Begin(string purpose, string destination);
}

public interface IScreenObserver
{
    Task<WindowInfo?> GetActiveWindowAsync(CancellationToken ct);
    /// <summary>On demand only. Must be refused unless <see cref="UserSettings.AllowScreenCapture"/> is true.</summary>
    Task<Result<ScreenCapture>> CaptureAsync(CaptureTarget target, string purpose, CancellationToken ct);
    Task<ScreenGeometry> GetGeometryAsync(CancellationToken ct);
}

public interface IOcrService
{
    Task<OcrResult> RecognizeAsync(ScreenCapture image, CancellationToken ct);
}

public interface IUIAutomationService
{
    Task<UiTree> SnapshotAsync(WindowHandle window, UiQuery query, CancellationToken ct);
    Task<Result<UiElementRef>> FindAsync(WindowHandle window, UiSelector selector, CancellationToken ct);
    Task<Result<Unit>> InvokeAsync(UiElementRef element, UiPattern pattern, CancellationToken ct);
}

// ---- Actions / orchestration ---------------------------------------------------
public interface IToolArgumentValidator
{
    /// <summary>The ONLY producer of <see cref="ValidatedArgs"/>. Rejects anything not matching the tool's schema.</summary>
    Result<ValidatedArgs> Validate(ToolDescriptor tool, JsonElement rawArguments);
}

public interface IPermissionGate
{
    /// <summary>
    /// Final authority on execution. Applies fixed policy (Blocked is never allowed; Confirm always asks the user
    /// through <paramref name="prompt"/>), then returns an <see cref="AuthorisedStep"/> or a Denied result.
    /// </summary>
    Task<Result<AuthorisedStep>> AuthoriseAsync(PlannedStep step, UserSettings settings, IConfirmationPrompt prompt, CancellationToken ct);
}

public interface IActionExecutor
{
    /// <summary>Accepts only gate-approved steps. There is deliberately no overload taking a <see cref="PlannedStep"/>.</summary>
    Task<ActionOutcome> ExecuteAsync(AuthorisedStep step, ActionContext context, CancellationToken ct);
}

public interface IActionVerifier
{
    Task<Verification> VerifyAsync(AuthorisedStep step, ActionOutcome outcome, CancellationToken ct);
}

public interface ITaskPlanner
{
    Task<TaskPlan> PlanAsync(UserRequest request, ConversationContext context, CancellationToken ct);
}

public interface ITaskRunner
{
    Task<TaskReport> RunAsync(TaskPlan plan, IProgress<TaskEvent> progress, CancellationToken ct);
}

/// <summary>Handles one user turn end to end. The shell depends on this, not on planner internals.</summary>
public interface ITurnHandler
{
    Task<AssistantReply> HandleAsync(UserRequest request, CancellationToken ct);
}

public interface IBrowserService
{
    Task<Result<Unit>> NavigateAsync(Uri url, CancellationToken ct);
}

// ---- Teaching / documents ----------------------------------------------------------
/// <summary>
/// Callable from ANY thread. Implementations own the marshalling onto the UI thread (WPF Dispatcher); callers must
/// never touch overlay visuals directly.
/// </summary>
public interface ITeachingOverlay
{
    bool Enabled { get; }
    Task SetEnabledAsync(bool enabled, CancellationToken ct);
    Task ShowAsync(IReadOnlyList<Annotation> annotations, CancellationToken ct);
    Task UpdateAsync(string annotationId, Annotation replacement, CancellationToken ct);
    Task RemoveAsync(string annotationId, CancellationToken ct);
    Task ClearAsync(CancellationToken ct);
}

public interface IDocumentCreator
{
    /// <summary>Saves the document, then verifies the file exists before reporting success.</summary>
    Task<Result<CreatedDocument>> CreateAsync(DocumentSpec spec, CancellationToken ct);
}

// ---- Storage / config -----------------------------------------------------------------
public interface ISettingsStore
{
    Task<UserSettings> LoadAsync(CancellationToken ct);
    Task SaveAsync(UserSettings settings, CancellationToken ct);
}

public interface ISecretStore
{
    Task<string?> GetAsync(string name, CancellationToken ct);
    Task SetAsync(string name, string value, CancellationToken ct);
    Task DeleteAsync(string name, CancellationToken ct);
}

public interface IActionHistory
{
    Task AppendAsync(HistoryEntry entry, CancellationToken ct);
    Task<IReadOnlyList<HistoryEntry>> RecentAsync(int count, CancellationToken ct);
}
