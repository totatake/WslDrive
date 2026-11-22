# Changelog

All notable changes to **WslDrive** will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Security
- Validated `FileSystemType` strictly (`[A-Za-z0-9][A-Za-z0-9._-]{0,31}`) and replaced `sh -c` string concatenation with direct argument passing in `WslMountService` to prevent shell command injection.
- Rejected oversized `CmdWrite` payloads exceeding `MaxReadLengthBytes` in `ReadOnlyNbdServer`.
- Added NuGet vulnerability scanning (`dotnet list package --vulnerable --include-transitive`) in CI and tag-to-version verification in Release workflow.

### Fixed
- Fixed drive-mapping failure rollback in `WslMountService.MountAsync` so sibling mounts only `umount` the newly mounted path and reused mounts are preserved without detaching the entire disk.
- Removed unused `WNetAddConnection2W` P/Invoke definition from `NativeMethods`.

## [0.1.0] - 2026-11-22

### Added
- Initial core library (`WslDrive.Core`), CLI (`WslDrive.Cli`), WPF/tray GUI (`WslDrive.App`), and xUnit test suite (`WslDrive.Tests`).
- Pre-mount MBR/EBR/GPT partition table and superblock inspection (`ext2`/`ext3`/`ext4`, `btrfs`, `xfs`, `swap`, `LUKS`).
- `DefineDosDeviceW` + `explorer.exe` non-elevated token impersonation drive letter mapper bypassing Plan 9 (`p9np.dll`) subdirectory restrictions and UAC session isolation.
- Multi-partition concurrent mount and sibling-aware unmount lifecycle management.
- Hybrid Read-Only WSL2 kernel `/dev/nbdX` bridge (`AlignedDiskStream` + `ReadOnlyNbdServer`) for physical disks shared with active Windows volumes.
- Optional Read-Write (`rw`) mount support via CLI `--rw` / `--read-write` and GUI **Read-Write (RW)** toggle (while keeping NBD bridge mounts strictly Read-Only).
- Bilingual GUI localization (`Auto` / `日本語` / `English`) defaulting to `Auto` based on the OS UI culture.
- CLI `--version` / `-v` flag.
- Standardized OSS repository layout (`README.md`, `README.ja.md`, `LICENSE`, `CONTRIBUTING.md`, `SECURITY.md`, `AGENTS.md`, `.github/`, `scripts/`, and reorganized `docs/`).

### Changed
- Renamed project, namespaces, solution/csproj files, WSL mount prefix (`wsldrive_`), and NBD bridge handshake tokens (`WSLDRIVE_NBD_*`) from `LinuxRoDrive` to `WslDrive`.
- Consolidated mount and unmount actions into the middle action bar in `WslDrive.App` while keeping quick-open in the active mounts table.

### Fixed
- Fixed WSL2 unmount ordering so `wsl --unmount` runs directly before `mini_init` teardown and stale `/mnt/wsl/wsldrive_*` mount points are self-bound when needed.
- Fixed `WslDrive.App.exe` lingering as a background process after closing the main window by calling `Application.Current.Shutdown()` in `OnClosed`.
