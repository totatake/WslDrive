using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using LinuxRoDrive.Core.Disk;
using LinuxRoDrive.Core.Interop;
using LinuxRoDrive.Core.Models;

namespace LinuxRoDrive.Core.Wsl;

public sealed record MountOperationResult(bool Success, MountSession? Session, string Message);
public sealed record UnmountOperationResult(bool Success, string Message);

/// <summary>
/// Orchestrates WSL2 `wsl --mount` / `wsl --unmount`, Read-Only NBD bridge mounts for Windows-shared disks,
/// active mount inspection via `/proc/mounts`, and Windows drive letter mapping via <see cref="DriveMapper"/>.
/// </summary>
public sealed partial class WslMountService
{
    private readonly IWslCommandRunner _runner;
    private readonly Func<char, string, DriveMappingResult> _mapDrive;
    private readonly INbdBridgeProvider _nbdBridgeProvider;
    private readonly ConcurrentDictionary<string, IAsyncDisposable> _activeNbdSessions =
        new(StringComparer.OrdinalIgnoreCase);

    [GeneratedRegex(@"^linuxro_d(?<disk>\d+)(?:p(?<part>\d+))?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AutoMountNameDiskRegex();

    [GeneratedRegex(@"[\\/]+mnt[\\/]+wsl[\\/]+(?<name>[^\\/]+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UncMountNameRegex();

    public WslMountService() : this(new WslCliRunner())
    {
    }

    public WslMountService(
        IWslCommandRunner runner,
        Func<char, string, DriveMappingResult>? mapDrive = null,
        INbdBridgeProvider? nbdBridgeProvider = null)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _mapDrive = mapDrive ?? DriveMapper.MapDriveLetter;
        _nbdBridgeProvider = nbdBridgeProvider ?? new InProcessNbdBridgeProvider();
    }

    public async Task<string?> GetDefaultDistroAsync(CancellationToken cancellationToken = default)
    {
        // First try `wsl --list --verbose` to identify the starred (*) default distribution.
        var verboseResult = await _runner.RunAsync(["--list", "--verbose"], cancellationToken).ConfigureAwait(false);
        if (verboseResult.ExitCode == 0)
        {
            string? starred = ParseDefaultDistroFromVerbose(verboseResult.StandardOutput);
            if (!string.IsNullOrWhiteSpace(starred))
            {
                return starred;
            }
        }

        // Fallback to `wsl --list --quiet`
        var quietResult = await _runner.RunAsync(["--list", "--quiet"], cancellationToken).ConfigureAwait(false);
        if (quietResult.ExitCode != 0)
        {
            return null;
        }

        return ParseDistroNamesFromQuiet(quietResult.StandardOutput).FirstOrDefault();
    }

    public async Task<List<string>> GetDistrosAsync(CancellationToken cancellationToken = default)
    {
        var quietResult = await _runner.RunAsync(["--list", "--quiet"], cancellationToken).ConfigureAwait(false);
        if (quietResult.ExitCode != 0)
        {
            return [];
        }
        return ParseDistroNamesFromQuiet(quietResult.StandardOutput);
    }

    public static string? ParseDefaultDistroFromVerbose(string output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;

        foreach (string rawLine in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            string line = rawLine.Trim();
            if (!line.StartsWith('*')) continue;

            string withoutStar = line[1..].TrimStart();
            // Columns are whitespace-separated: <NAME> <STATE> <VERSION>
            int splitIdx = withoutStar.IndexOfAny([' ', '\t']);
            string name = splitIdx > 0 ? withoutStar[..splitIdx] : withoutStar;
            if (!string.IsNullOrWhiteSpace(name) && !IsInternalDockerDistro(name))
            {
                return name;
            }
        }

        return null;
    }

    public static List<string> ParseDistroNamesFromQuiet(string output)
    {
        if (string.IsNullOrWhiteSpace(output)) return [];

        return output
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => !string.IsNullOrWhiteSpace(l) && !IsInternalDockerDistro(l))
            .ToList();
    }

    private static bool IsInternalDockerDistro(string name) =>
        name.Equals("docker-desktop", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("docker-desktop-data", StringComparison.OrdinalIgnoreCase);

    public static List<string> BuildMountArguments(MountRequest request, bool forceSimpleRo = false)
    {
        ArgumentNullException.ThrowIfNull(request);

        var args = new List<string>
        {
            "--mount",
            request.GetTargetWslDiskArgument()
        };

        if (!string.IsNullOrWhiteSpace(request.VhdPath))
        {
            args.Add("--vhd");
        }

        if (request.PartitionNumber is > 0)
        {
            args.Add("--partition");
            args.Add(request.PartitionNumber.Value.ToString());
        }

        string mountName = request.ResolveMountName();
        args.Add("--name");
        args.Add(mountName);

        if (!string.IsNullOrWhiteSpace(request.FileSystemType))
        {
            args.Add("--type");
            args.Add(request.FileSystemType);
        }

        bool useNoLoad = !forceSimpleRo &&
                         request.UseNoLoadJournal &&
                         (string.IsNullOrWhiteSpace(request.FileSystemType) ||
                          request.FileSystemType.Equals("ext4", StringComparison.OrdinalIgnoreCase) ||
                          request.FileSystemType.Equals("ext3", StringComparison.OrdinalIgnoreCase));

        args.Add("--options");
        args.Add(useNoLoad ? "ro,noload" : "ro");

        return args;
    }

    public async Task<MountOperationResult> MountAsync(
        MountRequest request,
        Action<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        char driveLetter = char.ToUpperInvariant(request.DriveLetter);
        if (driveLetter is < 'A' or > 'Z')
        {
            return new MountOperationResult(false, null, $"Invalid drive letter '{request.DriveLetter}'.");
        }

        string? distro = !string.IsNullOrWhiteSpace(request.DistroName)
            ? request.DistroName.Trim()
            : await GetDefaultDistroAsync(cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(distro))
        {
            return new MountOperationResult(
                false,
                null,
                "No usable WSL2 Linux distribution was found. Please install one first (e.g. `wsl --install Ubuntu`).");
        }

        string mountName = request.ResolveMountName();
        string targetDiskArg = request.GetTargetWslDiskArgument();

        if (request.DiskIndex.HasValue && request.ForceNbdBridge)
        {
            return await MountViaNbdBridgeAsync(request, distro, mountName, targetDiskArg, driveLetter, log, cancellationToken)
                .ConfigureAwait(false);
        }

        List<string> mountArgs = BuildMountArguments(request, forceSimpleRo: false);
        string usedOptions = mountArgs[^1];

        log?.Invoke($"Running: wsl.exe {string.Join(' ', mountArgs)}");
        WslCommandResult mountResult = await _runner.RunAsync(mountArgs, cancellationToken).ConfigureAwait(false);

        // If WSL2 reports that the disk/partition/mount-name is already mounted or attached:
        // 1) Check if /mnt/wsl/<mountName> is already actively mounted read-only in /proc/mounts; if so, reuse it.
        // 2) Check if a sibling partition on the same disk is actively mounted; if so, do NOT detach the disk
        //    and instead mount the requested partition directly inside WSL from the sibling's base block device.
        // 3) Otherwise, ensure any stale unmounted /mnt/wsl/linuxro_* directories are self-bound so mini_init's
        //    umount() succeeds, detach via `wsl --unmount <target>`, and retry `wsl --mount` once.
        if (mountResult.ExitCode != 0 && ContainsAlreadyMountedOrAttached(mountResult))
        {
            log?.Invoke($"wsl --mount reported existing attachment/mount ({CombineOutput(mountResult)}). Checking /proc/mounts...");

            var existingMounts = await GetActiveMountsAsync(distro, cancellationToken).ConfigureAwait(false);
            var existingSameMount = existingMounts.FirstOrDefault(m =>
                m.MountName.Equals(mountName, StringComparison.OrdinalIgnoreCase));

            if (existingSameMount is not null && existingSameMount.IsReadOnly)
            {
                log?.Invoke($"Found existing read-only mount at {existingSameMount.WslMountPoint}; reusing and mapping drive letter {driveLetter}:...");
                usedOptions = existingSameMount.MountOptions;
                mountResult = new WslCommandResult(0, string.Empty, string.Empty);
            }
            else
            {
                var activeSibling = existingMounts.FirstOrDefault(m =>
                    !string.IsNullOrEmpty(m.SourceDeviceInWsl) &&
                    !string.IsNullOrEmpty(m.TargetWslDiskPath) &&
                    m.TargetWslDiskPath.Equals(targetDiskArg, StringComparison.OrdinalIgnoreCase));

                if (activeSibling is not null)
                {
                    var siblingMount = await TryMountSiblingPartitionInWslAsync(
                        request,
                        distro,
                        mountName,
                        activeSibling.SourceDeviceInWsl,
                        usedOptions,
                        log,
                        cancellationToken).ConfigureAwait(false);

                    if (siblingMount.Success)
                    {
                        usedOptions = siblingMount.UsedOptions;
                        mountResult = new WslCommandResult(0, string.Empty, string.Empty);
                    }
                }
                else
                {
                    await EnsureStaleMountPointsBoundForDetachAsync(distro, cancellationToken).ConfigureAwait(false);
                    log?.Invoke($"Detaching stale WSL2 disk state: wsl.exe --unmount {targetDiskArg}");
                    var unmountRes = await _runner.RunAsync(["--unmount", targetDiskArg], cancellationToken).ConfigureAwait(false);
                    if (unmountRes.ExitCode != 0)
                    {
                        log?.Invoke($"wsl --unmount returned {unmountRes.ExitCode}: {CombineOutput(unmountRes)}");
                    }

                    log?.Invoke($"Retrying: wsl.exe {string.Join(' ', mountArgs)}");
                    mountResult = await _runner.RunAsync(mountArgs, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        // Retry with plain `ro` if `ro,noload` was rejected by a non-ext filesystem or WSL kernel version.
        if (mountResult.ExitCode != 0 &&
            usedOptions == "ro,noload" &&
            !ContainsSharingViolation(mountResult) &&
            !ContainsAlreadyMountedOrAttached(mountResult))
        {
            log?.Invoke($"Mount with `ro,noload` failed ({CombineOutput(mountResult)}); retrying with `ro`...");

            // If the failed `ro,noload` attempt left the physical disk attached in wslservice.exe,
            // detach it first so the `ro` retry does not fail with WSL_E_DISK_ALREADY_ATTACHED / ALREADY_MOUNTED.
            if (ContainsMountFailed(mountResult))
            {
                _ = await _runner.RunAsync(["--unmount", targetDiskArg], cancellationToken).ConfigureAwait(false);
            }

            mountArgs = BuildMountArguments(request, forceSimpleRo: true);
            usedOptions = "ro";
            log?.Invoke($"Running: wsl.exe {string.Join(' ', mountArgs)}");
            mountResult = await _runner.RunAsync(mountArgs, cancellationToken).ConfigureAwait(false);
        }

        if (mountResult.ExitCode != 0 &&
            request.DiskIndex.HasValue &&
            ContainsSharingViolation(mountResult))
        {
            log?.Invoke("Physical disk is shared with Windows (ERROR_SHARING_VIOLATION); switching to Read-Only WSL2 NBD bridge...");
            return await MountViaNbdBridgeAsync(request, distro, mountName, targetDiskArg, driveLetter, log, cancellationToken)
                .ConfigureAwait(false);
        }

        if (mountResult.ExitCode != 0)
        {
            string details = CombineOutput(mountResult);
            string hint = ContainsSharingViolation(mountResult)
                ? " (This physical disk appears to contain volumes currently mounted by Windows. Only separate physical disks or .vhdx files can be attached to WSL2.)"
                : ContainsAlreadyMountedOrAttached(mountResult)
                    ? " (WSL2 holds a stale disk attachment state. Run `wsl --shutdown` once and retry.)"
                    : string.Empty;
            return new MountOperationResult(false, null, $"wsl --mount failed (exit {mountResult.ExitCode}): {details}{hint}");
        }

        string wslMountPoint = $"/mnt/wsl/{mountName}";
        string uncPath = await ResolveAccessibleUncPathAsync(distro, mountName, wslMountPoint, log, cancellationToken)
            .ConfigureAwait(false);

        log?.Invoke($"Mapping {driveLetter}: -> {uncPath}");
        DriveMappingResult mapResult = _mapDrive(driveLetter, uncPath);
        if (!mapResult.Success)
        {
            // Rollback WSL mount so disk is not left attached if drive mapping failed.
            log?.Invoke($"Drive mapping failed ({mapResult.Message}). Rolling back WSL mount...");
            await _runner.RunAsync(["--unmount", targetDiskArg], cancellationToken).ConfigureAwait(false);
            return new MountOperationResult(false, null, mapResult.Message);
        }

        var session = new MountSession
        {
            MountName = mountName,
            SourceDeviceInWsl = string.Empty,
            WslMountPoint = wslMountPoint,
            UncPath = uncPath,
            MappedDriveLetter = $"{driveLetter}:",
            FileSystemType = request.FileSystemType ?? "auto",
            MountOptions = usedOptions,
            IsReadOnly = true,
            TargetWslDiskPath = targetDiskArg,
            DiskIndex = request.DiskIndex,
            PartitionNumber = request.PartitionNumber
        };

        log?.Invoke(mapResult.Message);
        return new MountOperationResult(true, session, $"Mounted {targetDiskArg} on {driveLetter}: ({usedOptions}).");
    }

    private async Task<MountOperationResult> MountViaNbdBridgeAsync(
        MountRequest request,
        string distro,
        string mountName,
        string targetDiskArg,
        char driveLetter,
        Action<string>? log,
        CancellationToken cancellationToken)
    {
        var existingMounts = await GetActiveMountsAsync(distro, cancellationToken).ConfigureAwait(false);
        var existingSameMount = existingMounts.FirstOrDefault(m =>
            m.MountName.Equals(mountName, StringComparison.OrdinalIgnoreCase));

        if (existingSameMount is not null && existingSameMount.IsReadOnly)
        {
            log?.Invoke($"Found existing read-only mount at {existingSameMount.WslMountPoint}; reusing and mapping drive letter {driveLetter}:...");
            string existingUnc = await ResolveAccessibleUncPathAsync(distro, mountName, existingSameMount.WslMountPoint, log, cancellationToken)
                .ConfigureAwait(false);
            DriveMappingResult existingMap = _mapDrive(driveLetter, existingUnc);
            if (!existingMap.Success)
            {
                return new MountOperationResult(false, null, existingMap.Message);
            }

            var reusedSession = new MountSession
            {
                MountName = mountName,
                SourceDeviceInWsl = existingSameMount.SourceDeviceInWsl,
                WslMountPoint = existingSameMount.WslMountPoint,
                UncPath = existingUnc,
                MappedDriveLetter = $"{driveLetter}:",
                FileSystemType = request.FileSystemType ?? existingSameMount.FileSystemType,
                MountOptions = existingSameMount.MountOptions,
                IsReadOnly = true,
                TargetWslDiskPath = targetDiskArg,
                DiskIndex = request.DiskIndex,
                PartitionNumber = request.PartitionNumber
            };
            return new MountOperationResult(true, reusedSession, $"Mapped {existingUnc} on {driveLetter}: ({existingSameMount.MountOptions}).");
        }

        long offsetBytes = request.PartitionOffsetBytes ?? -1;
        long sizeBytes = request.PartitionSizeBytes ?? -1;
        int sectorSize = request.SectorSize > 0 ? request.SectorSize : 512;
        string? fsType = request.FileSystemType;

        if (offsetBytes < 0 || sizeBytes <= 0)
        {
            PhysicalDiskEntry? disk = new DiskScanner().ScanPhysicalDisks()
                .FirstOrDefault(d => d.DiskIndex == request.DiskIndex!.Value);
            if (disk is null)
            {
                return new MountOperationResult(false, null, $"Could not find physical disk {request.DiskIndex}.");
            }

            PartitionEntry? part = request.PartitionNumber is > 0
                ? disk.Partitions.FirstOrDefault(p => p.PartitionNumber == request.PartitionNumber.Value)
                : disk.Partitions.FirstOrDefault(p => p.IsLinuxFileSystem);

            if (part is null)
            {
                return new MountOperationResult(
                    false,
                    null,
                    $"Could not resolve partition {request.PartitionNumber} geometry on Disk {disk.DiskIndex} for NBD bridge.");
            }

            offsetBytes = part.OffsetBytes;
            sizeBytes = part.SizeBytes;
            sectorSize = disk.SectorSize;
            fsType ??= part.FileSystemType.ToWslTypeName();
        }

        log?.Invoke($"Attaching partition {request.PartitionNumber} of Disk {request.DiskIndex} via Read-Only WSL2 NBD bridge...");
        NbdBridgeAttachment attach = await _nbdBridgeProvider.AttachPartitionAsync(
            request.DiskIndex!.Value,
            offsetBytes,
            sizeBytes,
            sectorSize,
            distro,
            log,
            cancellationToken).ConfigureAwait(false);

        if (!attach.Success)
        {
            return new MountOperationResult(false, null, attach.ErrorMessage);
        }

        string wslMountPoint = $"/mnt/wsl/{mountName}";
        bool useNoLoad = request.UseNoLoadJournal &&
                         (string.IsNullOrWhiteSpace(fsType) ||
                          fsType.Equals("ext4", StringComparison.OrdinalIgnoreCase) ||
                          fsType.Equals("ext3", StringComparison.OrdinalIgnoreCase));
        string usedOptions = useNoLoad ? "ro,noload" : "ro";
        string fsTypeArg = !string.IsNullOrWhiteSpace(fsType) ? $"-t {fsType} " : string.Empty;

        string mountCmd = $"mkdir -p '{wslMountPoint}' && mount {fsTypeArg}-o {usedOptions} '{attach.NbdDevicePath}' '{wslMountPoint}'";
        log?.Invoke($"Mounting NBD device inside WSL2: mount {fsTypeArg}-o {usedOptions} {attach.NbdDevicePath} {wslMountPoint}");
        WslCommandResult mountRes = await _runner.RunAsync(["-d", distro, "-u", "root", "--", "sh", "-c", mountCmd], cancellationToken)
            .ConfigureAwait(false);

        if (mountRes.ExitCode != 0 && usedOptions == "ro,noload")
        {
            log?.Invoke($"NBD mount with `ro,noload` failed ({CombineOutput(mountRes)}); retrying with `ro`...");
            usedOptions = "ro";
            string fallbackCmd = $"mkdir -p '{wslMountPoint}' && mount {fsTypeArg}-o ro '{attach.NbdDevicePath}' '{wslMountPoint}'";
            mountRes = await _runner.RunAsync(["-d", distro, "-u", "root", "--", "sh", "-c", fallbackCmd], cancellationToken)
                .ConfigureAwait(false);
        }

        if (mountRes.ExitCode != 0)
        {
            _ = await NbdDiskBridge.DisconnectInWslAsync(_runner, distro, attach.NbdDevicePath, CancellationToken.None).ConfigureAwait(false);
            if (attach.Session is not null)
            {
                await attach.Session.DisposeAsync().ConfigureAwait(false);
            }
            _ = await _runner.RunAsync(["-d", distro, "-u", "root", "--", "rmdir", wslMountPoint], CancellationToken.None).ConfigureAwait(false);
            return new MountOperationResult(false, null, $"WSL2 NBD mount failed (exit {mountRes.ExitCode}): {CombineOutput(mountRes)}");
        }

        string uncPath = await ResolveAccessibleUncPathAsync(distro, mountName, wslMountPoint, log, cancellationToken)
            .ConfigureAwait(false);

        log?.Invoke($"Mapping {driveLetter}: -> {uncPath}");
        DriveMappingResult mapResult = _mapDrive(driveLetter, uncPath);
        if (!mapResult.Success)
        {
            log?.Invoke($"Drive mapping failed ({mapResult.Message}). Rolling back NBD mount...");
            _ = await _runner.RunAsync(["-d", distro, "-u", "root", "--", "umount", wslMountPoint], CancellationToken.None).ConfigureAwait(false);
            _ = await NbdDiskBridge.DisconnectInWslAsync(_runner, distro, attach.NbdDevicePath, CancellationToken.None).ConfigureAwait(false);
            if (attach.Session is not null)
            {
                await attach.Session.DisposeAsync().ConfigureAwait(false);
            }
            return new MountOperationResult(false, null, mapResult.Message);
        }

        if (attach.Session is not null)
        {
            _activeNbdSessions[mountName] = attach.Session;
        }

        var session = new MountSession
        {
            MountName = mountName,
            SourceDeviceInWsl = attach.NbdDevicePath,
            WslMountPoint = wslMountPoint,
            UncPath = uncPath,
            MappedDriveLetter = $"{driveLetter}:",
            FileSystemType = fsType ?? "auto",
            MountOptions = usedOptions,
            IsReadOnly = true,
            TargetWslDiskPath = targetDiskArg,
            DiskIndex = request.DiskIndex,
            PartitionNumber = request.PartitionNumber
        };

        log?.Invoke(mapResult.Message);
        return new MountOperationResult(
            true,
            session,
            $"Mounted {targetDiskArg} (Partition {request.PartitionNumber}) via WSL2 NBD bridge ({attach.NbdDevicePath}) on {driveLetter}: ({usedOptions}).");
    }

    private async Task<string> ResolveAccessibleUncPathAsync(
        string distro,
        string mountName,
        string wslMountPoint,
        Action<string>? log,
        CancellationToken cancellationToken)
    {
        // Ensure the distro VM/9P server is awake and verify the mount directory exists.
        var checkDir = await _runner.RunAsync(["-d", distro, "--", "test", "-d", wslMountPoint], cancellationToken).ConfigureAwait(false);
        if (checkDir.ExitCode != 0)
        {
            log?.Invoke($"Warning: /mnt/wsl/{mountName} check returned exit code {checkDir.ExitCode}.");
        }

        string uncPath = $@"\\wsl.localhost\{distro}\mnt\wsl\{mountName}";
        if (OperatingSystem.IsWindows() && !Directory.Exists(uncPath))
        {
            string legacyUnc = $@"\\wsl$\{distro}\mnt\wsl\{mountName}";
            if (Directory.Exists(legacyUnc))
            {
                uncPath = legacyUnc;
            }
        }

        return uncPath;
    }

    private async Task<(bool Success, string UsedOptions)> TryMountSiblingPartitionInWslAsync(
        MountRequest request,
        string distro,
        string mountName,
        string siblingSourceDevice,
        string preferredOptions,
        Action<string>? log,
        CancellationToken cancellationToken)
    {
        string baseDev = GetBaseBlockDevice(siblingSourceDevice);
        string partDev = request.PartitionNumber is > 0
            ? GetPartitionDeviceFromBase(baseDev, request.PartitionNumber.Value)
            : baseDev;
        string fsTypeArg = !string.IsNullOrWhiteSpace(request.FileSystemType)
            ? $"-t {request.FileSystemType} "
            : string.Empty;
        string wslMountPoint = $"/mnt/wsl/{mountName}";

        log?.Invoke($"Disk already has active sibling mount ({siblingSourceDevice}); mounting {partDev} at {wslMountPoint} ({preferredOptions})...");
        string mountCmd = $"mkdir -p '{wslMountPoint}' && (mountpoint -q '{wslMountPoint}' || mount {fsTypeArg}-o {preferredOptions} '{partDev}' '{wslMountPoint}')";
        var mountRes = await _runner.RunAsync(["-d", distro, "-u", "root", "--", "sh", "-c", mountCmd], cancellationToken).ConfigureAwait(false);
        if (mountRes.ExitCode == 0)
        {
            return (true, preferredOptions);
        }

        if (preferredOptions == "ro,noload")
        {
            log?.Invoke($"Sibling mount with `ro,noload` failed ({CombineOutput(mountRes)}); retrying with `ro`...");
            string fallbackCmd = $"mkdir -p '{wslMountPoint}' && (mountpoint -q '{wslMountPoint}' || mount {fsTypeArg}-o ro '{partDev}' '{wslMountPoint}')";
            var fallbackRes = await _runner.RunAsync(["-d", distro, "-u", "root", "--", "sh", "-c", fallbackCmd], cancellationToken).ConfigureAwait(false);
            if (fallbackRes.ExitCode == 0)
            {
                return (true, "ro");
            }
        }

        log?.Invoke($"Sibling partition mount of {partDev} failed: {CombineOutput(mountRes)}");
        return (false, preferredOptions);
    }

    private async Task EnsureStaleMountPointsBoundForDetachAsync(string distro, CancellationToken cancellationToken)
    {
        // When a partition under /mnt/wsl/linuxro_* was previously unmounted via `umount` inside WSL while another
        // partition on the same disk remained active, wslservice.exe still tracks it in m_attachedDisks[disk].Mounts.
        // Calling `wsl --unmount` later asks mini_init to umount all tracked paths; binding any unmounted directory
        // onto itself lets mini_init's umount() + rmdir() succeed cleanly.
        const string bindScript =
            "for d in /mnt/wsl/linuxro_*; do " +
            "  [ -d \"$d\" ] || continue; " +
            "  mountpoint -q \"$d\" || mount --bind \"$d\" \"$d\" 2>/dev/null || true; " +
            "done";

        _ = await _runner.RunAsync(["-d", distro, "-u", "root", "--", "sh", "-c", bindScript], cancellationToken).ConfigureAwait(false);
    }

    public async Task<UnmountOperationResult> UnmountAsync(
        char? driveLetter = null,
        int? diskIndex = null,
        string? vhdPath = null,
        string? distroName = null,
        Action<string>? log = null,
        CancellationToken cancellationToken = default,
        string? mountName = null,
        bool forceWholeDisk = false)
    {
        string? inferredDiskTarget = null;
        string? resolvedMountName = !string.IsNullOrWhiteSpace(mountName) ? mountName.Trim() : null;

        if (driveLetter.HasValue)
        {
            char normalized = char.ToUpperInvariant(driveLetter.Value);
            string local = $"{normalized}:";
            var mapped = DriveMapper.GetMappedNetworkDrives();
            if (mapped.TryGetValue(local, out string? unc))
            {
                var nameMatch = UncMountNameRegex().Match(unc.TrimEnd('\\', '/'));
                if (nameMatch.Success)
                {
                    resolvedMountName ??= nameMatch.Groups["name"].Value;
                }
            }

            log?.Invoke($"Unmapping drive {local}...");
            var unmapResult = DriveMapper.UnmapDriveLetter(normalized);
            log?.Invoke(unmapResult.Message);
        }

        if (!string.IsNullOrWhiteSpace(resolvedMountName))
        {
            var diskMatch = AutoMountNameDiskRegex().Match(resolvedMountName);
            if (diskMatch.Success && int.TryParse(diskMatch.Groups["disk"].Value, out int parsedDisk))
            {
                inferredDiskTarget = $@"\\.\PhysicalDrive{parsedDisk}";
            }
        }

        string? unmountTarget = !string.IsNullOrWhiteSpace(vhdPath)
            ? vhdPath
            : diskIndex.HasValue
                ? $@"\\.\PhysicalDrive{diskIndex.Value}"
                : inferredDiskTarget;

        string? distro = !string.IsNullOrWhiteSpace(distroName)
            ? distroName.Trim()
            : await GetDefaultDistroAsync(cancellationToken).ConfigureAwait(false);

        bool wholeDiskRequested = forceWholeDisk ||
                                  (!driveLetter.HasValue && string.IsNullOrWhiteSpace(resolvedMountName) && !string.IsNullOrWhiteSpace(unmountTarget));
        bool otherPartitionsRemainOnSameBaseDevice = false;
        MountSession? targetSession = null;

        if (wholeDiskRequested && !string.IsNullOrWhiteSpace(unmountTarget))
        {
            if (!string.IsNullOrWhiteSpace(distro))
            {
                var active = await GetActiveMountsAsync(distro, cancellationToken).ConfigureAwait(false);
                var nbdToDisconnect = new List<MountSession>();
                bool hasNonNbdOnTarget = false;

                foreach (var session in active)
                {
                    if (!string.IsNullOrEmpty(session.TargetWslDiskPath) &&
                        session.TargetWslDiskPath.Equals(unmountTarget, StringComparison.OrdinalIgnoreCase))
                    {
                        if (!string.IsNullOrWhiteSpace(session.MappedDriveLetter))
                        {
                            char mappedLetter = char.ToUpperInvariant(session.MappedDriveLetter[0]);
                            log?.Invoke($"Unmapping drive {mappedLetter}:...");
                            var unmapRes = DriveMapper.UnmapDriveLetter(mappedLetter);
                            log?.Invoke(unmapRes.Message);
                        }

                        if (IsNbdDevice(session.SourceDeviceInWsl))
                        {
                            nbdToDisconnect.Add(session);
                        }
                        else
                        {
                            hasNonNbdOnTarget = true;
                        }
                    }
                }

                foreach (var nbdSess in nbdToDisconnect)
                {
                    await UnmountSingleNbdSessionAsync(distro, nbdSess, log, cancellationToken).ConfigureAwait(false);
                }

                if (nbdToDisconnect.Count > 0 && !hasNonNbdOnTarget)
                {
                    return new UnmountOperationResult(
                        true,
                        $"Successfully unmounted {nbdToDisconnect.Count} NBD mount(s) on {unmountTarget}.");
                }
            }
        }
        else if (!string.IsNullOrWhiteSpace(resolvedMountName) && !string.IsNullOrWhiteSpace(distro))
        {
            var active = await GetActiveMountsAsync(distro, cancellationToken).ConfigureAwait(false);
            targetSession = active.FirstOrDefault(m =>
                m.MountName.Equals(resolvedMountName, StringComparison.OrdinalIgnoreCase));

            if (targetSession is not null)
            {
                unmountTarget ??= targetSession.TargetWslDiskPath;
                if (IsNbdDevice(targetSession.SourceDeviceInWsl))
                {
                    await UnmountSingleNbdSessionAsync(distro, targetSession, log, cancellationToken).ConfigureAwait(false);
                    return new UnmountOperationResult(
                        true,
                        $"Successfully unmounted {targetSession.MountName} and disconnected NBD device {targetSession.SourceDeviceInWsl}.");
                }
            }

            string baseDev = targetSession is not null ? GetBaseBlockDevice(targetSession.SourceDeviceInWsl) : string.Empty;
            otherPartitionsRemainOnSameBaseDevice = active.Any(m =>
                !m.MountName.Equals(resolvedMountName, StringComparison.OrdinalIgnoreCase) &&
                !IsNbdDevice(m.SourceDeviceInWsl) &&
                ((!string.IsNullOrEmpty(baseDev) && GetBaseBlockDevice(m.SourceDeviceInWsl).Equals(baseDev, StringComparison.OrdinalIgnoreCase)) ||
                 (!string.IsNullOrEmpty(unmountTarget) && string.Equals(m.TargetWslDiskPath, unmountTarget, StringComparison.OrdinalIgnoreCase))));
        }

        if (!string.IsNullOrWhiteSpace(unmountTarget))
        {
            if (otherPartitionsRemainOnSameBaseDevice && !string.IsNullOrWhiteSpace(distro))
            {
                // Another partition on the same disk is still mounted, so only umount this partition inside WSL
                // without detaching the entire physical disk from the WSL2 VM.
                if (targetSession is not null)
                {
                    log?.Invoke($"Running: wsl.exe -d {distro} -u root -- umount {targetSession.WslMountPoint}");
                    _ = await _runner.RunAsync(
                        ["-d", distro, "-u", "root", "--", "umount", targetSession.WslMountPoint],
                        cancellationToken).ConfigureAwait(false);
                }

                return new UnmountOperationResult(
                    true,
                    $"Unmounted {resolvedMountName} and removed drive mapping; physical disk {unmountTarget} kept attached because another partition is still mounted.");
            }

            // Let `wsl.exe --unmount <unmountTarget>` cleanly unmount /mnt/wsl/<name> and detach the disk in wslservice.exe.
            // IMPORTANT: Do NOT run `umount /mnt/wsl/<name>` before `wsl --unmount`, as `wslservice.exe` requires the
            // mountpoint to still be mounted when `mini_init` detaches the disk.
            log?.Invoke($"Running: wsl.exe --unmount {unmountTarget}");
            var wslUnmount = await _runner.RunAsync(["--unmount", unmountTarget], cancellationToken).ConfigureAwait(false);
            if (wslUnmount.ExitCode != 0 && !string.IsNullOrWhiteSpace(distro))
            {
                await EnsureStaleMountPointsBoundForDetachAsync(distro, cancellationToken).ConfigureAwait(false);
                wslUnmount = await _runner.RunAsync(["--unmount", unmountTarget], cancellationToken).ConfigureAwait(false);
            }

            if (wslUnmount.ExitCode != 0)
            {
                string err = CombineOutput(wslUnmount);
                return new UnmountOperationResult(false, $"Drive unmapped, but `wsl --unmount {unmountTarget}` failed: {err}");
            }

            return new UnmountOperationResult(true, $"Successfully unmounted {unmountTarget}.");
        }

        // Fallback if we only know the mount point inside WSL but not the host disk/VHD path
        if (targetSession is not null && !string.IsNullOrWhiteSpace(distro))
        {
            log?.Invoke($"Running: wsl.exe -d {distro} -u root -- umount {targetSession.WslMountPoint}");
            _ = await _runner.RunAsync(
                ["-d", distro, "-u", "root", "--", "umount", targetSession.WslMountPoint],
                cancellationToken).ConfigureAwait(false);
        }

        return driveLetter.HasValue
            ? new UnmountOperationResult(true, $"Unmapped {char.ToUpperInvariant(driveLetter.Value)}:. Specify --disk or --vhd if the physical disk is still attached in WSL2.")
            : new UnmountOperationResult(false, "Specify a drive letter (--letter), disk index (--disk), or VHD path (--vhd) to unmount.");
    }

    private static bool IsNbdDevice(string? devicePath) =>
        !string.IsNullOrWhiteSpace(devicePath) &&
        devicePath.StartsWith("/dev/nbd", StringComparison.OrdinalIgnoreCase);

    private async Task UnmountSingleNbdSessionAsync(
        string distro,
        MountSession nbdSession,
        Action<string>? log,
        CancellationToken cancellationToken)
    {
        log?.Invoke($"Unmounting NBD mount point: wsl.exe -d {distro} -u root -- umount {nbdSession.WslMountPoint}");
        _ = await _runner.RunAsync(
            ["-d", distro, "-u", "root", "--", "umount", nbdSession.WslMountPoint],
            cancellationToken).ConfigureAwait(false);
        _ = await _runner.RunAsync(
            ["-d", distro, "-u", "root", "--", "rmdir", nbdSession.WslMountPoint],
            cancellationToken).ConfigureAwait(false);

        log?.Invoke($"Disconnecting WSL2 NBD device {nbdSession.SourceDeviceInWsl}...");
        _ = await NbdDiskBridge.DisconnectInWslAsync(_runner, distro, nbdSession.SourceDeviceInWsl, cancellationToken)
            .ConfigureAwait(false);

        if (_activeNbdSessions.TryRemove(nbdSession.MountName, out IAsyncDisposable? activeNbd))
        {
            await activeNbd.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async Task<List<MountSession>> GetActiveMountsAsync(
        string? distroName = null,
        CancellationToken cancellationToken = default)
    {
        string? distro = !string.IsNullOrWhiteSpace(distroName)
            ? distroName.Trim()
            : await GetDefaultDistroAsync(cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(distro))
        {
            return [];
        }

        var procMounts = await _runner.RunAsync(["-d", distro, "--", "cat", "/proc/mounts"], cancellationToken).ConfigureAwait(false);
        if (procMounts.ExitCode != 0)
        {
            return [];
        }

        Dictionary<string, string> mappedDrives = DriveMapper.GetMappedNetworkDrives();
        return ParseProcMounts(procMounts.StandardOutput, distro, mappedDrives);
    }

    public static List<MountSession> ParseProcMounts(
        string procMountsContent,
        string distroName,
        IReadOnlyDictionary<string, string> mappedDrives)
    {
        var sessions = new List<MountSession>();
        if (string.IsNullOrWhiteSpace(procMountsContent))
        {
            return sessions;
        }

        foreach (string rawLine in procMountsContent.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = rawLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 4) continue;

            string source = parts[0];
            string mountPoint = UnescapeMountPath(parts[1]);
            string fsType = parts[2];
            string options = parts[3];

            if (!source.StartsWith("/dev/", StringComparison.Ordinal) ||
                !mountPoint.StartsWith("/mnt/wsl/", StringComparison.Ordinal))
            {
                continue;
            }

            string mountName = mountPoint["/mnt/wsl/".Length..];
            if (string.IsNullOrWhiteSpace(mountName) ||
                mountName.Contains('/') ||
                mountName.Equals("wslg", StringComparison.OrdinalIgnoreCase) ||
                mountName.Equals("resolv.conf", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string expectedUnc = $@"\\wsl.localhost\{distroName}\mnt\wsl\{mountName}";
            string legacyUnc = $@"\\wsl$\{distroName}\mnt\wsl\{mountName}";

            string? matchedDrive = null;
            string resolvedUnc = expectedUnc;
            foreach (var kv in mappedDrives)
            {
                string candidateUnc = kv.Value.TrimEnd('\\', '/');
                if (candidateUnc.Equals(expectedUnc, StringComparison.OrdinalIgnoreCase) ||
                    candidateUnc.Equals(legacyUnc, StringComparison.OrdinalIgnoreCase) ||
                    candidateUnc.EndsWith($@"\mnt\wsl\{mountName}", StringComparison.OrdinalIgnoreCase))
                {
                    matchedDrive = kv.Key;
                    resolvedUnc = candidateUnc;
                    break;
                }
            }

            string? inferredTargetDisk = null;
            int? parsedDiskIndex = null;
            int? parsedPartitionNumber = null;
            var diskMatch = AutoMountNameDiskRegex().Match(mountName);
            if (diskMatch.Success && int.TryParse(diskMatch.Groups["disk"].Value, out int parsedDisk))
            {
                parsedDiskIndex = parsedDisk;
                inferredTargetDisk = $@"\\.\PhysicalDrive{parsedDisk}";
                if (diskMatch.Groups["part"].Success && int.TryParse(diskMatch.Groups["part"].Value, out int parsedPart))
                {
                    parsedPartitionNumber = parsedPart;
                }
            }

            bool isRo = options.Split(',').Any(o => o.Equals("ro", StringComparison.OrdinalIgnoreCase));
            sessions.Add(new MountSession
            {
                MountName = mountName,
                SourceDeviceInWsl = source,
                WslMountPoint = mountPoint,
                UncPath = resolvedUnc,
                MappedDriveLetter = matchedDrive,
                FileSystemType = fsType,
                MountOptions = options,
                IsReadOnly = isRo,
                TargetWslDiskPath = inferredTargetDisk,
                DiskIndex = parsedDiskIndex,
                PartitionNumber = parsedPartitionNumber
            });
        }

        return sessions;
    }

    public static string GetPartitionDeviceFromBase(string baseDevice, int partitionNumber)
    {
        if (string.IsNullOrWhiteSpace(baseDevice) || partitionNumber <= 0)
        {
            return baseDevice;
        }
        return char.IsDigit(baseDevice[^1])
            ? $"{baseDevice}p{partitionNumber}"
            : $"{baseDevice}{partitionNumber}";
    }

    private static string GetBaseBlockDevice(string devicePath)
    {
        if (string.IsNullOrWhiteSpace(devicePath)) return string.Empty;
        // e.g. /dev/sdd2 -> /dev/sdd, /dev/nvme0n1p2 -> /dev/nvme0n1
        if (devicePath.Contains("nvme", StringComparison.OrdinalIgnoreCase) ||
            devicePath.Contains("mmcblk", StringComparison.OrdinalIgnoreCase))
        {
            int pIdx = devicePath.LastIndexOf('p');
            if (pIdx > 0 && devicePath[(pIdx + 1)..].All(char.IsDigit))
            {
                return devicePath[..pIdx];
            }
            return devicePath;
        }
        return devicePath.TrimEnd("0123456789".ToCharArray());
    }

    private static string UnescapeMountPath(string escaped) =>
        escaped.Replace(@"\040", " ").Replace(@"\011", "\t").Replace(@"\012", "\n").Replace(@"\134", @"\");

    private static bool ContainsSharingViolation(WslCommandResult result)
    {
        string text = $"{result.StandardOutput}\n{result.StandardError}";
        return text.Contains("SHARING_VIOLATION", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("0x80070020", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsAlreadyMountedOrAttached(WslCommandResult result)
    {
        string text = $"{result.StandardOutput}\n{result.StandardError}";
        return text.Contains("WSL_E_DISK_ALREADY_MOUNTED", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("0x80040319", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("WSL_E_DISK_ALREADY_ATTACHED", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("0x80040318", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("WSL_E_VM_MODE_MOUNT_NAME_ALREADY_EXISTS", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("0x80040314", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("WSL_E_USER_VHD_ALREADY_ATTACHED", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("0x80040312", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsMountFailed(WslCommandResult result)
    {
        string text = $"{result.StandardOutput}\n{result.StandardError}";
        return text.Contains("WSL_E_DISK_MOUNT_FAILED", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("0x8004031a", StringComparison.OrdinalIgnoreCase);
    }

    private static string CombineOutput(WslCommandResult result)
    {
        var parts = new[] { result.StandardError.Trim(), result.StandardOutput.Trim() }
            .Where(s => !string.IsNullOrWhiteSpace(s));
        string joined = string.Join(" | ", parts);
        return string.IsNullOrWhiteSpace(joined) ? "Unknown WSL error." : joined;
    }
}
