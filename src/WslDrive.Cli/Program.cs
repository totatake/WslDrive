using WslDrive.Core.Disk;
using WslDrive.Core.Interop;
using WslDrive.Core.Models;
using WslDrive.Core.Wsl;

namespace WslDrive.Cli;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "__nbd-daemon")
        {
            return await HandleNbdDaemonAsync(args.Skip(1).ToArray()).ConfigureAwait(false);
        }

        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            PrintUsage();
            return 0;
        }

        if (args[0] is "-v" or "--version" or "version")
        {
            string ver = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";
            Console.WriteLine($"WslDrive.Cli v{ver}");
            return 0;
        }

        string command = args[0].ToLowerInvariant();
        string[] rest = args.Skip(1).ToArray();

        try
        {
            return command switch
            {
                "list" => HandleList(rest),
                "mount" => await HandleMountAsync(rest).ConfigureAwait(false),
                "unmount" or "umount" => await HandleUnmountAsync(rest).ConfigureAwait(false),
                "status" => await HandleStatusAsync(rest).ConfigureAwait(false),
                _ => UnknownCommand(command)
            };
        }
        catch (ArgumentException ex)
        {
            WriteError(ex.Message);
            return 2;
        }
        catch (Exception ex)
        {
            WriteError(ex.Message);
            return 1;
        }
    }

    private static int HandleList(string[] args)
    {
        bool showAll = false;
        string? imagePath = null;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--all":
                case "-a":
                    showAll = true;
                    break;
                case "--image":
                case "-i":
                    imagePath = RequireArgValue(args, ref i, "--image");
                    break;
                case "--help":
                case "-h":
                    PrintUsage();
                    return 0;
                default:
                    throw new ArgumentException($"Unknown option '{args[i]}' for list.");
            }
        }

        var scanner = new DiskScanner();
        if (!string.IsNullOrWhiteSpace(imagePath))
        {
            PhysicalDiskEntry imgEntry = scanner.InspectImageFile(imagePath);
            PrintDiskList([imgEntry], showAll: true);
            return 0;
        }

        WarnIfNotAdmin();
        List<PhysicalDiskEntry> disks = scanner.ScanPhysicalDisks();
        PrintDiskList(disks, showAll);
        return 0;
    }

    private static async Task<int> HandleMountAsync(string[] args)
    {
        int? diskIndex = null;
        string? vhdPath = null;
        int? partitionNumber = null;
        char? driveLetter = null;
        string? mountName = null;
        string? fsType = null;
        bool useNoLoad = true;
        bool readWrite = false;
        string? distroName = null;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--disk":
                case "-d":
                    string diskStr = RequireArgValue(args, ref i, "--disk");
                    if (!int.TryParse(diskStr, out int parsedDisk) || parsedDisk < 0)
                    {
                        throw new ArgumentException($"Invalid disk index '{diskStr}'.");
                    }
                    diskIndex = parsedDisk;
                    break;
                case "--vhd":
                    vhdPath = RequireArgValue(args, ref i, "--vhd");
                    break;
                case "--partition":
                case "-p":
                    string partStr = RequireArgValue(args, ref i, "--partition");
                    if (!int.TryParse(partStr, out int parsedPart) || parsedPart < 0)
                    {
                        throw new ArgumentException($"Invalid partition number '{partStr}'.");
                    }
                    partitionNumber = parsedPart;
                    break;
                case "--letter":
                case "-l":
                    driveLetter = DriveMapper.NormalizeDriveLetter(RequireArgValue(args, ref i, "--letter"));
                    break;
                case "--name":
                case "-n":
                    mountName = RequireArgValue(args, ref i, "--name");
                    break;
                case "--fs":
                case "--type":
                case "-t":
                    fsType = RequireArgValue(args, ref i, "--fs");
                    break;
                case "--rw":
                case "--read-write":
                    readWrite = true;
                    break;
                case "--ro":
                case "--read-only":
                    readWrite = false;
                    break;
                case "--no-noload":
                    useNoLoad = false;
                    break;
                case "--distro":
                    distroName = RequireArgValue(args, ref i, "--distro");
                    break;
                case "--help":
                case "-h":
                    PrintUsage();
                    return 0;
                default:
                    throw new ArgumentException($"Unknown option '{args[i]}' for mount.");
            }
        }

        if (diskIndex is null && string.IsNullOrWhiteSpace(vhdPath))
        {
            throw new ArgumentException("Either --disk <index> or --vhd <path> must be specified.");
        }
        if (diskIndex is not null && !string.IsNullOrWhiteSpace(vhdPath))
        {
            throw new ArgumentException("Specify either --disk or --vhd, not both.");
        }
        if (driveLetter is null)
        {
            var free = DriveMapper.GetAvailableDriveLetters();
            if (free.Count == 0)
            {
                throw new InvalidOperationException("No free Windows drive letters are available.");
            }
            driveLetter = free[0];
            WriteInfo($"No --letter specified; selected available drive letter {driveLetter}:");
        }

        WarnIfNotAdmin();

        long? partitionOffsetBytes = null;
        long? partitionSizeBytes = null;
        int sectorSize = 512;
        bool forceNbdBridge = false;

        // Inspect physical disk metadata so NBD bridge geometry and auto-selected FS type/partition are available.
        if (diskIndex.HasValue)
        {
            var scanner = new DiskScanner();
            var disk = scanner.ScanPhysicalDisks().FirstOrDefault(d => d.DiskIndex == diskIndex.Value);
            if (disk is not null)
            {
                sectorSize = disk.SectorSize;
                forceNbdBridge = disk.HasWindowsMountedVolumes;
                if (disk.HasWindowsMountedVolumes && !readWrite)
                {
                    WriteInfo($"Disk {disk.DiskIndex} ({disk.Model}) contains active Windows drive(s) [{disk.WindowsDriveLetters}]. Will use Read-Only WSL2 NBD bridge.");
                }

                var part = partitionNumber.HasValue
                    ? disk.Partitions.FirstOrDefault(p => p.PartitionNumber == partitionNumber.Value)
                    : disk.Partitions.FirstOrDefault(p => p.IsLinuxFileSystem);

                if (part is not null)
                {
                    partitionOffsetBytes = part.OffsetBytes;
                    partitionSizeBytes = part.SizeBytes;
                    if (string.IsNullOrWhiteSpace(fsType))
                    {
                        fsType = part.FileSystemType.ToWslTypeName();
                    }
                    if (!partitionNumber.HasValue && part.PartitionNumber > 0)
                    {
                        partitionNumber = part.PartitionNumber;
                        WriteInfo($"Auto-selected Linux partition {partitionNumber} ({part.FileSystemType}, {part.FormattedSize}).");
                    }
                }
            }
        }

        var request = new MountRequest
        {
            DiskIndex = diskIndex,
            VhdPath = vhdPath,
            PartitionNumber = partitionNumber,
            DriveLetter = driveLetter.Value,
            MountName = mountName,
            FileSystemType = fsType,
            ReadWrite = readWrite,
            UseNoLoadJournal = useNoLoad,
            DistroName = distroName,
            PartitionOffsetBytes = partitionOffsetBytes,
            PartitionSizeBytes = partitionSizeBytes,
            SectorSize = sectorSize,
            ForceNbdBridge = forceNbdBridge
        };

        var service = new WslMountService(
            new WslCliRunner(),
            DriveMapper.MapDriveLetter,
            new DetachedProcessNbdBridgeProvider());
        MountOperationResult result = await service.MountAsync(request, WriteInfo).ConfigureAwait(false);
        if (!result.Success || result.Session is null)
        {
            WriteError(result.Message);
            return 1;
        }

        WriteSuccess(result.Message);
        Console.WriteLine($"  Drive Letter : {result.Session.MappedDriveLetter}");
        Console.WriteLine($"  UNC Path     : {result.Session.UncPath}");
        Console.WriteLine($"  WSL Mount    : {result.Session.WslMountPoint}");
        Console.WriteLine($"  Options      : {result.Session.MountOptions}");
        return 0;
    }

    private static async Task<int> HandleUnmountAsync(string[] args)
    {
        char? driveLetter = null;
        int? diskIndex = null;
        string? vhdPath = null;
        string? distroName = null;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--letter":
                case "-l":
                    driveLetter = DriveMapper.NormalizeDriveLetter(RequireArgValue(args, ref i, "--letter"));
                    break;
                case "--disk":
                case "-d":
                    string diskStr = RequireArgValue(args, ref i, "--disk");
                    if (!int.TryParse(diskStr, out int parsedDisk) || parsedDisk < 0)
                    {
                        throw new ArgumentException($"Invalid disk index '{diskStr}'.");
                    }
                    diskIndex = parsedDisk;
                    break;
                case "--vhd":
                    vhdPath = RequireArgValue(args, ref i, "--vhd");
                    break;
                case "--distro":
                    distroName = RequireArgValue(args, ref i, "--distro");
                    break;
                case "--help":
                case "-h":
                    PrintUsage();
                    return 0;
                default:
                    // Allow positional drive letter such as `unmount L:`
                    if (!args[i].StartsWith('-') && driveLetter is null)
                    {
                        driveLetter = DriveMapper.NormalizeDriveLetter(args[i]);
                    }
                    else
                    {
                        throw new ArgumentException($"Unknown option '{args[i]}' for unmount.");
                    }
                    break;
            }
        }

        if (driveLetter is null && diskIndex is null && string.IsNullOrWhiteSpace(vhdPath))
        {
            throw new ArgumentException("Specify --letter <L:>, --disk <index>, or --vhd <path>.");
        }

        WarnIfNotAdmin();
        var service = new WslMountService();
        UnmountOperationResult result = await service.UnmountAsync(
            driveLetter,
            diskIndex,
            vhdPath,
            distroName,
            WriteInfo).ConfigureAwait(false);

        if (!result.Success)
        {
            WriteError(result.Message);
            return 1;
        }

        WriteSuccess(result.Message);
        return 0;
    }

    private static async Task<int> HandleStatusAsync(string[] args)
    {
        string? distroName = null;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--distro":
                    distroName = RequireArgValue(args, ref i, "--distro");
                    break;
                case "--help":
                case "-h":
                    PrintUsage();
                    return 0;
                default:
                    throw new ArgumentException($"Unknown option '{args[i]}' for status.");
            }
        }

        var service = new WslMountService();
        string? effectiveDistro = distroName ?? await service.GetDefaultDistroAsync().ConfigureAwait(false);
        Console.WriteLine($"WSL Default Distro : {effectiveDistro ?? "(none)"}");

        List<MountSession> sessions = await service.GetActiveMountsAsync(effectiveDistro).ConfigureAwait(false);
        if (sessions.Count == 0)
        {
            Console.WriteLine("No active WSL block device mounts found under /mnt/wsl/.");
            return 0;
        }

        Console.WriteLine();
        Console.WriteLine($"{"Drive",-7} {"MountName",-20} {"FS",-8} {"Device",-12} {"Mode",-6} {"UNC Path"}");
        Console.WriteLine(new string('-', 90));
        foreach (var s in sessions)
        {
            string drive = s.MappedDriveLetter ?? "-";
            string mode = s.IsReadOnly ? "RO" : "RW";
            Console.WriteLine($"{drive,-7} {s.MountName,-20} {s.FileSystemType,-8} {s.SourceDeviceInWsl,-12} {mode,-6} {s.UncPath}");
        }
        return 0;
    }

    private static void PrintDiskList(List<PhysicalDiskEntry> disks, bool showAll)
    {
        if (disks.Count == 0)
        {
            Console.WriteLine("No disks found.");
            return;
        }

        foreach (var disk in disks)
        {
            var parts = showAll ? disk.Partitions : disk.Partitions.Where(p => p.IsLinuxFileSystem).ToList();
            if (!showAll && parts.Count == 0 && string.IsNullOrEmpty(disk.ScanError))
            {
                continue;
            }

            Console.ForegroundColor = ConsoleColor.Cyan;
            string diskIdLabel = disk.DiskIndex >= 0 ? $"Disk {disk.DiskIndex}" : "Image";
            Console.WriteLine($"[{diskIdLabel}] {disk.DevicePath} - {disk.Model} ({disk.FormattedSize}, {disk.PartitionStyle}, Sector={disk.SectorSize}B)");
            Console.ResetColor();

            if (disk.HasWindowsMountedVolumes)
            {
                WriteInfo($"  Windows mounted volumes: {disk.WindowsDriveLetters} (mounted via NBD bridge [RO only] instead of wsl --mount)");
            }
            if (!string.IsNullOrWhiteSpace(disk.ScanError))
            {
                WriteWarning($"  Scan note: {disk.ScanError}");
            }

            if (parts.Count == 0)
            {
                Console.WriteLine("  (No matching partitions)");
                Console.WriteLine();
                continue;
            }

            Console.WriteLine($"  {"Part",-5} {"FS",-10} {"Size",-11} {"Label",-18} {"UUID",-38} {"Type"}");
            Console.WriteLine("  " + new string('-', 102));
            foreach (var p in parts)
            {
                string partStr = p.PartitionNumber == 0 ? "RAW" : p.PartitionNumber.ToString();
                string label = Truncate(string.IsNullOrEmpty(p.VolumeLabel) ? p.PartitionName : p.VolumeLabel, 17);
                string uuid = string.IsNullOrEmpty(p.Uuid) ? "-" : p.Uuid;
                Console.WriteLine($"  {partStr,-5} {p.FileSystemType,-10} {p.FormattedSize,-11} {label,-18} {uuid,-38} {p.PartitionTypeDescription}");
            }
            Console.WriteLine();
        }
    }

    private static string RequireArgValue(string[] args, ref int index, string flag)
    {
        if (index + 1 >= args.Length)
        {
            throw new ArgumentException($"Missing value after {flag}.");
        }
        return args[++index];
    }

    private static string Truncate(string value, int maxLen) =>
        value.Length <= maxLen ? value : value[..(maxLen - 1)] + "…";

    private static void WarnIfNotAdmin()
    {
        if (OperatingSystem.IsWindows() && !DiskScanner.IsRunningAsAdministrator())
        {
            WriteWarning("Not running as Administrator. Raw disk scanning and `wsl --mount` require an elevated terminal.");
        }
    }

    private static int UnknownCommand(string command)
    {
        WriteError($"Unknown command '{command}'.");
        PrintUsage();
        return 2;
    }

    private static void WriteInfo(string message) => Console.WriteLine($"[*] {message}");

    private static void WriteSuccess(string message)
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"[OK] {message}");
        Console.ResetColor();
    }

    private static void WriteWarning(string message)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"[!] {message}");
        Console.ResetColor();
    }

    private static void WriteError(string message)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.Error.WriteLine($"[ERROR] {message}");
        Console.ResetColor();
    }

    private static void PrintUsage()
    {
        string ver = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";
        Console.WriteLine($"""
            WslDrive.Cli v{ver} - Linux Partition Mounter for Windows 11 (WSL2) [Default: RO, optional --rw]

            Usage:
              WslDrive.Cli list [--all] [--image <file>]
              WslDrive.Cli mount --disk <index> [--partition <num>] [--letter <L:>] [--name <name>] [--fs <type>] [--rw|--ro] [--no-noload] [--distro <name>]
              WslDrive.Cli mount --vhd <path> [--partition <num>] [--letter <L:>] [--name <name>] [--fs <type>] [--rw|--ro] [--no-noload] [--distro <name>]
              WslDrive.Cli unmount [--letter <L:>] [--disk <index>] [--vhd <path>] [--distro <name>]
              WslDrive.Cli status [--distro <name>]
              WslDrive.Cli --version

            Examples:
              WslDrive.Cli list
              WslDrive.Cli mount --disk 1 --partition 2 --letter L:
              WslDrive.Cli mount --disk 1 --partition 2 --letter L: --rw
              WslDrive.Cli mount --vhd C:\VMs\data.vhdx --partition 1 --letter M:
              WslDrive.Cli status
              WslDrive.Cli unmount --letter L:
            """);
    }

    private static async Task<int> HandleNbdDaemonAsync(string[] args)
    {
        int diskIndex = -1;
        long offset = -1;
        long length = -1;
        int sectorSize = 512;
        string? distro = null;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--disk":
                    diskIndex = int.Parse(RequireArgValue(args, ref i, "--disk"));
                    break;
                case "--offset":
                    offset = long.Parse(RequireArgValue(args, ref i, "--offset"));
                    break;
                case "--length":
                    length = long.Parse(RequireArgValue(args, ref i, "--length"));
                    break;
                case "--sector":
                    sectorSize = int.Parse(RequireArgValue(args, ref i, "--sector"));
                    break;
                case "--distro":
                    distro = RequireArgValue(args, ref i, "--distro");
                    break;
            }
        }

        if (diskIndex < 0 || offset < 0 || length <= 0 || string.IsNullOrWhiteSpace(distro))
        {
            Console.WriteLine($"{NbdDiskBridge.ErrorPrefix}Invalid __nbd-daemon arguments.");
            Console.Out.Flush();
            return 1;
        }

        var provider = new InProcessNbdBridgeProvider();
        NbdBridgeAttachment attach = await provider.AttachPartitionAsync(
            diskIndex,
            offset,
            length,
            sectorSize,
            distro).ConfigureAwait(false);

        if (!attach.Success || attach.Session is not InProcessNbdSession session)
        {
            Console.WriteLine($"{NbdDiskBridge.ErrorPrefix}{attach.ErrorMessage}");
            Console.Out.Flush();
            return 1;
        }

        Console.WriteLine($"{NbdDiskBridge.ReadyPrefix}{attach.NbdDevicePath}");
        Console.Out.Flush();
        Console.SetOut(TextWriter.Null);

        await session.Completion.ConfigureAwait(false);
        return 0;
    }
}
