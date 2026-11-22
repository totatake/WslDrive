namespace WslDrive.Core.Models;

/// <summary>
/// Represents a parsed disk partition (MBR, GPT, or raw unpartitioned filesystem).
/// </summary>
public sealed class PartitionEntry
{
    /// <summary>
    /// 1-based partition index matching the number expected by `wsl --mount --partition`.
    /// 0 indicates a raw unpartitioned filesystem occupying the entire disk/image.
    /// </summary>
    public int PartitionNumber { get; init; }

    public ulong StartSector { get; init; }
    public ulong SectorCount { get; init; }
    public long OffsetBytes { get; init; }
    public long SizeBytes { get; init; }

    public string PartitionTypeDescription { get; init; } = string.Empty;
    public string PartitionTypeGuidOrId { get; init; } = string.Empty;
    public string PartitionName { get; init; } = string.Empty;

    public LinuxFileSystemType FileSystemType { get; set; } = LinuxFileSystemType.Unknown;
    public string VolumeLabel { get; set; } = string.Empty;
    public string Uuid { get; set; } = string.Empty;

    public bool IsLinuxFileSystem => FileSystemType.IsMountableLinuxFs();

    public string FormattedSize => FormatBytes(SizeBytes);

    public static string FormatBytes(long bytes)
    {
        if (bytes < 0) return "0 B";
        string[] units = ["B", "KB", "MB", "GB", "TB", "PB"];
        double value = bytes;
        int unitIndex = 0;
        while (value >= 1024.0 && unitIndex < units.Length - 1)
        {
            value /= 1024.0;
            unitIndex++;
        }
        return unitIndex == 0 ? $"{bytes} B" : $"{value:F2} {units[unitIndex]}";
    }
}
