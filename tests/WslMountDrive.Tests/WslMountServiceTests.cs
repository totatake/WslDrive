using System.Buffers.Binary;
using System.Text;
using WslMountDrive.Core.Interop;
using WslMountDrive.Core.Models;
using WslMountDrive.Core.Wsl;
using Xunit;

namespace WslMountDrive.Tests;

public sealed class WslMountServiceTests
{
    [Fact]
    public void DecodeWslBytes_HandlesUtf16LeWithAndWithoutBomAndUtf8()
    {
        byte[] utf16NoBom = Encoding.Unicode.GetBytes("Ubuntu-24.04\r\n");
        Assert.Equal("Ubuntu-24.04\r\n", WslCliRunner.DecodeWslBytes(utf16NoBom));

        byte[] utf16WithBom = [0xFF, 0xFE, .. Encoding.Unicode.GetBytes("Debian")];
        Assert.Equal("Debian", WslCliRunner.DecodeWslBytes(utf16WithBom));

        byte[] utf8 = Encoding.UTF8.GetBytes("/dev/sdd1 /mnt/wsl/test ext4 ro 0 0\n");
        Assert.Equal("/dev/sdd1 /mnt/wsl/test ext4 ro 0 0\n", WslCliRunner.DecodeWslBytes(utf8));
    }

    [Fact]
    public void BuildMountArguments_PhysicalDiskExt4_UsesRoNoLoadByDefault()
    {
        var req = new MountRequest
        {
            DiskIndex = 1,
            PartitionNumber = 2,
            DriveLetter = 'L',
            FileSystemType = "ext4",
            UseNoLoadJournal = true
        };

        List<string> args = WslMountService.BuildMountArguments(req);
        Assert.Equal(
            ["--mount", @"\\.\PhysicalDrive1", "--partition", "2", "--name", "wslmountdrive_d1p2", "--type", "ext4", "--options", "ro,noload"],
            args);
    }

    [Fact]
    public void BuildMountArguments_BtrfsOrNoLoadDisabled_UsesSimpleRo()
    {
        var btrfsReq = new MountRequest
        {
            DiskIndex = 2,
            PartitionNumber = 1,
            DriveLetter = 'M',
            FileSystemType = "btrfs",
            UseNoLoadJournal = true
        };
        Assert.Equal("ro", WslMountService.BuildMountArguments(btrfsReq)[^1]);

        var vhdReq = new MountRequest
        {
            VhdPath = @"C:\VMs\data.vhdx",
            PartitionNumber = 1,
            DriveLetter = 'N',
            FileSystemType = "ext4",
            UseNoLoadJournal = false
        };
        List<string> vhdArgs = WslMountService.BuildMountArguments(vhdReq);
        Assert.Contains("--vhd", vhdArgs);
        Assert.Equal("ro", vhdArgs[^1]);
    }

    [Fact]
    public void ParseDefaultDistroFromVerbose_ExtractsStarredUserDistro()
    {
        const string sample = """
              NAME                   STATE           VERSION
            * Ubuntu-22.04           Running         2
              docker-desktop-data    Stopped         2
              Debian                 Stopped         2
            """;

        string? distro = WslMountService.ParseDefaultDistroFromVerbose(sample);
        Assert.Equal("Ubuntu-22.04", distro);
    }

    [Fact]
    public void ParseProcMounts_FiltersWslBlockMountsAndMatchesDriveLetters()
    {
        const string procMounts = """
            none /mnt/wsl tmpfs rw,relatime 0 0
            /dev/sdd2 /mnt/wsl/wslmountdrive_d1p2 ext4 ro,relatime,noload 0 0
            /dev/sde1 /mnt/wsl/custom\040share btrfs ro 0 0
            /dev/sda / ext4 rw,relatime 0 0
            """;

        var mapped = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["L:"] = @"\\wsl.localhost\Ubuntu\mnt\wsl\wslmountdrive_d1p2"
        };

        List<MountSession> sessions = WslMountService.ParseProcMounts(procMounts, "Ubuntu", mapped);

        Assert.Equal(2, sessions.Count);
        Assert.Equal("wslmountdrive_d1p2", sessions[0].MountName);
        Assert.Equal("L:", sessions[0].MappedDriveLetter);
        Assert.Equal("ext4", sessions[0].FileSystemType);
        Assert.True(sessions[0].IsReadOnly);
        Assert.Equal(@"\\.\PhysicalDrive1", sessions[0].TargetWslDiskPath);
        Assert.Equal(1, sessions[0].DiskIndex);
        Assert.Equal(2, sessions[0].PartitionNumber);

        Assert.Equal("custom share", sessions[1].MountName);
        Assert.Null(sessions[1].MappedDriveLetter);
        Assert.Equal("btrfs", sessions[1].FileSystemType);
        Assert.Null(sessions[1].TargetWslDiskPath);
        Assert.Null(sessions[1].DiskIndex);
        Assert.Null(sessions[1].PartitionNumber);
    }

    [Fact]
    public void GetPartitionDeviceFromBase_FormatsSataAndNvmeDeviceNames()
    {
        Assert.Equal("/dev/sdc1", WslMountService.GetPartitionDeviceFromBase("/dev/sdc", 1));
        Assert.Equal("/dev/nvme0n1p1", WslMountService.GetPartitionDeviceFromBase("/dev/nvme0n1", 1));
        Assert.Equal("/dev/mmcblk0p2", WslMountService.GetPartitionDeviceFromBase("/dev/mmcblk0", 2));
        Assert.Equal("/dev/sdc", WslMountService.GetPartitionDeviceFromBase("/dev/sdc", 0));
    }

    [Fact]
    public void TryParseDosDeviceUncTarget_ParsesDosUncAndP9RdrTargets()
    {
        Assert.True(DriveMapper.TryParseDosDeviceUncTarget(
            @"\??\UNC\wsl.localhost\Ubuntu\mnt\wsl\wslmountdrive_d2p2",
            out string? unc1));
        Assert.Equal(@"\\wsl.localhost\Ubuntu\mnt\wsl\wslmountdrive_d2p2", unc1);

        Assert.True(DriveMapper.TryParseDosDeviceUncTarget(
            @"\Device\P9Rdr\;L:0000000000012345\wsl.localhost\Ubuntu\mnt\wsl\wslmountdrive_d2p2",
            out string? unc2));
        Assert.Equal(@"\\wsl.localhost\Ubuntu\mnt\wsl\wslmountdrive_d2p2", unc2);

        Assert.False(DriveMapper.TryParseDosDeviceUncTarget(@"\Device\HarddiskVolume3", out _));
        Assert.False(DriveMapper.TryParseDosDeviceUncTarget(@"\??\C:\Users\Public", out _));
    }

    [Fact]
    public async Task MountAsync_RetriesWithPlainRo_WhenNoLoadFails()
    {
        var runner = new FakeWslRunner();
        var service = new WslMountService(
            runner,
            (letter, unc) => new DriveMappingResult(true, $"{letter}:", $"Mapped {letter}: -> {unc}", true));

        var req = new MountRequest
        {
            DiskIndex = 1,
            PartitionNumber = 1,
            DriveLetter = 'L',
            FileSystemType = null,
            UseNoLoadJournal = true
        };

        MountOperationResult result = await service.MountAsync(req);

        Assert.True(result.Success);
        Assert.Contains(runner.RecordedCalls, c => c.Contains("ro,noload"));
        Assert.Contains(runner.RecordedCalls, c => c.EndsWith("ro"));
        Assert.Contains(runner.RecordedCalls, c => c.Contains("/mnt/wsl/wslmountdrive_d1p1"));
    }

    [Fact]
    public async Task MountAsync_DetachesAndRetries_WhenDiskAlreadyMountedStale()
    {
        var runner = new StaleMountWslRunner();
        var service = new WslMountService(
            runner,
            (letter, unc) => new DriveMappingResult(true, $"{letter}:", $"Mapped {letter}: -> {unc}", true));

        var req = new MountRequest
        {
            DiskIndex = 2,
            PartitionNumber = 2,
            DriveLetter = 'L',
            FileSystemType = "ext4",
            UseNoLoadJournal = true,
            DistroName = "Ubuntu"
        };

        MountOperationResult result = await service.MountAsync(req);

        Assert.True(result.Success);
        Assert.Equal("ro,noload", result.Session?.MountOptions);
        Assert.Contains(runner.RecordedCalls, c => c == @"--unmount \\.\PhysicalDrive2");
    }

    [Fact]
    public async Task UnmountAsync_KeepsDiskAttachedAndRunsWslUmount_WhenSiblingPartitionRemains()
    {
        var runner = new MultiPartitionWslRunner(
            "/dev/sdc1 /mnt/wsl/wslmountdrive_d2p1 ext4 ro,relatime,noload 0 0\n" +
            "/dev/sdc2 /mnt/wsl/wslmountdrive_d2p2 ext4 ro,relatime,noload 0 0\n");
        var service = new WslMountService(runner);

        UnmountOperationResult result = await service.UnmountAsync(
            driveLetter: null,
            diskIndex: 2,
            vhdPath: null,
            distroName: "Ubuntu",
            mountName: "wslmountdrive_d2p1");

        Assert.True(result.Success);
        Assert.Contains(runner.RecordedCalls, c => c == "-d Ubuntu -u root -- umount /mnt/wsl/wslmountdrive_d2p1");
        Assert.DoesNotContain(runner.RecordedCalls, c => c.Contains("--unmount"));
    }

    [Fact]
    public async Task MountAsync_MountsSiblingPartitionDirectly_WhenReMountingPartitionWithActiveSibling()
    {
        var runner = new MultiPartitionWslRunner(
            "/dev/sdc2 /mnt/wsl/wslmountdrive_d2p2 ext4 ro,relatime,noload 0 0\n",
            failMountWithAlreadyMounted: true);
        var service = new WslMountService(
            runner,
            (letter, unc) => new DriveMappingResult(true, $"{letter}:", $"Mapped {letter}: -> {unc}", true));

        var req = new MountRequest
        {
            DiskIndex = 2,
            PartitionNumber = 1,
            DriveLetter = 'L',
            FileSystemType = "ext4",
            UseNoLoadJournal = true,
            DistroName = "Ubuntu"
        };

        MountOperationResult result = await service.MountAsync(req);

        Assert.True(result.Success);
        Assert.Equal("ro,noload", result.Session?.MountOptions);
        Assert.DoesNotContain(runner.RecordedCalls, c => c.Contains("--unmount"));
        Assert.DoesNotContain(runner.RecordedCalls, c => c.Contains("sh -c"));
        Assert.Contains(runner.RecordedCalls, c =>
            c.Contains("-- mount") && c.Contains("/dev/sdc1") && c.Contains("/mnt/wsl/wslmountdrive_d2p1"));
    }

    [Fact]
    public async Task ReadOnlyNbdServer_HandlesReadWriteFlushAndDisconnectRequests()
    {
        const long partitionOffset = 4096;
        const long partitionLength = 8192;
        byte[] backing = new byte[partitionOffset + partitionLength];
        Encoding.ASCII.GetBytes("HELLO_EXT4_DATA").CopyTo(backing, (int)partitionOffset + 512);
        using var diskStream = new MemoryStream(backing, writable: false);

        using var input = new MemoryStream();
        void AppendReq(ushort type, ulong handle, ulong offset, uint len, byte[]? writePayload = null)
        {
            byte[] hdr = new byte[28];
            BinaryPrimitives.WriteUInt32BigEndian(hdr.AsSpan(0, 4), ReadOnlyNbdServer.RequestMagic);
            BinaryPrimitives.WriteUInt16BigEndian(hdr.AsSpan(4, 2), 0);
            BinaryPrimitives.WriteUInt16BigEndian(hdr.AsSpan(6, 2), type);
            BinaryPrimitives.WriteUInt64BigEndian(hdr.AsSpan(8, 8), handle);
            BinaryPrimitives.WriteUInt64BigEndian(hdr.AsSpan(16, 8), offset);
            BinaryPrimitives.WriteUInt32BigEndian(hdr.AsSpan(24, 4), len);
            input.Write(hdr);
            if (writePayload is not null)
            {
                input.Write(writePayload);
            }
        }

        AppendReq(ReadOnlyNbdServer.CmdRead, handle: 11, offset: 512, len: 15);
        AppendReq(ReadOnlyNbdServer.CmdWrite, handle: 22, offset: 0, len: 4, writePayload: [1, 2, 3, 4]);
        AppendReq(ReadOnlyNbdServer.CmdFlush, handle: 33, offset: 0, len: 0);
        AppendReq(ReadOnlyNbdServer.CmdDisc, handle: 44, offset: 0, len: 0);
        input.Position = 0;

        using var output = new MemoryStream();
        await ReadOnlyNbdServer.RunStreamLoopAsync(input, output, diskStream, partitionOffset, partitionLength);

        byte[] outBytes = output.ToArray();
        // Reply 1: READ (16 byte header + 15 bytes payload)
        Assert.Equal(ReadOnlyNbdServer.ReplyMagic, BinaryPrimitives.ReadUInt32BigEndian(outBytes.AsSpan(0, 4)));
        Assert.Equal(ReadOnlyNbdServer.ErrOk, BinaryPrimitives.ReadUInt32BigEndian(outBytes.AsSpan(4, 4)));
        Assert.Equal(11UL, BinaryPrimitives.ReadUInt64BigEndian(outBytes.AsSpan(8, 8)));
        Assert.Equal("HELLO_EXT4_DATA", Encoding.ASCII.GetString(outBytes, 16, 15));

        // Reply 2: WRITE -> ErrRoFs (16 byte header)
        int r2 = 31;
        Assert.Equal(ReadOnlyNbdServer.ReplyMagic, BinaryPrimitives.ReadUInt32BigEndian(outBytes.AsSpan(r2, 4)));
        Assert.Equal(ReadOnlyNbdServer.ErrRoFs, BinaryPrimitives.ReadUInt32BigEndian(outBytes.AsSpan(r2 + 4, 4)));
        Assert.Equal(22UL, BinaryPrimitives.ReadUInt64BigEndian(outBytes.AsSpan(r2 + 8, 8)));

        // Reply 3: FLUSH -> ErrOk (16 byte header)
        int r3 = r2 + 16;
        Assert.Equal(ReadOnlyNbdServer.ReplyMagic, BinaryPrimitives.ReadUInt32BigEndian(outBytes.AsSpan(r3, 4)));
        Assert.Equal(ReadOnlyNbdServer.ErrOk, BinaryPrimitives.ReadUInt32BigEndian(outBytes.AsSpan(r3 + 4, 4)));
        Assert.Equal(33UL, BinaryPrimitives.ReadUInt64BigEndian(outBytes.AsSpan(r3 + 8, 8)));
        Assert.Equal(r3 + 16, outBytes.Length);
    }

    [Fact]
    public async Task ReadOnlyNbdServer_RejectsOversizedWriteAndStopsLoop()
    {
        using var diskStream = new MemoryStream(new byte[4096], writable: false);
        using var input = new MemoryStream();

        byte[] hdr = new byte[28];
        BinaryPrimitives.WriteUInt32BigEndian(hdr.AsSpan(0, 4), ReadOnlyNbdServer.RequestMagic);
        BinaryPrimitives.WriteUInt16BigEndian(hdr.AsSpan(4, 2), 0);
        BinaryPrimitives.WriteUInt16BigEndian(hdr.AsSpan(6, 2), ReadOnlyNbdServer.CmdWrite);
        BinaryPrimitives.WriteUInt64BigEndian(hdr.AsSpan(8, 8), 99UL);
        BinaryPrimitives.WriteUInt64BigEndian(hdr.AsSpan(16, 8), 0UL);
        BinaryPrimitives.WriteUInt32BigEndian(hdr.AsSpan(24, 4), (64U * 1024U * 1024U) + 1U);
        input.Write(hdr);
        input.Position = 0;

        using var output = new MemoryStream();
        await ReadOnlyNbdServer.RunStreamLoopAsync(input, output, diskStream, 0, 4096);

        byte[] outBytes = output.ToArray();
        Assert.Equal(16, outBytes.Length);
        Assert.Equal(ReadOnlyNbdServer.ReplyMagic, BinaryPrimitives.ReadUInt32BigEndian(outBytes.AsSpan(0, 4)));
        Assert.Equal(ReadOnlyNbdServer.ErrInvalid, BinaryPrimitives.ReadUInt32BigEndian(outBytes.AsSpan(4, 4)));
        Assert.Equal(99UL, BinaryPrimitives.ReadUInt64BigEndian(outBytes.AsSpan(8, 8)));
    }

    [Fact]
    public async Task MountAsync_FallsBackToNbdBridge_WhenSharingViolationOrForceNbdBridge()
    {
        var runner = new SharingViolationWslRunner();
        var nbdProvider = new FakeNbdBridgeProvider("/dev/nbd0");
        var service = new WslMountService(
            runner,
            (letter, unc) => new DriveMappingResult(true, $"{letter}:", $"Mapped {letter}: -> {unc}", true),
            nbdProvider);

        var req = new MountRequest
        {
            DiskIndex = 0,
            PartitionNumber = 3,
            DriveLetter = 'L',
            FileSystemType = "ext4",
            UseNoLoadJournal = true,
            DistroName = "Ubuntu",
            PartitionOffsetBytes = 1048576,
            PartitionSizeBytes = 64L * 1024 * 1024,
            SectorSize = 512,
            ForceNbdBridge = false
        };

        MountOperationResult result = await service.MountAsync(req);

        Assert.True(result.Success);
        Assert.Equal("/dev/nbd0", result.Session?.SourceDeviceInWsl);
        Assert.Equal("ro,noload", result.Session?.MountOptions);
        Assert.Equal(1, nbdProvider.AttachCallCount);
        Assert.DoesNotContain(runner.RecordedCalls, c => c.Contains("sh -c"));
        Assert.Contains(runner.RecordedCalls, c =>
            c.Contains("-- mount") && c.Contains("/dev/nbd0") && c.Contains("/mnt/wsl/wslmountdrive_d0p3"));
    }

    [Fact]
    public async Task UnmountAsync_DisconnectsNbdDevice_WhenSessionUsesNbd()
    {
        var runner = new MultiPartitionWslRunner("/dev/nbd0 /mnt/wsl/wslmountdrive_d0p3 ext4 ro,relatime,noload 0 0\n");
        var service = new WslMountService(runner);

        UnmountOperationResult result = await service.UnmountAsync(
            driveLetter: null,
            diskIndex: 0,
            vhdPath: null,
            distroName: "Ubuntu",
            mountName: "wslmountdrive_d0p3");

        Assert.True(result.Success);
        Assert.Contains(runner.RecordedCalls, c => c == "-d Ubuntu -u root -- umount /mnt/wsl/wslmountdrive_d0p3");
        Assert.Contains(runner.RecordedCalls, c => c.Contains("python3") && c.Contains("/dev/nbd0"));
        Assert.DoesNotContain(runner.RecordedCalls, c => c.Contains("--unmount"));
    }

    [Fact]
    public void BuildMountArguments_ReadWrite_UsesRwOption()
    {
        var req = new MountRequest
        {
            DiskIndex = 1,
            PartitionNumber = 2,
            DriveLetter = 'L',
            FileSystemType = "ext4",
            ReadWrite = true,
            UseNoLoadJournal = true
        };

        List<string> args = WslMountService.BuildMountArguments(req);
        Assert.Equal(
            ["--mount", @"\\.\PhysicalDrive1", "--partition", "2", "--name", "wslmountdrive_d1p2", "--type", "ext4", "--options", "rw"],
            args);
    }

    [Fact]
    public async Task MountAsync_ReadWrite_MountsWithRwAndSetsIsReadOnlyFalse()
    {
        var runner = new FakeWslRunner();
        var service = new WslMountService(
            runner,
            (letter, unc) => new DriveMappingResult(true, $"{letter}:", $"Mapped {letter}: -> {unc}", true));

        var req = new MountRequest
        {
            DiskIndex = 1,
            PartitionNumber = 2,
            DriveLetter = 'L',
            FileSystemType = "ext4",
            ReadWrite = true,
            UseNoLoadJournal = true
        };

        MountOperationResult result = await service.MountAsync(req);

        Assert.True(result.Success);
        Assert.NotNull(result.Session);
        Assert.False(result.Session.IsReadOnly);
        Assert.Equal("RW", result.Session.AccessModeDisplay);
        Assert.Equal("rw", result.Session.MountOptions);
        Assert.Contains(runner.RecordedCalls, c => c.Contains("--options rw"));
    }

    [Fact]
    public async Task MountAsync_ReadWrite_OnNbdBridge_ReturnsFailure()
    {
        var runner = new FakeWslRunner();
        var nbdProvider = new FakeNbdBridgeProvider("/dev/nbd0");
        var service = new WslMountService(
            runner,
            (letter, unc) => new DriveMappingResult(true, $"{letter}:", $"Mapped {letter}: -> {unc}", true),
            nbdProvider);

        var req = new MountRequest
        {
            DiskIndex = 0,
            PartitionNumber = 3,
            DriveLetter = 'L',
            FileSystemType = "ext4",
            ReadWrite = true,
            ForceNbdBridge = true
        };

        MountOperationResult result = await service.MountAsync(req);

        Assert.False(result.Success);
        Assert.Contains("Read-Only only", result.Message);
        Assert.Equal(0, nbdProvider.AttachCallCount);
    }

    [Fact]
    public async Task MountAsync_AlreadyMountedWithDifferentMode_ReturnsFailure()
    {
        var runner = new MultiPartitionWslRunner(
            "/dev/sdc1 /mnt/wsl/wslmountdrive_d2p1 ext4 ro,relatime,noload 0 0\n",
            failMountWithAlreadyMounted: true);
        var service = new WslMountService(
            runner,
            (letter, unc) => new DriveMappingResult(true, $"{letter}:", $"Mapped {letter}: -> {unc}", true));

        var req = new MountRequest
        {
            DiskIndex = 2,
            PartitionNumber = 1,
            DriveLetter = 'L',
            FileSystemType = "ext4",
            ReadWrite = true,
            DistroName = "Ubuntu"
        };

        MountOperationResult result = await service.MountAsync(req);

        Assert.False(result.Success);
        Assert.Contains("already mounted as RO", result.Message);
    }

    [Fact]
    public void ParseProcMounts_DetectsReadOnlyAndReadWriteModes()
    {
        const string procMounts = """
            /dev/sdd1 /mnt/wsl/wslmountdrive_d1p1 ext4 ro,relatime,noload 0 0
            /dev/sdd2 /mnt/wsl/wslmountdrive_d1p2 ext4 rw,relatime 0 0
            """;

        List<MountSession> sessions = WslMountService.ParseProcMounts(
            procMounts,
            "Ubuntu",
            new Dictionary<string, string>());

        Assert.Equal(2, sessions.Count);
        Assert.True(sessions[0].IsReadOnly);
        Assert.Equal("RO", sessions[0].AccessModeDisplay);
        Assert.True(sessions[1].IsReadOnly == false);
        Assert.Equal("RW", sessions[1].AccessModeDisplay);
    }

    [Theory]
    [InlineData("ext4;id>/tmp/pwned;#")]
    [InlineData("-o rw")]
    [InlineData("ext4 && rm -rf /")]
    public async Task MountAsync_RejectsUnsafeFileSystemType(string unsafeFsType)
    {
        var runner = new FakeWslRunner();
        var service = new WslMountService(runner);

        var req = new MountRequest
        {
            DiskIndex = 1,
            PartitionNumber = 1,
            DriveLetter = 'L',
            FileSystemType = unsafeFsType,
            DistroName = "Ubuntu"
        };

        MountOperationResult result = await service.MountAsync(req);

        Assert.False(result.Success);
        Assert.Contains("Invalid filesystem type", result.Message);
        Assert.Empty(runner.RecordedCalls);
        Assert.Throws<ArgumentException>(() => WslMountService.BuildMountArguments(req));
    }

    [Fact]
    public async Task MountAsync_RollbackOnMappingFailure_OnlyUmountsSiblingPartition()
    {
        var runner = new MultiPartitionWslRunner(
            "/dev/sdc2 /mnt/wsl/wslmountdrive_d2p2 ext4 ro,relatime,noload 0 0\n",
            failMountWithAlreadyMounted: true);
        var service = new WslMountService(
            runner,
            (letter, _) => new DriveMappingResult(false, $"{letter}:", "Simulated mapping failure", false));

        var req = new MountRequest
        {
            DiskIndex = 2,
            PartitionNumber = 1,
            DriveLetter = 'L',
            FileSystemType = "ext4",
            DistroName = "Ubuntu"
        };

        MountOperationResult result = await service.MountAsync(req);

        Assert.False(result.Success);
        Assert.DoesNotContain(runner.RecordedCalls, c => c.Contains("--unmount"));
        Assert.Contains(runner.RecordedCalls, c => c == "-d Ubuntu -u root -- umount /mnt/wsl/wslmountdrive_d2p1");
    }

    [Fact]
    public async Task MountAsync_RollbackOnMappingFailure_DoesNotUnmountReusedMount()
    {
        var runner = new MultiPartitionWslRunner(
            "/dev/sdc1 /mnt/wsl/wslmountdrive_d2p1 ext4 ro,relatime,noload 0 0\n",
            failMountWithAlreadyMounted: true);
        var service = new WslMountService(
            runner,
            (letter, _) => new DriveMappingResult(false, $"{letter}:", "Simulated mapping failure", false));

        var req = new MountRequest
        {
            DiskIndex = 2,
            PartitionNumber = 1,
            DriveLetter = 'L',
            FileSystemType = "ext4",
            DistroName = "Ubuntu"
        };

        MountOperationResult result = await service.MountAsync(req);

        Assert.False(result.Success);
        Assert.DoesNotContain(runner.RecordedCalls, c => c.Contains("--unmount"));
        Assert.DoesNotContain(runner.RecordedCalls, c => c.Contains("umount /mnt/wsl/wslmountdrive_d2p1"));
    }

    private sealed class FakeNbdBridgeProvider(string nbdDevicePath) : INbdBridgeProvider
    {
        public int AttachCallCount { get; private set; }

        public Task<NbdBridgeAttachment> AttachPartitionAsync(
            int diskIndex,
            long partitionOffset,
            long partitionLength,
            int sectorSize,
            string distro,
            Action<string>? log = null,
            CancellationToken cancellationToken = default)
        {
            AttachCallCount++;
            return Task.FromResult(new NbdBridgeAttachment(true, nbdDevicePath, string.Empty, null));
        }
    }

    private sealed class SharingViolationWslRunner : IWslCommandRunner
    {
        public List<string> RecordedCalls { get; } = [];

        public Task<WslCommandResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
        {
            RecordedCalls.Add(string.Join(' ', arguments));

            if (arguments.Contains("--mount"))
            {
                return Task.FromResult(new WslCommandResult(
                    -1,
                    "エラー コード: Wsl/Service/AttachDisk/ERROR_SHARING_VIOLATION (0x80070020)",
                    string.Empty));
            }

            if (arguments.Contains("/proc/mounts"))
            {
                return Task.FromResult(new WslCommandResult(0, "none /mnt/wsl tmpfs rw,relatime 0 0\n", string.Empty));
            }

            return Task.FromResult(new WslCommandResult(0, string.Empty, string.Empty));
        }
    }

    private sealed class MultiPartitionWslRunner(string procMountsOutput, bool failMountWithAlreadyMounted = false) : IWslCommandRunner
    {
        public List<string> RecordedCalls { get; } = [];

        public Task<WslCommandResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
        {
            string joined = string.Join(' ', arguments);
            RecordedCalls.Add(joined);

            if (arguments.Contains("/proc/mounts"))
            {
                return Task.FromResult(new WslCommandResult(0, procMountsOutput, string.Empty));
            }

            if (arguments.Contains("mountpoint"))
            {
                return Task.FromResult(new WslCommandResult(1, string.Empty, string.Empty));
            }

            if (failMountWithAlreadyMounted && arguments.Contains("--mount"))
            {
                return Task.FromResult(new WslCommandResult(
                    -1,
                    "エラー コード: Wsl/Service/DetachDisk/WSL_E_DISK_ALREADY_MOUNTED",
                    string.Empty));
            }

            return Task.FromResult(new WslCommandResult(0, string.Empty, string.Empty));
        }
    }

    private sealed class FakeWslRunner : IWslCommandRunner
    {
        public List<string> RecordedCalls { get; } = [];

        public Task<WslCommandResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
        {
            string joined = string.Join(' ', arguments);
            RecordedCalls.Add(joined);

            if (arguments.Contains("--verbose"))
            {
                return Task.FromResult(new WslCommandResult(0, "  NAME STATE VERSION\n* Ubuntu Running 2\n", string.Empty));
            }

            if (arguments.Contains("--mount") && arguments.Contains("ro,noload"))
            {
                return Task.FromResult(new WslCommandResult(1, string.Empty, "mount: invalid option noload"));
            }

            return Task.FromResult(new WslCommandResult(0, string.Empty, string.Empty));
        }
    }

    private sealed class StaleMountWslRunner : IWslCommandRunner
    {
        private bool _wasUnmounted;
        public List<string> RecordedCalls { get; } = [];

        public Task<WslCommandResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
        {
            string joined = string.Join(' ', arguments);
            RecordedCalls.Add(joined);

            if (arguments.Contains("/proc/mounts"))
            {
                return Task.FromResult(new WslCommandResult(0, "none /mnt/wsl tmpfs rw,relatime 0 0\n", string.Empty));
            }

            if (arguments.Contains("--unmount"))
            {
                _wasUnmounted = true;
                return Task.FromResult(new WslCommandResult(0, string.Empty, string.Empty));
            }

            if (arguments.Contains("--mount") && !_wasUnmounted)
            {
                return Task.FromResult(new WslCommandResult(
                    -1,
                    "そのボリュームは WSL2 内で既にマウントされています。\r\nエラー コード: Wsl/Service/DetachDisk/WSL_E_DISK_ALREADY_MOUNTED",
                    string.Empty));
            }

            return Task.FromResult(new WslCommandResult(0, string.Empty, string.Empty));
        }
    }
}
