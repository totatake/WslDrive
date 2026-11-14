using System.Text.RegularExpressions;
using LinuxRoDrive.Core.Interop;
using LinuxRoDrive.Core.Models;

namespace LinuxRoDrive.Core.Wsl;

public sealed record MountOperationResult(bool Success, MountSession? Session, string Message);
public sealed record UnmountOperationResult(bool Success, string Message);

/// <summary>
/// Orchestrates WSL2 `wsl --mount` / `wsl --unmount`, active mount inspection via `/proc/mounts`,
/// and Windows drive letter mapping via <see cref="DriveMapper"/>.
/// </summary>
public sealed partial class WslMountService
{
    private readonly IWslCommandRunner _runner;
    private readonly Func<char, string, DriveMappingResult> _mapDrive;

    [GeneratedRegex(@"^linuxro_d(?<disk>\d+)(?:p\d+)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AutoMountNameDiskRegex();

    [GeneratedRegex(@"[\\/]+mnt[\\/]+wsl[\\/]+(?<name>[^\\/]+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UncMountNameRegex();

    public WslMountService() : this(new WslCliRunner())
    {
    }

    public WslMountService(IWslCommandRunner runner, Func<char, string, DriveMappingResult>? mapDrive = null)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _mapDrive = mapDrive ?? DriveMapper.MapDriveLetter;
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
        List<string> mountArgs = BuildMountArguments(request, forceSimpleRo: false);
        string usedOptions = mountArgs[^1];

        log?.Invoke($"Running: wsl.exe {string.Join(' ', mountArgs)}");
        WslCommandResult mountResult = await _runner.RunAsync(mountArgs, cancellationToken).ConfigureAwait(false);

        // If WSL2 reports that the disk/partition/mount-name is already mounted or attached:
        // 1) Check if /mnt/wsl/<mountName> is already actively mounted read-only in /proc/mounts; if so, reuse it.
        // 2) Otherwise, detach the stale disk state via `wsl --unmount <target>` and retry `wsl --mount` once.
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

        // Ensure the distro VM/9P server is awake and verify the mount directory exists.
        string wslMountPoint = $"/mnt/wsl/{mountName}";
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
            TargetWslDiskPath = targetDiskArg
        };

        log?.Invoke(mapResult.Message);
        return new MountOperationResult(true, session, $"Mounted {targetDiskArg} on {driveLetter}: ({usedOptions}).");
    }

    public async Task<UnmountOperationResult> UnmountAsync(
        char? driveLetter = null,
        int? diskIndex = null,
        string? vhdPath = null,
        string? distroName = null,
        Action<string>? log = null,
        CancellationToken cancellationToken = default,
        string? mountName = null)
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

        bool otherPartitionsRemainOnSameBaseDevice = false;
        MountSession? targetSession = null;
        if (!string.IsNullOrWhiteSpace(resolvedMountName) && !string.IsNullOrWhiteSpace(distro))
        {
            var active = await GetActiveMountsAsync(distro, cancellationToken).ConfigureAwait(false);
            targetSession = active.FirstOrDefault(m =>
                m.MountName.Equals(resolvedMountName, StringComparison.OrdinalIgnoreCase));

            if (targetSession is not null)
            {
                unmountTarget ??= targetSession.TargetWslDiskPath;
                string baseDev = GetBaseBlockDevice(targetSession.SourceDeviceInWsl);
                otherPartitionsRemainOnSameBaseDevice = !string.IsNullOrEmpty(baseDev) &&
                    active.Any(m =>
                        !m.MountName.Equals(resolvedMountName, StringComparison.OrdinalIgnoreCase) &&
                        GetBaseBlockDevice(m.SourceDeviceInWsl).Equals(baseDev, StringComparison.OrdinalIgnoreCase));
            }
        }

        if (!string.IsNullOrWhiteSpace(unmountTarget))
        {
            if (otherPartitionsRemainOnSameBaseDevice && targetSession is not null && !string.IsNullOrWhiteSpace(distro))
            {
                // Another partition on the same disk is still mounted, so only umount this partition inside WSL
                // without detaching the entire physical disk from the WSL2 VM.
                log?.Invoke($"Running: wsl.exe -d {distro} -u root -- umount {targetSession.WslMountPoint}");
                _ = await _runner.RunAsync(
                    ["-d", distro, "-u", "root", "--", "umount", targetSession.WslMountPoint],
                    cancellationToken).ConfigureAwait(false);

                return new UnmountOperationResult(
                    true,
                    $"Unmounted {resolvedMountName} and removed drive mapping; physical disk {unmountTarget} kept attached because another partition is still mounted.");
            }

            // Let `wsl.exe --unmount <unmountTarget>` cleanly unmount /mnt/wsl/<name> and detach the disk in wslservice.exe.
            // IMPORTANT: Do NOT run `umount /mnt/wsl/<name>` before `wsl --unmount`, as `wslservice.exe` requires the
            // mountpoint to still be mounted when `mini_init` detaches the disk.
            log?.Invoke($"Running: wsl.exe --unmount {unmountTarget}");
            var wslUnmount = await _runner.RunAsync(["--unmount", unmountTarget], cancellationToken).ConfigureAwait(false);
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
            var diskMatch = AutoMountNameDiskRegex().Match(mountName);
            if (diskMatch.Success && int.TryParse(diskMatch.Groups["disk"].Value, out int parsedDisk))
            {
                inferredTargetDisk = $@"\\.\PhysicalDrive{parsedDisk}";
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
                TargetWslDiskPath = inferredTargetDisk
            });
        }

        return sessions;
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
