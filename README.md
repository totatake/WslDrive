# WslDrive

> **Mount Linux partitions (`ext4` / `btrfs` / `xfs`) on Windows 11 as native drive letters via WSL2 — safe Read-Only (`ro,noload`) by default, with optional Read-Write (`--rw`) support.**

**Version:** `v0.1.0` | **Language:** English | [日本語](README.ja.md)

---

## Overview

`WslDrive` is a C# utility (CLI and WPF GUI with system tray integration) for Windows 11 that mounts Linux partitions on separate physical disks, `.vhdx` virtual disks, or Windows-shared physical disks via WSL2 and exposes them as ordinary Windows drive letters (such as `L:\`) in File Explorer and elevated terminals.

No third-party kernel filesystem drivers (Dokan, WinFsp, etc.) are required. It uses Windows 11's built-in WSL2 Linux kernel drivers combined with NT DOS device mapping (`DefineDosDeviceW`) and non-elevated desktop token impersonation (`explorer.exe`).

## Key Features

- **Zero Third-Party Kernel Drivers & Full Linux FS Compatibility**: Uses the genuine WSL2 Linux kernel (`ext4`, `btrfs`, `xfs`, etc.) without installing custom kernel drivers.
- **Safe by Default (`ro,noload`) with Optional Read-Write (`--rw`)**: Defaults to `ro,noload` (skipping journal replay on `ext3`/`ext4` to prevent accidental writes); users can explicitly enable Read-Write (`rw`) via `--rw` in CLI or the RW checkbox in GUI.
- **Pre-Mount Superblock Inspection**: Reads GPT/MBR/EBR partition tables and Linux superblocks (`ext2/3/4`, `btrfs`, `xfs`, `swap`, `LUKS`) directly using read-only handles to show filesystem types, labels, and UUIDs before mounting.
- **Automatic Plan 9 & UAC Session Bypass**: Maps `/mnt/wsl/<name>` subdirectories directly using `DefineDosDeviceW` and `explorer.exe` token impersonation so drives appear in both standard File Explorer and elevated terminals without modifying `EnableLinkedConnections`.
- **Hybrid Read-Only NBD Bridge for Windows-Shared Disks**: Automatically falls back to an in-kernel WSL2 `/dev/nbdX` bridge when a physical disk contains active Windows volumes (which `wsl --mount` rejects with `ERROR_SHARING_VIOLATION`); strictly Read-Only for data safety.
- **Multi-Partition Lifecycle Control**: Supports mounting multiple partitions from the same physical disk/VHDX concurrently and unmounting individual partitions without detaching active siblings.

## Quick Start

### Build & Test

Requires Windows 11, WSL2, and [.NET 8.0 Desktop Runtime (x64)](https://dotnet.microsoft.com/download/dotnet/8.0) (`winget install Microsoft.DotNet.DesktopRuntime.8`) for pre-built releases, or the .NET 8.0 SDK (`net8.0-windows`) to build from source:

```powershell
dotnet build WslDrive.sln -c Release
dotnet test WslDrive.sln -c Release
```

Or run the helper script:

```powershell
.\scripts\build-and-test.ps1
```

### CLI Usage

Run from an Administrator terminal:

```powershell
# List physical disks and detected Linux partitions
WslDrive.Cli.exe list

# Mount PhysicalDrive1 Partition 2 to L: (Read-Only by default: ro,noload)
WslDrive.Cli.exe mount --disk 1 --partition 2 --letter L:

# Mount as Read-Write (rw)
WslDrive.Cli.exe mount --disk 1 --partition 2 --letter L: --rw

# Check active WSL2 mounts and mapped drive letters
WslDrive.Cli.exe status

# Unmount and remove drive mapping
WslDrive.Cli.exe unmount --letter L:
```

### GUI Usage

1. Launch `WslDrive.App.exe` (requests UAC elevation via `app.manifest`, or use **Relaunch as Administrator** inside the window).
2. Select a detected Linux partition (or drag & drop a `.vhdx` / `.vhd` / `.img` file), choose a drive letter, and click **Mount**.
3. Optionally check **読み書き可能 (RW) でマウント** before mounting when Read-Write access is needed (disabled for Windows-shared NBD bridge disks).
4. Manage active drives from the bottom list or the system tray icon.

## Documentation

Detailed engineering documentation (written primarily in Japanese) lives under [`docs/`](docs/README.md):

- [Documentation Hub (`docs/README.md`)](docs/README.md)
- [Overview & Tool Comparison (`docs/overview.md`)](docs/overview.md)
- [Requirements Specification (`docs/requirements.md`)](docs/requirements.md)
- [System Architecture (`docs/architecture.md`)](docs/architecture.md)
- [User & CLI Guide (`docs/guides/usage.md`)](docs/guides/usage.md)
- [Troubleshooting (`docs/guides/troubleshooting.md`)](docs/guides/troubleshooting.md)
- [Architecture Decision Records (`docs/adr/README.md`)](docs/adr/README.md)
- [Testing & Verification (`docs/testing.md`)](docs/testing.md)
- [Roadmap (`docs/roadmap.md`)](docs/roadmap.md)

## Contributing & Security

- Build setup, coding workflow, and PR checklist: [CONTRIBUTING.md](CONTRIBUTING.md)
- Vulnerability reporting policy and privilege boundaries: [SECURITY.md](SECURITY.md)
- Release and change history: [CHANGELOG.md](CHANGELOG.md)

## License

Licensed under the **MIT License**. See [LICENSE](LICENSE) for full details.
