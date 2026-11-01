# Implementation Plan

Status: proposal. Estimates are rough engineering sessions (one focused working session ≈ half a day), not calendar commitments. Each phase ends with a report in the format required by the master spec §9 and waits for approval.

| Phase | Deliverable | Est. | Gate |
|---|---|---|---|
| 0 | Audit, architecture, decisions (this) | done | CTO approval + decisions D-01..D-06 |
| 1 | Desktop foundation | 3–4 | App launches; settings persist; DI/logging/tests green |
| 2 | Voice foundation | 4–6 | Hold hotkey, speak, get transcript and spoken reply |
| 3 | Computer control | 8–12 | Acceptance Tests 1–4, 10–12 |
| 4 | Screen understanding | 6–8 | Tests 5, 6, 9 |
| 5 | Visual teaching overlay | 5–7 | Test 7 |
| 6 | Document creation | 3–4 | Test 8 |
| 7 | Integration & hardening | 5–7 | Full 12-test suite, installer/publish build |

Total ≈ 34–48 sessions. Phase 3 and 4 carry the most uncertainty.

## Phase 1 — Desktop foundation
- Prereq: .NET SDK installed (D-02), Ridley git repo initialised (D-01).
- `Ridley.sln`, `Directory.Build.props`, `Directory.Packages.props`, `global.json`.
- `Ridley.Core` (first contracts and result types), `Ridley.Storage` (SQLite settings), `Ridley.App`.
- WPF compact floating window: status indicator (idle/listening/processing/speaking), text input, conversation list, stop button, settings panel, tray icon.
- Host/DI, Serilog, config layering, `ISecretStore` (env + DPAPI).
- Tests: settings round-trip, config layering, ViewModel state transitions.

## Phase 2 — Voice
- `IMicrophoneCapture` (NAudio/WASAPI), global push-to-talk hotkey, STT provider, TTS provider, turn manager, barge-in/cancel.
- Utterance classification (command / question / explain / teach / create) via planner stub that already uses the real model interface when a provider is configured.
- Honest mic-permission and no-device errors.

## Phase 3 — Computer control
- `Ridley.Platform.Windows`: launcher (allow-listed app registry + Start-menu/App Paths resolution), window manager, SendInput wrapper, media keys, Core Audio volume, UIA service, file/folder open.
- `Ridley.Orchestration`: tool registry + schema validation, `ITaskPlanner`, `IPermissionGate`, `ITaskRunner`, `IActionVerifier`, clarification flow, bounded retries, history.
- Browser: first pass uses URL launch + UIA; Playwright only if D-05 approved.
- Tests: heavy unit tests with fakes; integration tests against Notepad/Explorer.

## Phase 4 — Screen understanding
- Active window, capture (Windows.Graphics.Capture, GDI fallback), OCR (Windows.Media.Ocr), UIA text extraction, content selection, grounded prompt builder that labels each fact's source (OCR / UIA / image), visible capture indicator.
- Conversation context holds the last capture so follow-ups work (Test 9).

## Phase 5 — Visual teaching
- Transparent, topmost, click-through WPF overlay per monitor, DPI-aware; primitives: highlight, circle, arrow, rect, label, numbered step.
- Model returns annotation targets as OCR word boxes / UIA element IDs (not raw pixel guesses where avoidable); anchor strategy re-resolves on window move/resize; falls back to dropping the annotation with a notice.

## Phase 6 — Document creation
- `Ridley.Documents`: Markdown + .docx (Open XML SDK) + simple diagram (SVG/PNG). Save to `Documents\Ridley`, verify, report path. Prompt constrains output to source material.

## Phase 7 — Hardening
- Run all 12 acceptance tests on this machine, record results in TESTING.md, fix, write CHANGELOG, produce a self-contained publish build and README run instructions.

---
**Phase 1 status (2026-11-02): complete, awaiting CTO review.** See CHANGELOG.md and TECHNICAL_DECISIONS.md section D. Phase 2 not started.
