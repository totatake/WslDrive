# AGENTS.md — AI Agent Operational Guide for WslMountDrive

This file provides concise repository context, build/test commands, and architectural invariants for automated coding agents. Human-oriented background and comparisons live in [docs/overview.md](docs/overview.md).

## 1. Repository Map

- `WslMountDrive.sln` — Visual Studio / .NET 8 solution
- `src/WslMountDrive.Core/` — Core library (`net8.0-windows`):
  - `Models/` — `MountRequest`, `MountSession`, `PartitionEntry`, `PhysicalDiskEntry`, `LinuxFileSystemType`
  - `Disk/` — `AlignedDiskStream`, `PartitionTableReader`, `SuperblockInspector`, `DiskScanner`
  - `Wsl/` — `WslCliRunner`, `NbdDiskBridge`, `WslMountService`
  - `Interop/` — `NativeMethods`, `DriveMapper`
- `src/WslMountDrive.Cli/` — Console CLI (`list`, `mount`, `unmount`, `status`, internal `__nbd-daemon`)
- `src/WslMountDrive.App/` — WPF GUI + WinForms system tray app (`app.manifest` requires admin)
- `tests/WslMountDrive.Tests/` — xUnit unit tests (using mocked `IWslCommandRunner` / `INbdBridgeProvider` and synthetic streams)
- `scripts/` — PowerShell build/test/publish helpers (`scripts/build-and-test.ps1`)
- `docs/` — Engineering documentation (Japanese default, lowercase `kebab-case`)

## 2. Build, Test & Publish Commands

```powershell
# Build solution (Release)
dotnet build WslMountDrive.sln -c Release

# Run xUnit unit tests (Release)
dotnet test WslMountDrive.sln -c Release

# Build, test, and optionally publish single-file executables
.\scripts\build-and-test.ps1 -Publish
```

Note: If `WslMountDrive.App.exe` is currently running (including minimized in the system tray), `WslMountDrive.App` Release output file copy may fail due to file locking on `WslMountDrive.Core.dll`. Do not forcefully terminate a user's running instance if it may hold active mounts.

## 3. Architectural Invariants & Guardrails

- **Safe by Default (RO)**: `MountRequest.ReadWrite` defaults to `false` (`ro,noload` for `ext3`/`ext4`, falling back to `ro`). Never make RW the default.
- **NBD Bridge Is Strictly Read-Only**: For physical disks sharing volumes with Windows (`HasWindowsMountedVolumes` / `ERROR_SHARING_VIOLATION`), `AlignedDiskStream` opens with `GENERIC_READ` only and `ReadOnlyNbdServer` rejects writes with `EROFS`. `WslMountService.MountAsync` MUST reject `ReadWrite == true` on NBD bridge paths.
- **WSL2 Unmount Ordering**: Do NOT run `umount /mnt/wsl/<name>` before `wsl --unmount <target>` when detaching the last partition on a disk; `wslservice.exe` requires `/mnt/wsl/<name>` to remain mounted for `mini_init` teardown unless another sibling partition stays mounted.
- **Drive Mapping via `DefineDosDeviceW` + Token Impersonation**: Do NOT switch back to `WNetAddConnection2W` / `net use` for `/mnt/wsl/...` subdirectories (`p9np.dll` rejects >2 path components with error 67/64). Preserve `explorer.exe` non-elevated impersonation + elevated `DefineDosDeviceW` calls.
- **Stateless Session Inspection**: `WslMountService.GetActiveMountsAsync` reconstructs state from `/proc/mounts` and `DriveMapper.GetMappedNetworkDrives()` without relying solely on in-memory dictionaries.
- **Mount Prefix**: Default auto-generated mount names use `wslmountdrive_d<disk>p<part>` / `wslmountdrive_<vhd>` and NBD handshake markers use `WSLMOUNTDRIVE_NBD_READY:` / `WSLMOUNTDRIVE_NBD_ERROR:`.

## 4. Documentation Rules

- Root OSS files (`README.md`, `CONTRIBUTING.md`, `CHANGELOG.md`, `SECURITY.md`, `AGENTS.md`) are in English; `README.ja.md` is the 1:1 structural Japanese twin of `README.md`.
- `docs/` uses lowercase `kebab-case` (except directory hub `README.md` files) and Japanese by default.
- Record major design trade-offs under `docs/adr/NNNN-<title>.md` and update `docs/adr/README.md`.
- Record large feature design packs under `docs/design/<feature>/` and verification/benchmark snapshots under `docs/reports/`.
