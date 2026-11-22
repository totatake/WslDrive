namespace WslDrive.Core.Models;

/// <summary>
/// Represents an active WSL2 mount under /mnt/wsl/ and its mapped Windows drive letter (if any).
/// </summary>
public sealed class MountSession
{
    public string MountName { get; init; } = string.Empty;
    public string SourceDeviceInWsl { get; init; } = string.Empty;
    public string WslMountPoint { get; init; } = string.Empty;
    public string UncPath { get; init; } = string.Empty;
    public string? MappedDriveLetter { get; init; }
    public string FileSystemType { get; init; } = string.Empty;
    public string MountOptions { get; init; } = string.Empty;
    public bool IsReadOnly { get; init; }
    public string AccessModeDisplay => IsReadOnly ? "RO" : "RW";
    public string? TargetWslDiskPath { get; init; }
    public int? DiskIndex { get; init; }
    public int? PartitionNumber { get; init; }
}
