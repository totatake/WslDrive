using System.Buffers.Binary;
using System.Text;
using WslMountDrive.Core.Disk;
using WslMountDrive.Core.Models;
using Xunit;

namespace WslMountDrive.Tests;

public sealed class PartitionTableReaderTests
{
    [Fact]
    public void AlignedDiskStream_UnalignedReadsAcrossBlockBoundaries_ReturnExactBytes()
    {
        byte[] raw = new byte[12000];
        for (int i = 0; i < raw.Length; i++)
        {
            raw[i] = (byte)(i * 31 % 251);
        }

        using var backing = new MemoryStream(raw);
        using var aligned = new AlignedDiskStream(backing, raw.Length, alignment: 4096, leaveOpen: true);

        byte[] readBuf = new byte[5000];
        aligned.Seek(4000, SeekOrigin.Begin);
        int read = aligned.Read(readBuf, 0, readBuf.Length);

        Assert.Equal(5000, read);
        Assert.Equal(raw.AsSpan(4000, 5000).ToArray(), readBuf);
        Assert.Throws<NotSupportedException>(() => aligned.Write(new byte[4], 0, 4));
    }

    [Fact]
    public void ReadPartitions_GptDiskWithExt4_ExtractsPartitionsAndSuperblock()
    {
        const int sectorSize = 512;
        const int diskSectors = 2048; // 1 MiB
        byte[] disk = new byte[diskSectors * sectorSize];

        // LBA 0: Protective MBR
        disk[446 + 4] = 0xEE;
        BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(446 + 8, 4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(446 + 12, 4), diskSectors - 1);
        disk[510] = 0x55;
        disk[511] = 0xAA;

        // LBA 1: GPT Header
        int gptOffset = sectorSize;
        Encoding.ASCII.GetBytes("EFI PART").CopyTo(disk, gptOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(disk.AsSpan(gptOffset + 0x48, 8), 2);   // Entries LBA
        BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(gptOffset + 0x50, 4), 128); // Entry count
        BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(gptOffset + 0x54, 4), 128); // Entry size

        // LBA 2: Partition 1 = EFI System (LBA 34..100), Partition 2 = Linux Data ext4 (LBA 128..1023)
        int entry0 = 2 * sectorSize;
        new Guid("C12A7328-F81F-11D2-BA4B-00A0C93EC93B").TryWriteBytes(disk.AsSpan(entry0, 16));
        BinaryPrimitives.WriteUInt64LittleEndian(disk.AsSpan(entry0 + 0x20, 8), 34);
        BinaryPrimitives.WriteUInt64LittleEndian(disk.AsSpan(entry0 + 0x28, 8), 100);
        Encoding.Unicode.GetBytes("EFI").CopyTo(disk, entry0 + 0x38);

        int entry1 = entry0 + 128;
        new Guid("0FC63DAF-8483-4772-8E79-3D69D8477DE4").TryWriteBytes(disk.AsSpan(entry1, 16));
        BinaryPrimitives.WriteUInt64LittleEndian(disk.AsSpan(entry1 + 0x20, 8), 128);
        BinaryPrimitives.WriteUInt64LittleEndian(disk.AsSpan(entry1 + 0x28, 8), 1023);
        Encoding.Unicode.GetBytes("LinuxRoot").CopyTo(disk, entry1 + 0x38);

        byte[] ext4Payload = SuperblockInspectorTests.CreateExtPartitionBuffer(
            compatFlags: 0x0004,
            incompatFlags: 0x00C0,
            roCompatFlags: 0x0400,
            uuidBytes: [0xDE, 0xAD, 0xBE, 0xEF, 0x12, 0x34, 0x56, 0x78, 0x9A, 0xBC, 0x00, 0x11, 0x22, 0x33, 0x44, 0x55],
            label: "RootVolume");
        ext4Payload.CopyTo(disk, 128 * sectorSize);

        using var ms = new MemoryStream(disk);
        using var aligned = new AlignedDiskStream(ms, disk.Length, alignment: 4096, leaveOpen: true);
        PartitionTableResult result = PartitionTableReader.ReadPartitions(aligned, sectorSize);

        Assert.Equal("GPT", result.PartitionStyle);
        Assert.Equal(2, result.Partitions.Count);

        PartitionEntry p2 = result.Partitions[1];
        Assert.Equal(2, p2.PartitionNumber);
        Assert.Equal("LinuxRoot", p2.PartitionName);
        Assert.Equal(LinuxFileSystemType.Ext4, p2.FileSystemType);
        Assert.Equal("RootVolume", p2.VolumeLabel);
        Assert.Equal("deadbeef-1234-5678-9abc-001122334455", p2.Uuid);
        Assert.True(p2.IsLinuxFileSystem);
    }

    [Fact]
    public void ReadPartitions_MbrWithExtendedLogicalExt4_NumbersLogicalAt5()
    {
        const int sectorSize = 512;
        byte[] disk = new byte[2048 * sectorSize];
        disk[510] = 0x55;
        disk[511] = 0xAA;

        // Primary 1: NTFS at LBA 10..99
        disk[446 + 4] = 0x07;
        BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(446 + 8, 4), 10);
        BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(446 + 12, 4), 90);

        // Primary 2: Extended (0x05) at LBA 200..1500
        disk[462 + 4] = 0x05;
        BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(462 + 8, 4), 200);
        BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(462 + 12, 4), 1300);

        // EBR at LBA 200
        int ebrOffset = 200 * sectorSize;
        disk[ebrOffset + 510] = 0x55;
        disk[ebrOffset + 511] = 0xAA;
        disk[ebrOffset + 446 + 4] = 0x83; // Linux
        BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(ebrOffset + 446 + 8, 4), 8); // relative to 200 => LBA 208
        BinaryPrimitives.WriteUInt32LittleEndian(disk.AsSpan(ebrOffset + 446 + 12, 4), 512);

        byte[] ext4Payload = SuperblockInspectorTests.CreateExtPartitionBuffer(
            compatFlags: 0x0004,
            incompatFlags: 0x0040,
            roCompatFlags: 0,
            uuidBytes: new byte[16],
            label: "LogicalExt4");
        ext4Payload.CopyTo(disk, 208 * sectorSize);

        using var ms = new MemoryStream(disk);
        PartitionTableResult result = PartitionTableReader.ReadPartitions(ms, sectorSize);

        Assert.Equal("MBR", result.PartitionStyle);
        Assert.Equal(2, result.Partitions.Count);
        Assert.Equal(1, result.Partitions[0].PartitionNumber);
        Assert.Equal(5, result.Partitions[1].PartitionNumber);
        Assert.Equal(LinuxFileSystemType.Ext4, result.Partitions[1].FileSystemType);
        Assert.Equal("LogicalExt4", result.Partitions[1].VolumeLabel);
    }
}
