# Ridley

A Windows voice-first assistant that operates the PC, understands what is on screen, and teaches through on-screen annotations.

**Status: Phase 0 (design). There is no runnable code yet.** The repository currently contains only planning documents awaiting CTO approval.

## Planned stack
C# · .NET 10 · WPF (MVVM) · SQLite · modular monolith with provider-independent interfaces.

## Documentation
- [Repository & environment audit](docs/REPOSITORY_AUDIT.md)
- [Architecture](docs/ARCHITECTURE.md)
- [Implementation plan](docs/IMPLEMENTATION_PLAN.md)
- [Technical decisions](docs/TECHNICAL_DECISIONS.md)

## Prerequisites (once Phase 1 begins)
- Windows 11, .NET 10 SDK, Windows SDK 10.0.22621.

## Configuration & secrets
Copy `.env.example` to `.env` (git-ignored) for development. Never commit keys.

## Conventions
Conventional Commits; see ARCHITECTURE.md §6.
