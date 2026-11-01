# Testing

Run everything: `dotnet test Ridley.sln` (43 tests as of Phase 1).

| Project | Covers |
|---|---|
| Ridley.Core.Tests | Result type; settings validation and privacy defaults; **architecture rules enforced by reflection**: every async contract takes a `CancellationToken`; `IActionExecutor` accepts only `AuthorisedStep`; `AuthorisedStep`/`ValidatedArgs` have no public constructors; `PlannedStep` cannot carry raw model JSON; screen/audio payloads redact in `ToString`; no setting can disable confirmations |
| Ridley.Storage.Tests | Settings round-trip, overwrite, corrupt-row fallback, invalid-save rejection, cancellation, migration versioning; DPAPI round-trip, ciphertext check, env-var priority, delete |
| Ridley.App.Tests | MainViewModel state machine, Stop/cancel (including discarding results that arrive after Stop), error handling, no user text in logs, honest default handler; settings VM; capture-indicator scoping; `.env` loader |

## Manual checks (Phase 1, run via UI Automation against the real executable)
Launch; status "Ready"; Mic disabled; Stop disabled when idle; Send enabled only with text; submit shows an honest "can't act" reply; settings save and persist across restart; invalid input rejected; closing the window keeps the process alive in the tray; second launch is refused; log file contains no typed text.

## Not yet tested
Real microphone, speech, model calls, UI Automation actions, capture, overlay, documents: none exist yet. Integration tests needing an interactive desktop will live in `Ridley.Platform.Windows.Tests` (Phase 3).
