# Technical Decisions

Format: ID · decision · status · rationale. Items marked **NEEDS APPROVAL** block Phase 1.

## A. Framework

### WPF vs WinUI 3 → **WPF** (recommended, accepted for Phase 0 design)

| Requirement | WPF | WinUI 3 |
|---|---|---|
| Floating compact window | Trivial (`WindowStyle=None`, `Topmost`) | Possible, more manual (AppWindow APIs) |
| Transparent topmost click-through overlay | Mature: `AllowsTransparency` + `WS_EX_TRANSPARENT/LAYERED` via interop | Transparent windows are awkward; usually needs SystemBackdrop workarounds or interop anyway |
| Win32 / UIA / capture interop | First-class, vast samples | Fine, but fewer samples for overlay-style apps |
| Packaging | Unpackaged `.exe` works | Windows App SDK runtime / MSIX friction |
| Maturity / hiring | Very mature | Newer, fewer resources |
| Modern look | Needs custom styling | Native Fluent |
| Downside of WPF | Software-rendered layered windows can be slow at 4K; old stack | — |

Overlay is the differentiating feature and is the hardest thing to do well in WinUI 3, so WPF wins. Mitigation for WPF perf: draw only small annotation geometry on a transparent window per monitor. If rendering proves inadequate, the `ITeachingOverlay` interface lets us swap to a Direct2D layered window without touching the rest.

## B. Decisions

| ID | Decision | Status |
|---|---|---|
| D-01 | `git init` inside `Ridley/` so it has its own history, instead of living in the unrelated Desktop repo. Keep it out of OneDrive sync if possible (recommend relocating to e.g. `C:\dev\Ridley`; `bin/obj` in OneDrive causes locks) | **NEEDS APPROVAL** |
| D-02 | Install **.NET 10 SDK (LTS)** via `winget install Microsoft.DotNet.SDK.10`. .NET 8 and 9 reach end of support around Nov 2026, so starting on them would mean an immediate migration. Build with CLI/VS Code; VS 2022 17.10 cannot target .NET 10 (optional: update VS) | **NEEDS APPROVAL** |
| D-03 | TFM `net10.0-windows10.0.22621.0`, nullable on, warnings-as-errors, central package management | Proposed |
| D-04 | MVVM with `CommunityToolkit.Mvvm` | Proposed |
| D-05 | Playwright is **deferred**. It cannot drive the user's already-open Chrome profile without CDP flags, and it adds a large browser download (disk is 14 GB). Phase 3 uses URL launch + UIA; revisit if insufficient | **NEEDS APPROVAL** to defer |
| D-06 | Model provider: one real `OpenAiCompatibleModelProvider` (chat completions, tool calling, image input) pointing at any compatible endpoint (Ollama/LM Studio locally, or any hosted service once a key exists). No paid dependency is assumed. Until an endpoint is configured, the app reports "no model configured" and only offers deterministic fast-path commands | **NEEDS APPROVAL** |
| D-07 | STT: local **Whisper.net** (free, offline) with a small model (`ggml-base.en`, ~150 MB, downloaded on first use with consent). Fallback considered: Windows `System.Speech` (zero download, lower accuracy) | **NEEDS APPROVAL** |
| D-08 | TTS: Windows built-in synthesis (`Windows.Media.SpeechSynthesis`). Free, offline, real | Proposed |
| D-09 | OCR: `Windows.Media.Ocr` (built in; requires installed OCR language pack, normally present with the display language) | Proposed |
| D-10 | Screen capture: `Windows.Graphics.Capture`, GDI `CopyFromScreen` as fallback. On-demand only, with indicator | Proposed |
| D-11 | UI Automation: FlaUI.UIA3 behind `IUIAutomationService` (managed `System.Windows.Automation` is the zero-dependency alternative) | Proposed |
| D-12 | Win32: `Microsoft.Windows.CsWin32` source generator for SendInput, hotkeys, window APIs | Proposed |
| D-13 | Audio capture + volume: NAudio | Proposed |
| D-14 | Storage: `Microsoft.Data.Sqlite`, raw SQL migrations, DB in `%LOCALAPPDATA%\Ridley`. No ORM. Screenshots are never persisted | Proposed |
| D-15 | Documents: `DocumentFormat.OpenXml` for .docx, Markdown always also written | Proposed |
| D-16 | Tests: xUnit + NSubstitute. **Not** FluentAssertions (v8+ is commercially licensed) | Proposed |
| D-17 | Tray icon via WinForms `NotifyIcon` (`UseWindowsForms`) or Hardcodet.NotifyIcon.Wpf | Proposed (minor) |
| D-18 | Mock providers live in `Ridley.Providers.Mock`, are named `Mock*`, show "MOCK" in the UI whenever active, and are only registered when `Ridley:AllowMocks=true` (tests/dev). Never a silent fallback | Proposed |
| D-19 | Security stance: allow-listed typed tools only; no tool executes shell strings; the permission gate is C# code outside model control; destructive/send/purchase actions always `Confirm`; each step's expected outcome is verified | Proposed |

## C. Dependency list (proposed, none installed)

Runtime: `Microsoft.Extensions.Hosting`, `Serilog.Extensions.Hosting` + `Serilog.Sinks.File`, `CommunityToolkit.Mvvm`, `Microsoft.Data.Sqlite`, `Microsoft.Windows.CsWin32`, `NAudio`, `FlaUI.UIA3`, `Whisper.net` + runtime, `DocumentFormat.OpenXml` (Phase 6).
Test: `xunit`, `Microsoft.NET.Test.Sdk`, `NSubstitute`, `coverlet.collector`.
All free/open source. No paid APIs.
