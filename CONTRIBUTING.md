# Contributing to WslDrive

Thank you for considering contributing to **WslDrive**!

## Prerequisites

- **OS**: Windows 11 (22H2 or later recommended, with `wsl --mount` support)
- **WSL2**: Enabled with at least one Linux distribution installed (`wsl --list --verbose`)
- **SDK**: .NET 8.0 SDK (`net8.0-windows`) or Visual Studio 2022

## Development Setup & Verification

1. Clone the repository and restore/build the solution:
   ```powershell
   dotnet build WslDrive.sln -c Release
   ```
2. Run the automated unit test suite (see [docs/testing.md](docs/testing.md) for full details and VHDX integration steps):
   ```powershell
   dotnet test WslDrive.sln -c Release
   ```
3. Or use the helper script under `scripts/`:
   ```powershell
   .\scripts\build-and-test.ps1
   ```

## Workflow

1. Create a topic branch from `master` (`feat/<topic>`, `fix/<topic>`, `docs/<topic>`).
2. Keep changes focused and add/update xUnit tests in `tests/WslDrive.Tests/` for behavioral changes.
3. Ensure `dotnet build WslDrive.sln -c Release` and `dotnet test WslDrive.sln -c Release` succeed with zero warnings/errors.
4. Update documentation under [docs/README.md](docs/README.md) (including an ADR under `docs/adr/` for significant architectural trade-offs) and add a note under `[Unreleased]` in `CHANGELOG.md`.

## Commit Style

Concise imperative summaries or [Conventional Commits](https://www.conventionalcommits.org/) are both welcome (e.g., `feat(wsl): ...`, `fix(app): ...`, `docs(adr): ...`).

## Pull Requests

Please complete the checklist in `.github/PULL_REQUEST_TEMPLATE.md` and link any related issues or ADRs.
