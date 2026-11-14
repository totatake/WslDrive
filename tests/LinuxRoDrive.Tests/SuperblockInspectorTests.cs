using System.Buffers.Binary;
using System.Text;
using LinuxRoDrive.Core.Disk;
using LinuxRoDrive.Core.Models;
using Xunit;

namespace LinuxRoDrive.Tests;

public sealed class SuperblockInspectorTests
{
    [Fact]
    public void Inspect_Ext4Partition_ExtractsTypeLabelAndUuid()
    {
        byte[] partition = CreateExtPartitionBuffer(
            compatFlags: 0x0004,     // HAS_JOURNAL
            incompatFlags: 0x00C0,   // EXTENTS (0x40) | 64BIT (0x80)
            roCompatFlags: 0x0400,   // METADATA_CSUM
            uuidBytes: [0x12, 0x34, 0x56, 0x78, 0x9A, 0xBC, 0x4D, 0xEF, 0x80, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77],
            label: "UbuntuData");

        using var ms = new MemoryStream(partition);
        SuperblockInfo info = SuperblockInspector.Inspect(ms, 0, partition.Length);

        Assert.Equal(LinuxFileSystemType.Ext4, info.FileSystemType);
        Assert.Equal("UbuntuData", info.VolumeLabel);
        Assert.Equal("12345678-9abc-4def-8011-223344556677", info.Uuid);
    }

    [Fact]
    public void Inspect_Ext3Partition_DetectsJournalWithoutExt4Flags()
    {
        byte[] partition = CreateExtPartitionBuffer(
            compatFlags: 0x0004, // HAS_JOURNAL
            incompatFlags: 0x0002, // FILETYPE
            roCompatFlags: 0x0001, // SPARSE_SUPER
            uuidBytes: new byte[16],
            label: "LegacyExt3");

        using var ms = new MemoryStream(partition);
        SuperblockInfo info = SuperblockInspector.Inspect(ms, 0, partition.Length);

        Assert.Equal(LinuxFileSystemType.Ext3, info.FileSystemType);
        Assert.Equal("LegacyExt3", info.VolumeLabel);
    }

    [Fact]
    public void Inspect_Ext2Partition_DetectsExt2()
    {
        byte[] partition = CreateExtPartitionBuffer(
            compatFlags: 0,
            incompatFlags: 0x0002,
            roCompatFlags: 0x0001,
            uuidBytes: new byte[16],
            label: "BootExt2");

        using var ms = new MemoryStream(partition);
        SuperblockInfo info = SuperblockInspector.Inspect(ms, 0, partition.Length);

        Assert.Equal(LinuxFileSystemType.Ext2, info.FileSystemType);
        Assert.Equal("BootExt2", info.VolumeLabel);
    }

    [Fact]
    public void Inspect_BtrfsPartition_ExtractsTypeLabelAndUuid()
    {
        byte[] partition = new byte[0x10000 + 0x400];
        int sb = 0x10000;
        Encoding.ASCII.GetBytes("_BHRfS_M").CopyTo(partition, sb + 0x40);
        byte[] uuid = [0xAA, 0xBB, 0xCC, 0xDD, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88, 0x99, 0x00, 0xAA, 0xBB];
        uuid.CopyTo(partition, sb + 0x20);
        Encoding.UTF8.GetBytes("BtrfsPool").CopyTo(partition, sb + 0x12B);

        using var ms = new MemoryStream(partition);
        SuperblockInfo info = SuperblockInspector.Inspect(ms, 0, partition.Length);

        Assert.Equal(LinuxFileSystemType.Btrfs, info.FileSystemType);
        Assert.Equal("BtrfsPool", info.VolumeLabel);
        Assert.Equal("aabbccdd-1122-3344-5566-77889900aabb", info.Uuid);
    }

    [Fact]
    public void Inspect_XfsPartition_ExtractsTypeLabelAndUuid()
    {
        byte[] partition = new byte[4096];
        Encoding.ASCII.GetBytes("XFSB").CopyTo(partition, 0);
        byte[] uuid = [0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F, 0x10];
        uuid.CopyTo(partition, 0x20);
        Encoding.ASCII.GetBytes("XfsData").CopyTo(partition, 0x6C);

        using var ms = new MemoryStream(partition);
        SuperblockInfo info = SuperblockInspector.Inspect(ms, 0, partition.Length);

        Assert.Equal(LinuxFileSystemType.Xfs, info.FileSystemType);
        Assert.Equal("XfsData", info.VolumeLabel);
        Assert.Equal("01020304-0506-0708-090a-0b0c0d0e0f10", info.Uuid);
    }

    [Fact]
    public void Inspect_LinuxSwap_DetectsSwapSpace2()
    {
        byte[] partition = new byte[8192];
        Encoding.ASCII.GetBytes("SWAPSPACE2").CopyTo(partition, 4086);

        using var ms = new MemoryStream(partition);
        SuperblockInfo info = SuperblockInspector.Inspect(ms, 0, partition.Length);

        Assert.Equal(LinuxFileSystemType.LinuxSwap, info.FileSystemType);
    }

    [Fact]
    public void Inspect_LuksHeader_DetectsLuks()
    {
        byte[] partition = new byte[4096];
        partition[0] = (byte)'L';
        partition[1] = (byte)'U';
        partition[2] = (byte)'K';
        partition[3] = (byte)'S';
        partition[4] = 0xBA;
        partition[5] = 0xBE;

        using var ms = new MemoryStream(partition);
        SuperblockInfo info = SuperblockInspector.Inspect(ms, 0, partition.Length);

        Assert.Equal(LinuxFileSystemType.Luks, info.FileSystemType);
    }

    [Fact]
    public void Inspect_NtfsHeader_DetectsNtfs()
    {
        byte[] partition = new byte[4096];
        Encoding.ASCII.GetBytes("NTFS    ").CopyTo(partition, 3);

        using var ms = new MemoryStream(partition);
        SuperblockInfo info = SuperblockInspector.Inspect(ms, 0, partition.Length);

        Assert.Equal(LinuxFileSystemType.Ntfs, info.FileSystemType);
    }

    internal static byte[] CreateExtPartitionBuffer(
        uint compatFlags,
        uint incompatFlags,
        uint roCompatFlags,
        byte[] uuidBytes,
        string label,
        int totalSize = 4096)
    {
        byte[] buf = new byte[totalSize];
        const int sb = 1024;
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(sb + 0x38, 2), 0xEF53);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(sb + 0x5C, 4), compatFlags);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(sb + 0x60, 4), incompatFlags);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(sb + 0x64, 4), roCompatFlags);
        uuidBytes.AsSpan(0, Math.Min(16, uuidBytes.Length)).CopyTo(buf.AsSpan(sb + 0x68, 16));
        byte[] labelBytes = Encoding.UTF8.GetBytes(label);
        labelBytes.AsSpan(0, Math.Min(16, labelBytes.Length)).CopyTo(buf.AsSpan(sb + 0x78, 16));
        return buf;
    }
}
