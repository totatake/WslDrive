using System.ComponentModel;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.Principal;
using WslMountDrive.Core.Interop;
using WslMountDrive.Core.Models;
using Microsoft.Win32.SafeHandles;

namespace WslMountDrive.Core.Disk;

/// <summary>
/// Scans physical Windows drives (via WMI and raw read-only handles) and disk image files
/// to enumerate partitions and inspect Linux filesystems prior to mounting.
/// </summary>
public sealed class DiskScanner
{
    public static bool IsRunningAsAdministrator()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        WindowsPrincipal principal = new(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    public List<PhysicalDiskEntry> ScanPhysicalDisks()
    {
        var results = new List<PhysicalDiskEntry>();
        if (!OperatingSystem.IsWindows())
        {
            return results;
        }

        Dictionary<int, List<string>> diskDriveLetters = QueryWindowsDriveLettersByDisk();

        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Index, DeviceID, Model, InterfaceType, Size, BytesPerSector FROM Win32_DiskDrive");

            foreach (ManagementObject obj in searcher.Get())
            {
                int index = Convert.ToInt32(obj["Index"] ?? -1);
                if (index < 0) continue;

                string devicePath = obj["DeviceID"]?.ToString() ?? $@"\\.\PhysicalDrive{index}";
                string model = obj["Model"]?.ToString()?.Trim() ?? "Unknown Disk";
                string iface = obj["InterfaceType"]?.ToString()?.Trim() ?? string.Empty;
                long wmiSize = obj["Size"] is not null ? Convert.ToInt64(obj["Size"]) : 0;
                int sectorSize = obj["BytesPerSector"] is not null ? Convert.ToInt32(obj["BytesPerSector"]) : 512;
                if (sectorSize <= 0) sectorSize = 512;

                diskDriveLetters.TryGetValue(index, out var letters);
                string letterSummary = letters is { Count: > 0 } ? string.Join(", ", letters) : string.Empty;

                results.Add(InspectPhysicalDisk(
                    index,
                    devicePath,
                    model,
                    iface,
                    wmiSize,
                    sectorSize,
                    hasWindowsVolumes: letters is { Count: > 0 },
                    windowsDriveLetters: letterSummary));
            }
        }
        catch (Exception ex) when (ex is ManagementException or PlatformNotSupportedException)
        {
            // Fallback: probe PhysicalDrive0..15 directly
            for (int idx = 0; idx < 16; idx++)
            {
                string path = $@"\\.\PhysicalDrive{idx}";
                using SafeFileHandle handle = OpenDiskReadOnly(path, out _);
                if (handle.IsInvalid) continue;

                long diskLen = QueryDiskLength(handle);
                diskDriveLetters.TryGetValue(idx, out var letters);
                string letterSummary = letters is { Count: > 0 } ? string.Join(", ", letters) : string.Empty;

                results.Add(InspectPhysicalDisk(
                    idx,
                    path,
                    $"PhysicalDrive{idx}",
                    string.Empty,
                    diskLen,
                    512,
                    hasWindowsVolumes: letters is { Count: > 0 },
                    windowsDriveLetters: letterSummary));
            }
        }

        return results.OrderBy(d => d.DiskIndex).ToList();
    }

    public PhysicalDiskEntry InspectImageFile(string imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
        {
            throw new FileNotFoundException("Disk image file not found.", imagePath);
        }

        using var fs = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        long sizeBytes = fs.Length;

        // Check for VHDX signature ("vhdxfile")
        if (sizeBytes >= 8)
        {
            Span<byte> sig = stackalloc byte[8];
            if (fs.Read(sig) == 8 && sig.SequenceEqual("vhdxfile"u8))
            {
                return new PhysicalDiskEntry
                {
                    DiskIndex = -1,
                    DevicePath = imagePath,
                    Model = Path.GetFileName(imagePath),
                    InterfaceType = "VHDX",
                    SizeBytes = sizeBytes,
                    SectorSize = 512,
                    PartitionStyle = "VHDX Container (mount with --vhd)",
                    HasWindowsMountedVolumes = false,
                    Partitions = []
                };
            }
            fs.Position = 0;
        }

        using var aligned = new AlignedDiskStream(fs, sizeBytes, alignment: 4096, leaveOpen: true);
        var ptResult = PartitionTableReader.ReadPartitions(aligned, 512);

        return new PhysicalDiskEntry
        {
            DiskIndex = -1,
            DevicePath = imagePath,
            Model = Path.GetFileName(imagePath),
            InterfaceType = "Image File",
            SizeBytes = sizeBytes,
            SectorSize = ptResult.EffectiveSectorSize,
            PartitionStyle = ptResult.PartitionStyle,
            HasWindowsMountedVolumes = false,
            Partitions = ptResult.Partitions
        };
    }

    private static PhysicalDiskEntry InspectPhysicalDisk(
        int index,
        string devicePath,
        string model,
        string iface,
        long fallbackSize,
        int sectorSize,
        bool hasWindowsVolumes,
        string windowsDriveLetters)
    {
        using SafeFileHandle handle = OpenDiskReadOnly(devicePath, out int win32Error);
        if (handle.IsInvalid)
        {
            string errMsg = win32Error == 5
                ? "Access denied (Administrator privileges required to read partition/superblock headers)."
                : new Win32Exception(win32Error).Message;

            return new PhysicalDiskEntry
            {
                DiskIndex = index,
                DevicePath = devicePath,
                Model = model,
                InterfaceType = iface,
                SizeBytes = fallbackSize,
                SectorSize = sectorSize,
                PartitionStyle = "UNREADABLE",
                HasWindowsMountedVolumes = hasWindowsVolumes,
                WindowsDriveLetters = windowsDriveLetters,
                ScanError = errMsg,
                Partitions = []
            };
        }

        long diskLength = QueryDiskLength(handle);
        if (diskLength <= 0)
        {
            diskLength = fallbackSize;
        }

        try
        {
            int alignment = Math.Max(4096, sectorSize);
            using var aligned = new AlignedDiskStream(handle, diskLength, alignment, leaveOpen: true);
            var ptResult = PartitionTableReader.ReadPartitions(aligned, sectorSize);

            return new PhysicalDiskEntry
            {
                DiskIndex = index,
                DevicePath = devicePath,
                Model = model,
                InterfaceType = iface,
                SizeBytes = diskLength,
                SectorSize = ptResult.EffectiveSectorSize,
                PartitionStyle = ptResult.PartitionStyle,
                HasWindowsMountedVolumes = hasWindowsVolumes,
                WindowsDriveLetters = windowsDriveLetters,
                Partitions = ptResult.Partitions
            };
        }
        catch (Exception ex)
        {
            return new PhysicalDiskEntry
            {
                DiskIndex = index,
                DevicePath = devicePath,
                Model = model,
                InterfaceType = iface,
                SizeBytes = diskLength,
                SectorSize = sectorSize,
                PartitionStyle = "ERROR",
                HasWindowsMountedVolumes = hasWindowsVolumes,
                WindowsDriveLetters = windowsDriveLetters,
                ScanError = ex.Message,
                Partitions = []
            };
        }
    }

    private static SafeFileHandle OpenDiskReadOnly(string devicePath, out int lastError)
    {
        SafeFileHandle handle = NativeMethods.CreateFileW(
            devicePath,
            NativeMethods.GENERIC_READ,
            NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE,
            IntPtr.Zero,
            NativeMethods.OPEN_EXISTING,
            NativeMethods.FILE_ATTRIBUTE_NORMAL,
            IntPtr.Zero);

        lastError = handle.IsInvalid ? Marshal.GetLastWin32Error() : 0;
        return handle;
    }

    private static long QueryDiskLength(SafeFileHandle handle)
    {
        return NativeMethods.DeviceIoControl(
            handle,
            NativeMethods.IOCTL_DISK_GET_LENGTH_INFO,
            IntPtr.Zero,
            0,
            out long length,
            sizeof(long),
            out _,
            IntPtr.Zero) ? length : 0;
    }

    private static Dictionary<int, List<string>> QueryWindowsDriveLettersByDisk()
    {
        var map = new Dictionary<int, List<string>>();
        try
        {
            using var partSearcher = new ManagementObjectSearcher(
                "SELECT DeviceID, DiskIndex FROM Win32_DiskPartition");

            var partToDisk = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (ManagementObject part in partSearcher.Get())
            {
                string? partId = part["DeviceID"]?.ToString();
                if (partId is not null && part["DiskIndex"] is not null)
                {
                    partToDisk[partId] = Convert.ToInt32(part["DiskIndex"]);
                }
            }

            using var assocSearcher = new ManagementObjectSearcher(
                "SELECT Antecedent, Dependent FROM Win32_LogicalDiskToPartition");

            foreach (ManagementObject assoc in assocSearcher.Get())
            {
                string? antecedent = assoc["Antecedent"]?.ToString();
                string? dependent = assoc["Dependent"]?.ToString();
                if (antecedent is null || dependent is null) continue;

                string? partId = ExtractWmiQuotedKey(antecedent);
                string? driveLetter = ExtractWmiQuotedKey(dependent);

                if (partId is not null && driveLetter is not null && partToDisk.TryGetValue(partId, out int diskIdx))
                {
                    if (!map.TryGetValue(diskIdx, out var list))
                    {
                        list = [];
                        map[diskIdx] = list;
                    }
                    if (!list.Contains(driveLetter, StringComparer.OrdinalIgnoreCase))
                    {
                        list.Add(driveLetter);
                    }
                }
            }
        }
        catch
        {
            // Non-fatal: WMI association query may fail in restricted environments
        }
        return map;
    }

    private static string? ExtractWmiQuotedKey(string wmiPath)
    {
        int eq = wmiPath.IndexOf("DeviceID=\"", StringComparison.OrdinalIgnoreCase);
        if (eq < 0) return null;
        int start = eq + "DeviceID=\"".Length;
        int end = wmiPath.IndexOf('"', start);
        if (end < 0) return null;
        return wmiPath[start..end].Replace(@"\\", @"\");
    }
}
