namespace WslMountDrive.Core.Models;

/// <summary>
/// Recognized filesystem or partition content types detected via superblock inspection.
/// </summary>
public enum LinuxFileSystemType
{
    Unknown = 0,
    Ext2,
    Ext3,
    Ext4,
    Btrfs,
    Xfs,
    LinuxSwap,
    Luks,
    Ntfs,
    Fat32,
    ExFat
}

public static class LinuxFileSystemTypeExtensions
{
    /// <summary>
    /// Returns true if the filesystem is a mountable Linux filesystem supported by WSL2 mount.
    /// </summary>
    public static bool IsMountableLinuxFs(this LinuxFileSystemType fsType) => fsType is
        LinuxFileSystemType.Ext2 or
        LinuxFileSystemType.Ext3 or
        LinuxFileSystemType.Ext4 or
        LinuxFileSystemType.Btrfs or
        LinuxFileSystemType.Xfs;

    /// <summary>
    /// Returns the Linux kernel filesystem type argument for wsl --mount --type.
    /// </summary>
    public static string? ToWslTypeName(this LinuxFileSystemType fsType) => fsType switch
    {
        LinuxFileSystemType.Ext2 => "ext2",
        LinuxFileSystemType.Ext3 => "ext3",
        LinuxFileSystemType.Ext4 => "ext4",
        LinuxFileSystemType.Btrfs => "btrfs",
        LinuxFileSystemType.Xfs => "xfs",
        _ => null
    };

    /// <summary>
    /// Returns true if the filesystem supports the ext-family noload mount option.
    /// </summary>
    public static bool SupportsNoLoad(this LinuxFileSystemType fsType) => fsType is
        LinuxFileSystemType.Ext3 or
        LinuxFileSystemType.Ext4;
}
