using System.Buffers.Binary;
using System.Text;
using WslMountDrive.Core.Models;

namespace WslMountDrive.Core.Disk;

public sealed record PartitionTableResult(string PartitionStyle, int EffectiveSectorSize, List<PartitionEntry> Partitions);

/// <summary>
/// Parses GPT, MBR (including EBR logical partitions), and raw unpartitioned filesystems
/// from any seekable read-only stream, enriching each partition via <see cref="SuperblockInspector"/>.
/// </summary>
public static class PartitionTableReader
{
    private static readonly Guid GptLinuxData = new("0FC63DAF-8483-4772-8E79-3D69D8477DE4");
    private static readonly Guid GptLinuxSwap = new("0657FD6D-A4AB-43C4-84E5-0933C84B4F4F");
    private static readonly Guid GptLinuxLvm = new("E6D6D379-F507-44C2-A23C-238F2A3DF928");
    private static readonly Guid GptLinuxRaid = new("A19D880F-05FC-4D3B-A006-743F0F84911E");
    private static readonly Guid GptLinuxRootX64 = new("4F68BCE3-E8CD-4DB1-96E7-FBCAF984B709");
    private static readonly Guid GptLinuxHome = new("933AC7E1-2EB4-4F13-B844-0E14E2AEF915");
    private static readonly Guid GptEfiSystem = new("C12A7328-F81F-11D2-BA4B-00A0C93EC93B");
    private static readonly Guid GptMsBasicData = new("EBD0A0A2-B9E5-4433-87C0-68B6B72699C7");
    private static readonly Guid GptMsReserved = new("E3C9E316-0B5C-4DB8-817D-F92DF00215AE");

    public static PartitionTableResult ReadPartitions(Stream stream, int sectorSize = 512)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (sectorSize <= 0) sectorSize = 512;

        if (stream.Length < 512)
        {
            return new PartitionTableResult("RAW", sectorSize, []);
        }

        byte[] mbr = new byte[512];
        if (ReadExactAt(stream, 0, mbr) < 512)
        {
            return new PartitionTableResult("UNKNOWN", sectorSize, []);
        }

        bool hasMbrSignature = mbr[510] == 0x55 && mbr[511] == 0xAA;
        bool hasProtectiveMbr = hasMbrSignature && HasProtectiveGptEntry(mbr);

        // Try GPT at configured sectorSize (and fallback to 4096 if protective MBR is set and 512 didn't match)
        int gptSectorSize = sectorSize;
        if (TryReadGptHeader(stream, gptSectorSize, out byte[] gptHeader))
        {
            var gptPartitions = ParseGptPartitions(stream, gptSectorSize, gptHeader);
            return new PartitionTableResult("GPT", gptSectorSize, gptPartitions);
        }

        if (hasProtectiveMbr && gptSectorSize != 4096 && TryReadGptHeader(stream, 4096, out gptHeader))
        {
            var gptPartitions = ParseGptPartitions(stream, 4096, gptHeader);
            return new PartitionTableResult("GPT", 4096, gptPartitions);
        }

        // Try MBR if signature 0x55AA is present and not purely protective
        if (hasMbrSignature && !hasProtectiveMbr)
        {
            var mbrPartitions = ParseMbrPartitions(stream, sectorSize, mbr);
            if (mbrPartitions.Count > 0)
            {
                return new PartitionTableResult("MBR", sectorSize, mbrPartitions);
            }
        }

        // Fallback: check if the entire stream is a raw unpartitioned filesystem
        var rawSb = SuperblockInspector.Inspect(stream, 0, stream.Length);
        if (rawSb.FileSystemType != LinuxFileSystemType.Unknown)
        {
            var rawEntry = new PartitionEntry
            {
                PartitionNumber = 0,
                StartSector = 0,
                SectorCount = (ulong)(stream.Length / sectorSize),
                OffsetBytes = 0,
                SizeBytes = stream.Length,
                PartitionTypeDescription = "Raw Unpartitioned Filesystem",
                PartitionTypeGuidOrId = "RAW",
                PartitionName = string.Empty,
                FileSystemType = rawSb.FileSystemType,
                VolumeLabel = rawSb.VolumeLabel,
                Uuid = rawSb.Uuid
            };
            return new PartitionTableResult("RAW", sectorSize, [rawEntry]);
        }

        return new PartitionTableResult(hasMbrSignature ? "MBR" : "RAW", sectorSize, []);
    }

    private static bool HasProtectiveGptEntry(ReadOnlySpan<byte> mbr)
    {
        for (int i = 0; i < 4; i++)
        {
            if (mbr[446 + i * 16 + 4] == 0xEE)
            {
                return true;
            }
        }
        return false;
    }

    private static bool TryReadGptHeader(Stream stream, int sectorSize, out byte[] header)
    {
        header = new byte[92];
        if (stream.Length < sectorSize + 92)
        {
            return false;
        }

        if (ReadExactAt(stream, sectorSize, header) < 92)
        {
            return false;
        }

        // "EFI PART"
        return header.AsSpan(0, 8).SequenceEqual("EFI PART"u8);
    }

    private static List<PartitionEntry> ParseGptPartitions(Stream stream, int sectorSize, byte[] header)
    {
        ulong entriesStartLba = BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(0x48, 8));
        uint entryCount = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0x50, 4));
        uint entrySize = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0x54, 4));

        if (entrySize < 128 || entrySize > 4096 || entryCount == 0 || entryCount > 4096)
        {
            return [];
        }

        long tableOffset = (long)(entriesStartLba * (ulong)sectorSize);
        long tableBytes = (long)entryCount * entrySize;
        if (tableOffset < 0 || tableOffset >= stream.Length)
        {
            return [];
        }

        int readableBytes = (int)Math.Min(tableBytes, stream.Length - tableOffset);
        byte[] tableBuffer = new byte[readableBytes];
        int actualRead = ReadExactAt(stream, tableOffset, tableBuffer);
        int actualCount = actualRead / (int)entrySize;

        var result = new List<PartitionEntry>();
        for (int i = 0; i < actualCount; i++)
        {
            ReadOnlySpan<byte> entry = tableBuffer.AsSpan(i * (int)entrySize, (int)entrySize);
            Guid typeGuid = new(entry.Slice(0, 16));
            if (typeGuid == Guid.Empty)
            {
                continue;
            }

            ulong firstLba = BinaryPrimitives.ReadUInt64LittleEndian(entry.Slice(0x20, 8));
            ulong lastLba = BinaryPrimitives.ReadUInt64LittleEndian(entry.Slice(0x28, 8));
            if (lastLba < firstLba)
            {
                continue;
            }

            ulong sectorCount = lastLba - firstLba + 1;
            long offsetBytes = (long)(firstLba * (ulong)sectorSize);
            long sizeBytes = (long)(sectorCount * (ulong)sectorSize);

            string partName = ReadUtf16NullTerminated(entry.Slice(0x38, 72));
            var sb = SuperblockInspector.Inspect(stream, offsetBytes, sizeBytes);

            result.Add(new PartitionEntry
            {
                PartitionNumber = i + 1,
                StartSector = firstLba,
                SectorCount = sectorCount,
                OffsetBytes = offsetBytes,
                SizeBytes = sizeBytes,
                PartitionTypeDescription = DescribeGptType(typeGuid),
                PartitionTypeGuidOrId = typeGuid.ToString(),
                PartitionName = partName,
                FileSystemType = sb.FileSystemType,
                VolumeLabel = sb.VolumeLabel,
                Uuid = sb.Uuid
            });
        }

        return result;
    }

    private static List<PartitionEntry> ParseMbrPartitions(Stream stream, int sectorSize, ReadOnlySpan<byte> mbr)
    {
        var result = new List<PartitionEntry>();
        ulong? extendedStartLba = null;

        for (int i = 0; i < 4; i++)
        {
            ReadOnlySpan<byte> entry = mbr.Slice(446 + i * 16, 16);
            byte typeId = entry[4];
            if (typeId == 0)
            {
                continue;
            }

            uint startLba = BinaryPrimitives.ReadUInt32LittleEndian(entry.Slice(8, 4));
            uint sectorCount = BinaryPrimitives.ReadUInt32LittleEndian(entry.Slice(12, 4));
            if (sectorCount == 0)
            {
                continue;
            }

            if (typeId is 0x05 or 0x0F or 0x85)
            {
                extendedStartLba ??= startLba;
                continue;
            }

            long offsetBytes = (long)startLba * sectorSize;
            long sizeBytes = (long)sectorCount * sectorSize;
            var sb = SuperblockInspector.Inspect(stream, offsetBytes, sizeBytes);

            result.Add(new PartitionEntry
            {
                PartitionNumber = i + 1,
                StartSector = startLba,
                SectorCount = sectorCount,
                OffsetBytes = offsetBytes,
                SizeBytes = sizeBytes,
                PartitionTypeDescription = DescribeMbrType(typeId),
                PartitionTypeGuidOrId = $"0x{typeId:X2}",
                PartitionName = string.Empty,
                FileSystemType = sb.FileSystemType,
                VolumeLabel = sb.VolumeLabel,
                Uuid = sb.Uuid
            });
        }

        // Walk EBR chain for logical partitions (Linux numbers logical partitions starting at 5)
        if (extendedStartLba.HasValue)
        {
            ParseExtendedMbrChain(stream, sectorSize, extendedStartLba.Value, result);
        }

        return result;
    }

    private static void ParseExtendedMbrChain(Stream stream, int sectorSize, ulong baseExtendedLba, List<PartitionEntry> result)
    {
        var visited = new HashSet<ulong>();
        ulong currentEbrLba = baseExtendedLba;
        int logicalNumber = 5;
        byte[] ebr = new byte[512];

        for (int step = 0; step < 64; step++)
        {
            if (!visited.Add(currentEbrLba))
            {
                break;
            }

            long ebrOffset = (long)(currentEbrLba * (ulong)sectorSize);
            if (ebrOffset < 0 || ebrOffset + 512 > stream.Length)
            {
                break;
            }

            if (ReadExactAt(stream, ebrOffset, ebr) < 512 || ebr[510] != 0x55 || ebr[511] != 0xAA)
            {
                break;
            }

            // Entry 0: logical partition relative to currentEbrLba
            byte type0 = ebr[446 + 4];
            uint relStart0 = BinaryPrimitives.ReadUInt32LittleEndian(ebr.AsSpan(446 + 8, 4));
            uint count0 = BinaryPrimitives.ReadUInt32LittleEndian(ebr.AsSpan(446 + 12, 4));
            if (type0 != 0 && count0 > 0)
            {
                ulong absStart = currentEbrLba + relStart0;
                long offsetBytes = (long)(absStart * (ulong)sectorSize);
                long sizeBytes = (long)count0 * sectorSize;
                var sb = SuperblockInspector.Inspect(stream, offsetBytes, sizeBytes);

                result.Add(new PartitionEntry
                {
                    PartitionNumber = logicalNumber++,
                    StartSector = absStart,
                    SectorCount = count0,
                    OffsetBytes = offsetBytes,
                    SizeBytes = sizeBytes,
                    PartitionTypeDescription = DescribeMbrType(type0),
                    PartitionTypeGuidOrId = $"0x{type0:X2}",
                    PartitionName = string.Empty,
                    FileSystemType = sb.FileSystemType,
                    VolumeLabel = sb.VolumeLabel,
                    Uuid = sb.Uuid
                });
            }

            // Entry 1: link to next EBR relative to baseExtendedLba
            byte type1 = ebr[462 + 4];
            uint relNext = BinaryPrimitives.ReadUInt32LittleEndian(ebr.AsSpan(462 + 8, 4));
            if ((type1 is 0x05 or 0x0F or 0x85) && relNext > 0)
            {
                currentEbrLba = baseExtendedLba + relNext;
            }
            else
            {
                break;
            }
        }
    }

    private static string DescribeGptType(Guid guid)
    {
        if (guid == GptLinuxData) return "Linux Filesystem Data";
        if (guid == GptLinuxSwap) return "Linux Swap";
        if (guid == GptLinuxLvm) return "Linux LVM";
        if (guid == GptLinuxRaid) return "Linux RAID";
        if (guid == GptLinuxRootX64) return "Linux Root (x86-64)";
        if (guid == GptLinuxHome) return "Linux /home";
        if (guid == GptEfiSystem) return "EFI System Partition";
        if (guid == GptMsBasicData) return "Microsoft Basic Data";
        if (guid == GptMsReserved) return "Microsoft Reserved";
        return "GPT Partition";
    }

    private static string DescribeMbrType(byte typeId) => typeId switch
    {
        0x07 => "NTFS / exFAT",
        0x0B or 0x0C => "FAT32",
        0x82 => "Linux Swap",
        0x83 => "Linux Filesystem",
        0x8E => "Linux LVM",
        0xEF => "EFI System Partition",
        0xFD => "Linux RAID",
        _ => $"MBR Type 0x{typeId:X2}"
    };

    private static string ReadUtf16NullTerminated(ReadOnlySpan<byte> bytes)
    {
        int maxChars = bytes.Length / 2;
        int charLen = 0;
        for (int i = 0; i < maxChars; i++)
        {
            if (bytes[i * 2] == 0 && bytes[i * 2 + 1] == 0)
            {
                break;
            }
            charLen++;
        }
        return charLen == 0 ? string.Empty : Encoding.Unicode.GetString(bytes[..(charLen * 2)]);
    }

    private static int ReadExactAt(Stream stream, long offset, Span<byte> destination)
    {
        if (offset < 0 || offset >= stream.Length)
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
