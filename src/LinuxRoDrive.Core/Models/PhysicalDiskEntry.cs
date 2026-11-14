namespace LinuxRoDrive.Core.Models;

/// <summary>
/// Represents a physical drive or inspected disk image file and its partitions.
/// </summary>
public sealed class PhysicalDiskEntry
{
    public int DiskIndex { get; init; }
    public string DevicePath { get; init; } = string.Empty;
    public string Model { get; init; } = string.Empty;
    public string InterfaceType { get; init; } = string.Empty;
    public long SizeBytes { get; init; }
    public int SectorSize { get; init; } = 512;
    public string PartitionStyle { get; init; } = "UNKNOWN";

    /// <summary>
    /// True if Windows currently holds mounted volumes (e.g., C:, D:) on this physical disk.
    /// `wsl --mount` attaches an entire physical disk and cannot mount disks in use by Windows.
    /// </summary>
    public bool HasWindowsMountedVolumes { get; init; }

    public string WindowsDriveLetters { get; init; } = string.Empty;
    public string? ScanError { get; init; }

    public List<PartitionEntry> Partitions { get; init; } = [];

    public string FormattedSize => PartitionEntry.FormatBytes(SizeBytes);
}
