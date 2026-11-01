# Ridley Architecture (proposed)

Status: **proposal for CTO approval.** No code exists yet.

## 1. Style

Modular monolith, one process, one solution. Modules are separated by *projects and interfaces*, not by network boundaries. Dependency direction is strictly inward toward `Ridley.Core`.

## 2. Diagram

```
                        ┌──────────────────────────────┐
                        │          Ridley.App          │  WPF · MVVM · composition root
                        │  Shell · Tray · Settings ·   │  (DI wiring, hotkey, overlay window)
                        │  Overlay window · ViewModels │
                        └──────┬───────────┬───────────┘
                               │           │
              ┌────────────────▼──┐   ┌────▼─────────────────┐
              │ Ridley.Orchestration│   │ Ridley.Platform.Windows│
              │ Intent→Plan→Authorise│   │ UIA · Input · Windows  │
              │ →Act→Observe→Verify  │   │ Capture · OCR · Media  │
              │ Permission gate      │   │ Launch · Hotkey · Mic  │
              │ Conversation context │   └────────────┬───────────┘
              └───────┬──────────────┘                │
                      │         ┌─────────────────┐   │
                      │         │ Ridley.Providers│   │   Model / STT / TTS implementations
                      │         │ (+ mock, labelled)│  │
                      │         └────────┬────────┘   │
                      │   ┌──────────────┴───────┐    │
                      │   │    Ridley.Storage    │    │   SQLite: settings, history, conversations
                      │   └──────────┬───────────┘    │
                      └──────────────▼────────────────▼
                              ┌──────────────┐
                              │  Ridley.Core │   contracts, models, results, permission types
                              └──────────────┘            (no Windows, no UI, no I/O deps)
```

Rules: `Core` references nothing. `Orchestration`, `Platform.Windows`, `Providers`, `Storage` reference only `Core` and never each other. `App` is the only project that references everything and wires it via DI. Orchestration reaches the OS only through Core interfaces, so it is fully unit-testable with fakes.

## 3. Solution structure

Projects are created **only when a phase needs them**; none is created empty.

```
Ridley/
├─ Ridley.sln
├─ Directory.Build.props         # TFM, nullable, analyzers, warnings-as-errors
├─ Directory.Packages.props      # central package versions
├─ global.json                   # pins SDK
├─ src/
│  ├─ Ridley.Core/               # Phase 1
│  ├─ Ridley.App/                # Phase 1
│  ├─ Ridley.Storage/            # Phase 1 (settings) 
│  ├─ Ridley.Providers/          # Phase 2 (STT/TTS), Phase 4 (model)
│  ├─ Ridley.Orchestration/      # Phase 2 (intent), Phase 3 (task loop)
│  ├─ Ridley.Platform.Windows/   # Phase 3
│  └─ Ridley.Documents/          # Phase 6 (docx/md/diagram export)
├─ tests/
│  ├─ Ridley.Core.Tests/
│  ├─ Ridley.Orchestration.Tests/     # planner, gate, loop, cancellation (fakes)
│  └─ Ridley.Platform.Windows.Tests/  # integration; needs interactive desktop
├─ docs/
└─ README.md  .gitignore  .env.example
```

## 4. Core pipeline

```
UNDERSTAND → PLAN → AUTHORISE → ACT → OBSERVE → VERIFY → CONTINUE
```

- **UNDERSTAND**: `ITaskPlanner` sends the utterance, conversation context and the registered tool schemas to `IModelProvider` using structured tool calling. A deterministic fast-path handles trivial unambiguous commands ("scroll down", "volume up") for latency and offline use; it is an optimisation, not the only intent mechanism.
- **PLAN**: output is a `TaskPlan` of typed `PlannedStep`s. Parameters are validated against each tool's schema; invalid output is rejected, never executed.
- **AUTHORISE**: `IPermissionGate` (plain C#, not model-controlled) assigns each step a `RiskLevel`. `Confirm` steps pause for user approval. `Blocked` steps never run.
- **ACT**: `IActionExecutor` runs one step, choosing the highest-preference strategy available: app API → UI Automation → browser automation → keyboard/mouse fallback.
- **OBSERVE / VERIFY**: `IActionVerifier` checks the step's declared `ExpectedOutcome` against observed state (window exists, foreground changed, file exists, volume changed...). Bounded retries; a failure stops the plan and is reported honestly.
- **Cancellation**: one `CancellationToken` per task, checked between steps and passed into every async call. Cancel = stop issuing steps, release input, report what completed.

## 5. Core interface definitions (draft, `Ridley.Core`)

```csharp
// Shared
public readonly record struct Rect(int X, int Y, int Width, int Height);
public enum RiskLevel { Safe, Confirm, Blocked }
public sealed record Result<T>(bool Ok, T? Value, string? Error, ErrorKind Kind = ErrorKind.None);
public enum ErrorKind { None, Unsupported, Ambiguous, NotFound, Denied, Timeout, Cancelled, Failed }

// --- Model / LLM
public interface IModelProvider {
    string Name { get; }
    ModelCapabilities Capabilities { get; }           // Vision, ToolCalling, Streaming
    Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken ct);
    IAsyncEnumerable<ModelDelta> StreamAsync(ModelRequest request, CancellationToken ct);
}
// ModelRequest: messages, tool definitions (JSON schema), optional images (by explicit user-triggered capture only)

// --- Voice
public interface ISpeechToTextProvider {
    string Name { get; }
    Task<Transcript> TranscribeAsync(AudioClip audio, SttOptions options, CancellationToken ct);
}
public interface ITextToSpeechProvider {
    string Name { get; }
    Task SpeakAsync(string text, TtsOptions options, CancellationToken ct);   // cancellable = barge-in/stop
}
public interface IMicrophoneCapture {                // push-to-talk source
    Task StartAsync(CancellationToken ct);
    Task<AudioClip> StopAsync();
    event EventHandler<float> LevelChanged;
}

// --- Perception
public interface IScreenObserver {
    WindowInfo GetActiveWindow();
    Task<ScreenCapture> CaptureAsync(CaptureTarget target, CancellationToken ct); // explicit, indicator-raising
    ScreenGeometry GetGeometry();                    // monitors, DPI, virtual-screen bounds
}
public interface IOcrService {
    Task<OcrResult> RecognizeAsync(ScreenCapture image, CancellationToken ct);    // text + word bounding boxes
}
public interface IUIAutomationService {
    Task<UiTree> SnapshotAsync(WindowHandle window, UiQuery query, CancellationToken ct);
    Task<Result<UiElementRef>> FindAsync(WindowHandle window, UiSelector selector, CancellationToken ct);
    Task<Result<Unit>> InvokeAsync(UiElementRef element, UiPattern pattern, CancellationToken ct);
}

// --- Action
public interface IActionExecutor {
    Task<ActionOutcome> ExecuteAsync(PlannedStep step, CancellationToken ct);
}
public interface IActionTool {                       // one per typed action; registered in DI
    ToolDescriptor Descriptor { get; }               // name, JSON schema, RiskLevel policy
    Task<ActionOutcome> RunAsync(ValidatedArgs args, ActionContext ctx, CancellationToken ct);
}
public interface IBrowserService {                   // Playwright-backed, optional
    Task<Result<Unit>> NavigateAsync(Uri url, CancellationToken ct);
    Task<Result<PageSnapshot>> SnapshotAsync(CancellationToken ct);
}

// --- Orchestration
public interface ITaskPlanner {
    Task<TaskPlan> PlanAsync(UserRequest request, ConversationContext ctx, CancellationToken ct);
}
public interface IPermissionGate {
    GateDecision Evaluate(PlannedStep step, UserSettings settings);   // Allow | NeedsConfirmation(reason) | Deny(reason)
}
public interface IActionVerifier {
    Task<Verification> VerifyAsync(PlannedStep step, ActionOutcome outcome, CancellationToken ct);
}
public interface ITaskRunner {                       // owns the loop above
    Task<TaskReport> RunAsync(TaskPlan plan, IProgress<TaskEvent> progress, CancellationToken ct);
}

// --- Teaching & creation
public interface ITeachingOverlay {
    void Show(IReadOnlyList<Annotation> annotations);   // Circle, Arrow, Highlight, Label, Step, Rect
    void Update(string annotationId, Annotation replacement);
    void Remove(string annotationId);
    void Clear();
    bool Enabled { get; set; }
}
public interface IDocumentCreator {
    Task<Result<CreatedDocument>> CreateAsync(DocumentSpec spec, CancellationToken ct); // saves, then verifies existence
}

// --- Storage / config
public interface ISettingsStore { UserSettings Load(); void Save(UserSettings s); }
public interface ISecretStore  { string? Get(string name); void Set(string name, string value); }
public interface IActionHistory { Task AppendAsync(HistoryEntry e); Task<IReadOnlyList<HistoryEntry>> RecentAsync(int n); }
```

Notes:
- All long-running members take `CancellationToken`; none block the UI thread.
- Screenshots are passed to `IModelProvider` only when the user's request needs vision, and the shell shows a visible "screen captured / sent to <provider>" indicator. Never continuous.
- `ActionContext` carries the *current* target window so tools never act on "whatever is foreground by accident".

## 6. Cross-cutting standards

| Concern | Standard |
|---|---|
| DI | `Microsoft.Extensions.Hosting` generic host in `App`; constructor injection only; no service locator; providers selected by name from config; registration lives in per-project `AddRidleyXxx()` extension methods |
| Logging | `ILogger<T>` everywhere; Serilog file sink (rolling, `%LOCALAPPDATA%\Ridley\logs`); structured properties; **never** log screenshots, OCR text, transcripts or keys at Information level (Debug-only, off by default) |
| Configuration | `appsettings.json` (defaults, committed) < user settings (SQLite) < environment variables `RIDLEY__*` < `.env` in dev only |
| Secrets | Env vars / `dotnet user-secrets` in dev; DPAPI (`ProtectedData`, CurrentUser) for any key the user enters in Settings; nothing in source or SQLite plaintext |
| Errors | Expected failures return `Result<T>`/`ActionOutcome` with `ErrorKind`; exceptions only for programmer errors/unrecoverable; one top-level handler surfaces a readable message and logs detail |
| Async | `async/await` end to end; `ConfigureAwait(false)` in libraries; UI marshalled only in `App`; no `.Result`/`.Wait()` |
| Unit tests | xUnit + NSubstitute; Orchestration tested exhaustively with fakes (plan validation, gate, retries, cancellation, ambiguity) |
| Integration tests | Separate project, category-tagged, run on an interactive desktop (launch Notepad, verify window, volume, overlay shows); excluded from default CI |
| Git | Conventional Commits (`feat:`, `fix:`, `docs:`, `test:`, `chore:`), trunk-based with short feature branches, one phase = one PR/tag, no secrets, `.env` ignored |
