using System.Text;
using LinuxRoDrive.Core.Interop;
using LinuxRoDrive.Core.Models;
using LinuxRoDrive.Core.Wsl;
using Xunit;

namespace LinuxRoDrive.Tests;

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
            ["--mount", @"\\.\PhysicalDrive1", "--partition", "2", "--name", "linuxro_d1p2", "--type", "ext4", "--options", "ro,noload"],
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
            /dev/sdd2 /mnt/wsl/linuxro_d1p2 ext4 ro,relatime,noload 0 0
            /dev/sde1 /mnt/wsl/custom\040share btrfs ro 0 0
            /dev/sda / ext4 rw,relatime 0 0
            """;

        var mapped = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["L:"] = @"\\wsl.localhost\Ubuntu\mnt\wsl\linuxro_d1p2"
        };

        List<MountSession> sessions = WslMountService.ParseProcMounts(procMounts, "Ubuntu", mapped);

        Assert.Equal(2, sessions.Count);
        Assert.Equal("linuxro_d1p2", sessions[0].MountName);
        Assert.Equal("L:", sessions[0].MappedDriveLetter);
        Assert.Equal("ext4", sessions[0].FileSystemType);
        Assert.True(sessions[0].IsReadOnly);
        Assert.Equal(@"\\.\PhysicalDrive1", sessions[0].TargetWslDiskPath);

        Assert.Equal("custom share", sessions[1].MountName);
        Assert.Null(sessions[1].MappedDriveLetter);
        Assert.Equal("btrfs", sessions[1].FileSystemType);
        Assert.Null(sessions[1].TargetWslDiskPath);
    }

    [Fact]
    public void TryParseDosDeviceUncTarget_ParsesDosUncAndP9RdrTargets()
    {
        Assert.True(DriveMapper.TryParseDosDeviceUncTarget(
            @"\??\UNC\wsl.localhost\Ubuntu\mnt\wsl\linuxro_d2p2",
            out string? unc1));
        Assert.Equal(@"\\wsl.localhost\Ubuntu\mnt\wsl\linuxro_d2p2", unc1);

        Assert.True(DriveMapper.TryParseDosDeviceUncTarget(
            @"\Device\P9Rdr\;L:0000000000012345\wsl.localhost\Ubuntu\mnt\wsl\linuxro_d2p2",
            out string? unc2));
        Assert.Equal(@"\\wsl.localhost\Ubuntu\mnt\wsl\linuxro_d2p2", unc2);

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
        Assert.Contains(runner.RecordedCalls, c => c.Contains("/mnt/wsl/linuxro_d1p1"));
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
