using System.Buffers.Binary;
using System.Text;
using WslMountDrive.Core.Models;

namespace WslMountDrive.Core.Disk;

public readonly record struct SuperblockInfo(
    LinuxFileSystemType FileSystemType,
    string VolumeLabel,
    string Uuid);

/// <summary>
/// Read-only inspector for Linux (ext2/ext3/ext4, btrfs, xfs, swap, LUKS) and common Windows filesystem superblocks.
/// </summary>
public static class SuperblockInspector
{
    private const int ExtSuperblockOffset = 1024;
    private const int ExtSuperblockSize = 1024;
    private const int BtrfsSuperblockOffset = 0x10000;
    private const int BtrfsSuperblockSize = 0x230;
    private const int SwapMagicOffset = 4086;

    public static SuperblockInfo Inspect(Stream stream, long partitionOffset, long partitionLength)
    {
        ArgumentNullException.ThrowIfNull(stream);

        if (partitionLength <= 0 || partitionOffset < 0)
        {
            return new SuperblockInfo(LinuxFileSystemType.Unknown, string.Empty, string.Empty);
        }

        // 1. Check sector 0 (XFS, LUKS, NTFS, exFAT, FAT32)
        if (partitionLength >= 512)
        {
            Span<byte> sector0 = stackalloc byte[512];
            int read0 = ReadAt(stream, partitionOffset, sector0);
            if (read0 >= 128)
            {
                var sector0Result = InspectHeaderAtZero(sector0[..read0]);
                if (sector0Result.FileSystemType != LinuxFileSystemType.Unknown)
                {
                    return sector0Result;
                }
            }
        }

        // 2. Check ext2 / ext3 / ext4 at partitionOffset + 1024
        if (partitionLength >= ExtSuperblockOffset + 0x90)
        {
            int toRead = (int)Math.Min(ExtSuperblockSize, partitionLength - ExtSuperblockOffset);
            Span<byte> extSb = stackalloc byte[ExtSuperblockSize];
            int readExt = ReadAt(stream, partitionOffset + ExtSuperblockOffset, extSb[..toRead]);
            if (readExt >= 0x90)
            {
                var extResult = InspectExtSuperblock(extSb[..readExt]);
                if (extResult.FileSystemType != LinuxFileSystemType.Unknown)
                {
                    return extResult;
                }
            }
        }

        // 3. Check Linux Swap at partitionOffset + 4086
        if (partitionLength >= SwapMagicOffset + 10)
        {
            Span<byte> swapBuf = stackalloc byte[10];
            if (ReadAt(stream, partitionOffset + SwapMagicOffset, swapBuf) == 10 &&
                (swapBuf.SequenceEqual("SWAPSPACE2"u8) || swapBuf.SequenceEqual("SWAP-SPACE"u8)))
            {
                return new SuperblockInfo(LinuxFileSystemType.LinuxSwap, string.Empty, string.Empty);
            }
        }

        // 4. Check Btrfs at partitionOffset + 0x10000
        if (partitionLength >= BtrfsSuperblockOffset + BtrfsSuperblockSize)
        {
            Span<byte> btrfsSb = stackalloc byte[BtrfsSuperblockSize];
            int readBtrfs = ReadAt(stream, partitionOffset + BtrfsSuperblockOffset, btrfsSb);
            if (readBtrfs >= BtrfsSuperblockSize)
            {
                var btrfsResult = InspectBtrfsSuperblock(btrfsSb);
                if (btrfsResult.FileSystemType != LinuxFileSystemType.Unknown)
                {
                    return btrfsResult;
                }
            }
        }

        return new SuperblockInfo(LinuxFileSystemType.Unknown, string.Empty, string.Empty);
    }

    public static SuperblockInfo InspectExtSuperblock(ReadOnlySpan<byte> sb)
    {
        if (sb.Length < 0x88)
        {
            return new SuperblockInfo(LinuxFileSystemType.Unknown, string.Empty, string.Empty);
        }

        ushort magic = BinaryPrimitives.ReadUInt16LittleEndian(sb.Slice(0x38, 2));
        if (magic != 0xEF53)
        {
            return new SuperblockInfo(LinuxFileSystemType.Unknown, string.Empty, string.Empty);
        }

        uint compat = BinaryPrimitives.ReadUInt32LittleEndian(sb.Slice(0x5C, 4));
        uint incompat = BinaryPrimitives.ReadUInt32LittleEndian(sb.Slice(0x60, 4));
        uint roCompat = BinaryPrimitives.ReadUInt32LittleEndian(sb.Slice(0x64, 4));

        // Ext4 indicators: EXTENTS (0x0040), 64BIT (0x0080), FLEX_BG (0x0200), or ro_compat flags beyond Ext2/3 basics (0x0007)
        LinuxFileSystemType fsType;
        if ((incompat & 0x0040u) != 0 ||
            (incompat & 0x0080u) != 0 ||
            (incompat & 0x0200u) != 0 ||
            (roCompat & ~0x0007u) != 0)
        {
            fsType = LinuxFileSystemType.Ext4;
        }
        else if ((compat & 0x0004u) != 0)
        {
            fsType = LinuxFileSystemType.Ext3;
        }
        else
        {
            fsType = LinuxFileSystemType.Ext2;
        }

        string uuid = FormatBigEndianUuid(sb.Slice(0x68, 16));
        string label = ReadNullTerminatedUtf8(sb.Slice(0x78, 16));

        return new SuperblockInfo(fsType, label, uuid);
    }

    public static SuperblockInfo InspectBtrfsSuperblock(ReadOnlySpan<byte> sb)
    {
        if (sb.Length < 0x22B)
        {
            return new SuperblockInfo(LinuxFileSystemType.Unknown, string.Empty, string.Empty);
        }

        if (!sb.Slice(0x40, 8).SequenceEqual("_BHRfS_M"u8))
        {
            return new SuperblockInfo(LinuxFileSystemType.Unknown, string.Empty, string.Empty);
        }

        string uuid = FormatBigEndianUuid(sb.Slice(0x20, 16));
        string label = ReadNullTerminatedUtf8(sb.Slice(0x12B, 256));

        return new SuperblockInfo(LinuxFileSystemType.Btrfs, label, uuid);
    }

    public static SuperblockInfo InspectHeaderAtZero(ReadOnlySpan<byte> header)
    {
        if (header.Length < 128)
        {
            return new SuperblockInfo(LinuxFileSystemType.Unknown, string.Empty, string.Empty);
        }

        // XFS: "XFSB" at offset 0
        if (header.Slice(0, 4).SequenceEqual("XFSB"u8))
        {
            string uuid = FormatBigEndianUuid(header.Slice(0x20, 16));
            string label = ReadNullTerminatedUtf8(header.Slice(0x6C, 12));
            return new SuperblockInfo(LinuxFileSystemType.Xfs, label, uuid);
        }

        // LUKS: "LUKS\xba\xbe" at offset 0
        if (header.Length >= 6 &&
            header[0] == (byte)'L' && header[1] == (byte)'U' && header[2] == (byte)'K' && header[3] == (byte)'S' &&
            header[4] == 0xBA && header[5] == 0xBE)
        {
            string uuid = header.Length >= 0xD0 ? ReadNullTerminatedUtf8(header.Slice(0xA8, 40)) : string.Empty;
            return new SuperblockInfo(LinuxFileSystemType.Luks, "LUKS Encrypted", uuid);
        }

        // NTFS / exFAT / FAT32
        if (header.Length >= 11 && header.Slice(3, 8).SequenceEqual("NTFS    "u8))
        {
            return new SuperblockInfo(LinuxFileSystemType.Ntfs, string.Empty, string.Empty);
        }

        if (header.Length >= 11 && header.Slice(3, 8).SequenceEqual("EXFAT   "u8))
        {
            return new SuperblockInfo(LinuxFileSystemType.ExFat, string.Empty, string.Empty);
        }

        if (header.Length >= 0x5A && header.Slice(0x52, 8).SequenceEqual("FAT32   "u8))
        {
            return new SuperblockInfo(LinuxFileSystemType.Fat32, string.Empty, string.Empty);
        }

        return new SuperblockInfo(LinuxFileSystemType.Unknown, string.Empty, string.Empty);
    }

    internal static string FormatBigEndianUuid(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 16)
        {
            return string.Empty;
        }

        bool allZero = true;
        foreach (byte b in bytes[..16])
        {
            if (b != 0)
            {
                allZero = false;
                break;
            }
        }
        if (allZero)
        {
            return string.Empty;
        }

        return string.Create(36, bytes[..16].ToArray(), static (span, b) =>
        {
            WriteHex(b[0..4], span[0..8]);
            span[8] = '-';
            WriteHex(b[4..6], span[9..13]);
            span[13] = '-';
            WriteHex(b[6..8], span[14..18]);
            span[18] = '-';
            WriteHex(b[8..10], span[19..23]);
            span[23] = '-';
            WriteHex(b[10..16], span[24..36]);
        });
    }

    private static void WriteHex(ReadOnlySpan<byte> src, Span<char> dst)
    {
        const string HexChars = "0123456789abcdef";
        for (int i = 0; i < src.Length; i++)
        {
            dst[i * 2] = HexChars[src[i] >> 4];
            dst[i * 2 + 1] = HexChars[src[i] & 0x0F];
        }
    }

    private static string ReadNullTerminatedUtf8(ReadOnlySpan<byte> bytes)
    {
        int nulIndex = bytes.IndexOf((byte)0);
        ReadOnlySpan<byte> sliced = nulIndex >= 0 ? bytes[..nulIndex] : bytes;
        return Encoding.UTF8.GetString(sliced).TrimEnd();
    }

    private static int ReadAt(Stream stream, long offset, Span<byte> destination)
    {
        if (offset >= stream.Length)
        {
            return 0;
        }

        stream.Position = offset;
        int total = 0;
        while (total < destination.Length)
        {
            int read = stream.Read(destination[total..]);
            if (read == 0)
            {
                break;
            }
            total += read;
        }
        return total;
    }
}
