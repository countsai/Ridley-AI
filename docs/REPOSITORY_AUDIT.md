# Repository & Environment Audit

Date: 2026-11-02 · Phase 0 · Status: findings only, nothing installed or changed.

## 1. Repository

| Item | Finding |
|---|---|
| Path | `C:\Users\Admin\OneDrive\Desktop\Ridley` |
| Contents before Phase 0 | Empty directory. No code, tests, dependencies or config to reuse. |
| Git | **Ridley has no `.git` of its own.** `git rev-parse --show-toplevel` resolves to `C:/Users/Admin/OneDrive/Desktop`, a personal repo (branch `master`, one commit) with dozens of unrelated untracked projects (calmnus, RAG experiments, etc.). |
| Cloud sync | The folder is under **OneDrive**. Build output (`bin/`, `obj/`) and SQLite files inside a synced folder cause file locks and sync churn. |
| Existing secrets/config | None in Ridley. No `API_KEY`/`OPENAI`/`ANTHROPIC`/`AZURE`/`GEMINI` environment variables are set (names checked, values never read). |

**Implication:** if we commit now, Ridley history gets mixed into the Desktop repo. Needs a decision (see TECHNICAL_DECISIONS D-01).

## 2. Development environment

| Item | Finding | Verdict |
|---|---|---|
| OS | Windows 11 Pro 10.0.26200, 64-bit | OK |
| .NET SDK | **None.** `dotnet` not on PATH, nothing in `Program Files\dotnet`, no `DOTNET_*` env vars | **BLOCKER** |
| Visual Studio | Community 2022 **17.10** (supports up to .NET 8), Build Tools 2022 17.10, Build Tools 2019 16.11. MSBuild present. .NET desktop workload not verified | Usable, but too old for .NET 10 in the IDE |
| Windows SDK | 10.0.19041.0 and 10.0.22621.0 | OK (22621 enables WinRT APIs: OCR, Graphics Capture) |
| Git | 2.45.1.windows.1 | OK |
| VS Code | Installed | OK |
| winget | Available. `Microsoft.DotNet.SDK.10` 10.0.403 and `.SDK.9` 9.0.320 found | Install path exists |
| Node / Python | Present (Node, Python 3.12) | Not needed by Ridley; useful only for tooling |
| sqlite3 CLI | Found (Android SDK copy) | Optional debugging aid |
| Microphone | Several endpoints listed (headset, K66 mic); Realtek speakers active. Not yet tested for capture | Verify in Phase 2 |
| Free disk (C:) | **14.1 GB** | Tight: rules out large local models alongside builds |
| Elevation | Session not elevated (DISM query was denied) | Ridley must run unelevated; see risk R-06 |
| Chrome | Not verified | Check before Phase 3 acceptance Test 1 |

## 3. WPF prerequisites

- WPF ships with the .NET SDK on Windows (`Microsoft.NET.Sdk` + `UseWPF`). Once an SDK is installed, no extra workload is needed.
- Target framework moniker would be `net10.0-windows10.0.22621.0` to reach WinRT APIs.
- **Not currently satisfiable: no SDK.**

## 4. Missing dependencies

1. .NET SDK (blocking).
2. A speech-to-text engine with real quality (choice pending).
3. A model endpoint (no credentials; choice pending).
4. Optional: Playwright browsers (large download; deferred).

## 5. Reuse assessment

Nothing to reuse. Greenfield. The unrelated Desktop projects (`calmnus`, `RAGAWS`, `ai-travel-planner-mcp`, ...) were not inspected and are out of scope.
