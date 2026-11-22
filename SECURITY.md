# Security Policy

## Supported Versions

| Version | Supported |
| :--- | :--- |
| `v0.2.x` (`master`) | Yes |
| `v0.1.x` and earlier | No |

## Security Model & Privilege Boundaries

`WslDrive` interacts with low-level Windows and WSL2 primitives that require careful trust boundaries:

- **Elevation (`requireAdministrator`)**: Raw disk inspection (`\\.\PhysicalDriveX`) and `wsl --mount` require Administrator privileges.
- **Token Impersonation**: `DriveMapper` duplicates the desktop shell's (`explorer.exe`) non-elevated token solely for calling `DefineDosDeviceW` in the interactive user's `\DosDevices` namespace and immediately calls `RevertToSelf()`.
- **Safe-by-Default Mounts**: Mounts default to `ro,noload` (or `ro`) to prevent journal replay and accidental writes. Read-Write (`--rw` / GUI toggle) requires explicit user opt-in and is **blocked** on the Windows-shared NBD bridge path.
- **NBD Bridge Isolation**: `AlignedDiskStream` opens physical disks with `GENERIC_READ` only and restricts reads to the target partition byte range; `ReadOnlyNbdServer` rejects all write requests with `EROFS (30)`.
- **WSL Argument & Filesystem Validation**: `WslMountService` validates `FileSystemType` strictly (`[A-Za-z0-9][A-Za-z0-9._-]{0,31}`) and passes Linux `mount` arguments directly via `ProcessStartInfo.ArgumentList` without invoking `sh -c`, preventing shell command injection.

## Reporting a Vulnerability

Please **do not** report security vulnerabilities through public GitHub issues.

Instead, report them privately via:
- GitHub Security Advisories (`Security` tab -> `Report a vulnerability`), or
- Direct contact with the repository maintainer (`totatake`).

Please include:
1. Affected commit SHA / version and Windows 11 / WSL2 version (`wsl --version`).
2. Clear reproduction steps or proof-of-concept input (e.g., malformed partition table or superblock image).
3. Impact assessment (e.g., unintended disk write, privilege escalation, crash).

We aim to acknowledge reports within 72 hours and coordinate a fix before public disclosure.
