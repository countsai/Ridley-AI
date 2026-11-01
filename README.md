# Ridley

A Windows voice-first assistant that operates the PC, understands what is on screen, and teaches through on-screen annotations.

**Status: Phase 1 complete (desktop foundation).** The shell, settings, storage, logging and contracts exist. Voice, model integration, computer control, screen understanding, overlay and document creation are **not implemented yet** and the app says so rather than pretending.

## Build and run
Prerequisites: Windows 11, .NET 10 SDK (10.0.403 pinned in `global.json`), Windows SDK 10.0.22621.

```powershell
dotnet build Ridley.sln
dotnet test  Ridley.sln
dotnet run --project src/Ridley.App
```

Data lives in `%LOCALAPPDATA%\Ridley` (`ridley.db`, `logs\`). Override for experiments with `RIDLEY__Storage__DatabasePath` and `RIDLEY__Logging__Directory`.

## Layout
```
src/Ridley.Core      contracts, models, Result, settings (no Windows/UI/IO dependencies)
src/Ridley.Storage   SQLite settings store, DPAPI secret store
src/Ridley.App       WPF shell (MVVM), composition root, tray, logging
tests/               xUnit: Core, Storage, App
docs/                audit, architecture, plan, decisions
```

## Configuration and secrets
Layered: `appsettings.json` < user settings (SQLite) < `RIDLEY__*` environment variables < `.env` (Debug builds only). Copy `.env.example` to `.env` for development. Keys are read from environment variables or stored DPAPI-encrypted per Windows user; they are never written to source, logs or plaintext.

## Documentation
[Audit](docs/REPOSITORY_AUDIT.md) · [Architecture](docs/ARCHITECTURE.md) · [Plan](docs/IMPLEMENTATION_PLAN.md) · [Decisions](docs/TECHNICAL_DECISIONS.md) · [Testing](TESTING.md) · [Changelog](CHANGELOG.md)

## Conventions
Conventional Commits; one phase per PR/tag; see ARCHITECTURE.md §6.
