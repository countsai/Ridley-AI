# Changelog

## [Unreleased]
### Phase 1: desktop foundation
- Solution, central package management, pinned SDK (.NET 10.0.403), warnings-as-errors.
- `Ridley.Core`: provider-independent contracts revised after CTO review (cancellation everywhere, gate-minted `AuthorisedStep`, validated-only `ValidatedArgs`, capture indicator, any-thread overlay).
- `Ridley.Storage`: SQLite with versioned migrations, settings store, DPAPI secret store.
- `Ridley.App`: WPF MVVM compact window, status states, text input, Stop, settings panel, tray, single instance, Serilog file logging, global exception handling.
- 43 automated tests.

## Phase 0
- Repository audit, architecture, plan, decisions. Repository moved to `C:\dev\Ridley`.

### OceanToken provider spike
- Ridley.Providers: OceanToken client (timeouts, bounded retries, cancellation, usage/cost tracking, spend guard), chat/STT/TTS providers. 33 tests.
- spikes/OceanTokenSpike live harness (not in the solution). Findings in docs/OCEANTOKEN_EVALUATION.md.
