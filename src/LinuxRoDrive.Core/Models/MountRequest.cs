namespace LinuxRoDrive.Core.Models;

/// <summary>
/// Parameters specifying how to mount a Linux disk/partition or VHDX via WSL2 and map a Windows drive letter.
/// </summary>
public sealed class MountRequest
{
    /// <summary>
    /// Windows physical disk index (e.g. 1 for \\.\PhysicalDrive1). Mutually exclusive with <see cref="VhdPath"/>.
    /// </summary>
    public int? DiskIndex { get; init; }

    /// <summary>
    /// Path to a .vhd / .vhdx virtual disk image. Mutually exclusive with <see cref="DiskIndex"/>.
    /// </summary>
    public string? VhdPath { get; init; }

    /// <summary>
    /// 1-based partition number to mount (null or 0 for whole disk/unpartitioned filesystem).
    /// </summary>
    public int? PartitionNumber { get; init; }

    /// <summary>
    /// Target Windows drive letter to map (e.g., 'L').
    /// </summary>
    public char DriveLetter { get; init; }

    /// <summary>
    /// Optional custom mount directory name under /mnt/wsl/. Auto-generated if omitted.
    /// </summary>
    public string? MountName { get; init; }

    /// <summary>
    /// Optional filesystem type passed to `wsl --mount --type` (e.g., "ext4", "btrfs", "xfs").
    /// </summary>
    public string? FileSystemType { get; init; }

    /// <summary>
    /// When true (default), passes `ro,noload` for ext3/ext4 to avoid replaying dirty journals on read-only mounts.
    /// </summary>
    public bool UseNoLoadJournal { get; init; } = true;

    /// <summary>
    /// Target WSL distribution name whose UNC namespace (\\wsl.localhost\Distro\mnt\wsl\...) is used.
    /// Uses the default WSL distro if null/empty.
    /// </summary>
    public string? DistroName { get; init; }

    public string GetTargetWslDiskArgument()
    {
        if (!string.IsNullOrWhiteSpace(VhdPath))
        {
            return VhdPath;
        }
        if (DiskIndex.HasValue)
        {
            return $@"\\.\PhysicalDrive{DiskIndex.Value}";
        }
        throw new InvalidOperationException("Either DiskIndex or VhdPath must be specified.");
    }

    public string ResolveMountName()
    {
        if (!string.IsNullOrWhiteSpace(MountName))
        {
            return SanitizeMountName(MountName);
        }

        string partSuffix = PartitionNumber is > 0 ? $"p{PartitionNumber.Value}" : "";
        if (!string.IsNullOrWhiteSpace(VhdPath))
        {
            string baseName = Path.GetFileNameWithoutExtension(VhdPath);
            return SanitizeMountName($"linuxro_{baseName}{partSuffix}");
        }

        int idx = DiskIndex ?? 0;
        return $"linuxro_d{idx}{partSuffix}";
    }

    private static string SanitizeMountName(string name)
    {
        var chars = name.Select(c => char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '_').ToArray();
        string sanitized = new string(chars).Trim('_');
        return string.IsNullOrEmpty(sanitized) ? "linuxro_mount" : sanitized;
    }
}
