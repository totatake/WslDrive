# Changelog

All notable changes to **WslDrive** will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added
- Initial core library (`WslDrive.Core`), CLI (`WslDrive.Cli`), WPF/tray GUI (`WslDrive.App`), and xUnit test suite (`WslDrive.Tests`).
- Pre-mount MBR/EBR/GPT partition table and superblock inspection (`ext2`/`ext3`/`ext4`, `btrfs`, `xfs`, `swap`, `LUKS`).
- `DefineDosDeviceW` + `explorer.exe` non-elevated token impersonation drive letter mapper bypassing Plan 9 (`p9np.dll`) subdirectory restrictions and UAC session isolation.
- Multi-partition concurrent mount and sibling-aware unmount lifecycle management.
- Hybrid Read-Only WSL2 kernel `/dev/nbdX` bridge (`AlignedDiskStream` + `ReadOnlyNbdServer`) for physical disks shared with active Windows volumes.
- Optional Read-Write (`rw`) mount support via CLI `--rw` / `--read-write` and GUI **Read-Write (RW)** toggle (while keeping NBD bridge mounts strictly Read-Only).
- Standardized OSS repository layout (`README.md`, `README.ja.md`, `LICENSE`, `CONTRIBUTING.md`, `SECURITY.md`, `AGENTS.md`, `.github/`, `scripts/`, and reorganized `docs/`).

### Changed
- Renamed project, namespaces, solution/csproj files, WSL mount prefix (`wsldrive_`), and NBD bridge handshake tokens (`WSLDRIVE_NBD_*`) from `LinuxRoDrive` to `WslDrive`.
- Fixed WSL2 unmount ordering so `wsl --unmount` runs directly before `mini_init` teardown and stale `/mnt/wsl/wsldrive_*` mount points are self-bound when needed.
