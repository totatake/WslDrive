using System.Buffers;
using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using LinuxRoDrive.Core.Disk;
using LinuxRoDrive.Core.Interop;
using Microsoft.Win32.SafeHandles;

namespace LinuxRoDrive.Core.Wsl;

public sealed record NbdBridgeAttachment(
    bool Success,
    string NbdDevicePath,
    string ErrorMessage,
    IAsyncDisposable? Session);

public interface INbdBridgeProvider
{
    Task<NbdBridgeAttachment> AttachPartitionAsync(
        int diskIndex,
        long partitionOffset,
        long partitionLength,
        int sectorSize,
        string distro,
        Action<string>? log = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Implements the Linux Network Block Device (NBD) oldstyle data-phase protocol strictly read-only.
/// </summary>
public static class ReadOnlyNbdServer
{
    public const uint RequestMagic = 0x25609513;
    public const uint ReplyMagic = 0x67446698;

    public const ushort CmdRead = 0;
    public const ushort CmdWrite = 1;
    public const ushort CmdDisc = 2;
    public const ushort CmdFlush = 3;
    public const ushort CmdTrim = 4;
    public const ushort CmdCache = 5;
    public const ushort CmdWriteZeroes = 6;

    public const uint ErrOk = 0;
    public const uint ErrIo = 5;
    public const uint ErrInvalid = 22;
    public const uint ErrRoFs = 30;

    private const uint MaxReadLengthBytes = 64 * 1024 * 1024;

    public static async Task RunStreamLoopAsync(
        Stream input,
        Stream output,
        Stream diskStream,
        long partitionOffset,
        long partitionLength,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(diskStream);

        byte[] headerBuf = new byte[28];
        byte[] replyBuf = new byte[16];
        byte[] drainBuf = new byte[65536];

        while (!cancellationToken.IsCancellationRequested)
        {
            bool gotHeader = await ReadExactlyOrEofAsync(input, headerBuf, cancellationToken).ConfigureAwait(false);
            if (!gotHeader)
            {
                break;
            }

            uint magic = BinaryPrimitives.ReadUInt32BigEndian(headerBuf.AsSpan(0, 4));
            if (magic != RequestMagic)
            {
                throw new InvalidDataException($"Invalid NBD request magic: 0x{magic:X8}.");
            }

            _ = BinaryPrimitives.ReadUInt16BigEndian(headerBuf.AsSpan(4, 2)); // flags
            ushort type = BinaryPrimitives.ReadUInt16BigEndian(headerBuf.AsSpan(6, 2));
            ulong handle = BinaryPrimitives.ReadUInt64BigEndian(headerBuf.AsSpan(8, 8));
            ulong offset = BinaryPrimitives.ReadUInt64BigEndian(headerBuf.AsSpan(16, 8));
            uint length = BinaryPrimitives.ReadUInt32BigEndian(headerBuf.AsSpan(24, 4));

            if (type == CmdDisc)
            {
                break;
            }

            if (type == CmdWrite)
            {
                if (length > 0)
                {
                    await DrainExactAsync(input, drainBuf, length, cancellationToken).ConfigureAwait(false);
                }
                await SendReplyHeaderAsync(output, replyBuf, ErrRoFs, handle, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (type == CmdWriteZeroes)
            {
                await SendReplyHeaderAsync(output, replyBuf, ErrRoFs, handle, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (type is CmdFlush or CmdTrim or CmdCache)
            {
                await SendReplyHeaderAsync(output, replyBuf, ErrOk, handle, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (type == CmdRead)
            {
                if (partitionLength < 0 ||
                    offset > (ulong)partitionLength ||
                    length > (ulong)partitionLength - offset ||
                    length > MaxReadLengthBytes)
                {
                    await SendReplyHeaderAsync(output, replyBuf, ErrIo, handle, cancellationToken).ConfigureAwait(false);
                    await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }

                int readLen = (int)length;
                byte[]? rented = readLen > 0 ? ArrayPool<byte>.Shared.Rent(readLen) : null;
                try
                {
                    if (readLen > 0 && rented is not null)
                    {
                        diskStream.Position = checked(partitionOffset + (long)offset);
                        int totalRead = 0;
                        while (totalRead < readLen)
                        {
                            int n = diskStream.Read(rented, totalRead, readLen - totalRead);
                            if (n == 0)
                            {
                                rented.AsSpan(totalRead, readLen - totalRead).Clear();
                                break;
                            }
                            totalRead += n;
                        }
                    }

                    await SendReplyHeaderAsync(output, replyBuf, ErrOk, handle, cancellationToken).ConfigureAwait(false);
                    if (readLen > 0 && rented is not null)
                    {
                        await output.WriteAsync(rented.AsMemory(0, readLen), cancellationToken).ConfigureAwait(false);
                    }
                    await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    if (rented is not null)
                    {
                        ArrayPool<byte>.Shared.Return(rented);
                    }
                }
                continue;
            }

            await SendReplyHeaderAsync(output, replyBuf, ErrInvalid, handle, cancellationToken).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static ValueTask SendReplyHeaderAsync(
        Stream output,
        byte[] replyBuf,
        uint error,
        ulong handle,
        CancellationToken cancellationToken)
    {
        BinaryPrimitives.WriteUInt32BigEndian(replyBuf.AsSpan(0, 4), ReplyMagic);
        BinaryPrimitives.WriteUInt32BigEndian(replyBuf.AsSpan(4, 4), error);
        BinaryPrimitives.WriteUInt64BigEndian(replyBuf.AsSpan(8, 8), handle);
        return output.WriteAsync(replyBuf.AsMemory(0, 16), cancellationToken);
    }

    private static async Task<bool> ReadExactlyOrEofAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), cancellationToken).ConfigureAwait(false);
            if (n == 0)
            {
                if (offset == 0)
                {
                    return false;
                }
                throw new EndOfStreamException("Unexpected EOF while reading NBD request header.");
            }
            offset += n;
        }
        return true;
    }

    private static async Task DrainExactAsync(Stream stream, byte[] scratch, uint count, CancellationToken cancellationToken)
    {
        long remaining = count;
        while (remaining > 0)
        {
            int chunk = (int)Math.Min(remaining, scratch.Length);
            int n = await stream.ReadAsync(scratch.AsMemory(0, chunk), cancellationToken).ConfigureAwait(false);
            if (n == 0)
            {
                throw new EndOfStreamException("Unexpected EOF while draining NBD write payload.");
            }
            remaining -= n;
        }
    }
}

/// <summary>
/// Manages WSL2 kernel NBD device attachment and teardown.
/// </summary>
public static class NbdDiskBridge
{
    public const string ReadyPrefix = "LINUXRO_NBD_READY:";
    public const string ErrorPrefix = "LINUXRO_NBD_ERROR:";

    // Linux uapi/linux/nbd.h _IO(0xab, cmd) ioctl numbers.
    internal const string PythonNbdSetupAndPumpScript = """
import ctypes, os, socket, struct, subprocess, sys, threading, time

size_bytes = int(sys.argv[1])
subprocess.run(['modprobe', 'nbd'], stdout=sys.stderr, stderr=sys.stderr)
for _ in range(50):
    if os.path.exists('/dev/nbd0'):
        break
    time.sleep(0.02)

libc = ctypes.CDLL(None, use_errno=True)
libc.ioctl.argtypes = [ctypes.c_int, ctypes.c_ulong, ctypes.c_ulong]
libc.ioctl.restype = ctypes.c_int

nbd_dev = None
nbd_idx = -1
for i in range(16):
    cand = f'/dev/nbd{i}'
    if os.path.exists(cand) and not os.path.exists(f'/sys/block/nbd{i}/pid'):
        nbd_dev = cand
        nbd_idx = i
        break

if not nbd_dev:
    sys.stderr.write('No free /dev/nbdX device found.\n')
    sys.exit(1)

ksock, psock = socket.socketpair(socket.AF_UNIX, socket.SOCK_STREAM)
fd = os.open(nbd_dev, os.O_RDWR)
blksize = 4096 if (size_bytes % 4096 == 0) else 512
blocks = size_bytes // blksize

for op, arg in ((0xab04, 0), (0xab01, blksize), (0xab07, blocks), (0xab0a, 2), (0xab00, ksock.fileno())):
    if libc.ioctl(fd, op, arg) != 0:
        err = ctypes.get_errno()
        os.close(fd)
        ksock.close()
        psock.close()
        sys.stderr.write(f'NBD ioctl 0x{op:x} failed with errno {err}.\n')
        sys.exit(1)

def do_it():
    try:
        libc.ioctl(fd, 0xab03, 0)
    finally:
        try:
            psock.shutdown(socket.SHUT_RDWR)
        except Exception:
            pass

t = threading.Thread(target=do_it, daemon=True)
t.start()

for _ in range(100):
    if os.path.exists(f'/sys/block/nbd{nbd_idx}/pid') or not t.is_alive():
        break
    time.sleep(0.02)

sys.stdout.buffer.write(f'LINUXRO_NBD_READY:{nbd_dev}\n'.encode('ascii'))
sys.stdout.buffer.flush()

def recv_exact(s, n):
    buf = bytearray()
    while len(buf) < n:
        chunk = s.recv(n - len(buf))
        if not chunk:
            return None
        buf.extend(chunk)
    return bytes(buf)

def read_stdin_exact(n):
    buf = bytearray()
    while len(buf) < n:
        chunk = sys.stdin.buffer.read(n - len(buf))
        if not chunk:
            return None
        buf.extend(chunk)
    return bytes(buf)

try:
    while True:
        hdr = recv_exact(psock, 28)
        if not hdr:
            break
        magic, flags, cmd_type, handle, offset, length = struct.unpack('>IHHQQI', hdr)
        sys.stdout.buffer.write(hdr)
        if cmd_type == 1 and length > 0:
            wdata = recv_exact(psock, length)
            if not wdata:
                break
            sys.stdout.buffer.write(wdata)
        sys.stdout.buffer.flush()
        if cmd_type == 2:
            break
        rep = read_stdin_exact(16)
        if not rep:
            break
        rmagic, rerr, rhandle = struct.unpack('>IIQ', rep)
        psock.sendall(rep)
        if cmd_type == 0 and rerr == 0 and length > 0:
            rdata = read_stdin_exact(length)
            if not rdata:
                break
            psock.sendall(rdata)
finally:
    for op in (0xab08, 0xab05, 0xab04):
        try:
            libc.ioctl(fd, op, 0)
        except Exception:
            pass
    try:
        os.close(fd)
    except Exception:
        pass
    try:
        ksock.close()
    except Exception:
        pass
    try:
        psock.close()
    except Exception:
        pass
""";

    private const string PythonDisconnectScript = """
import ctypes, os, sys
dev = sys.argv[1]
if not os.path.exists(dev):
    sys.exit(0)
libc = ctypes.CDLL(None)
libc.ioctl.argtypes = [ctypes.c_int, ctypes.c_ulong, ctypes.c_ulong]
libc.ioctl.restype = ctypes.c_int
fd = os.open(dev, os.O_RDWR)
try:
    for op in (0xab08, 0xab05, 0xab04):
        libc.ioctl(fd, op, 0)
finally:
    os.close(fd)
""";

    public static async Task<WslCommandResult> DisconnectInWslAsync(
        IWslCommandRunner runner,
        string distro,
        string nbdDevicePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(runner);
        if (string.IsNullOrWhiteSpace(distro) ||
            string.IsNullOrWhiteSpace(nbdDevicePath) ||
            !nbdDevicePath.StartsWith("/dev/nbd", StringComparison.Ordinal))
        {
            return new WslCommandResult(1, string.Empty, $"Invalid NBD device path '{nbdDevicePath}'.");
        }

        return await runner.RunAsync(
            ["-d", distro, "-u", "root", "--", "python3", "-c", PythonDisconnectScript, nbdDevicePath],
            cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// Hosts the NBD bridge inside the current process (used by the GUI and the CLI background daemon).
/// </summary>
public sealed class InProcessNbdBridgeProvider : INbdBridgeProvider
{
    public async Task<NbdBridgeAttachment> AttachPartitionAsync(
        int diskIndex,
        long partitionOffset,
        long partitionLength,
        int sectorSize,
        string distro,
        Action<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        if (partitionLength <= 0)
        {
            return new NbdBridgeAttachment(false, string.Empty, "Invalid partition size for NBD bridge.", null);
        }

        string diskPath = $@"\\.\PhysicalDrive{diskIndex}";
        SafeFileHandle handle = NativeMethods.CreateFileW(
            diskPath,
            NativeMethods.GENERIC_READ,
            NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE,
            IntPtr.Zero,
            NativeMethods.OPEN_EXISTING,
            NativeMethods.FILE_ATTRIBUTE_NORMAL,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            int err = Marshal.GetLastWin32Error();
            return new NbdBridgeAttachment(
                false,
                string.Empty,
                $"Failed to open {diskPath} read-only: {new Win32Exception(err).Message}",
                null);
        }

        AlignedDiskStream? diskStream = null;
        Process? wslProcess = null;
        try
        {
            int alignment = Math.Max(4096, sectorSize > 0 ? sectorSize : 512);
            diskStream = new AlignedDiskStream(handle, checked(partitionOffset + partitionLength), alignment, leaveOpen: false);

            var psi = new ProcessStartInfo
            {
                FileName = "wsl.exe",
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("-d");
            psi.ArgumentList.Add(distro);
            psi.ArgumentList.Add("-u");
            psi.ArgumentList.Add("root");
            psi.ArgumentList.Add("--");
            psi.ArgumentList.Add("python3");
            psi.ArgumentList.Add("-u");
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(NbdDiskBridge.PythonNbdSetupAndPumpScript);
            psi.ArgumentList.Add(partitionLength.ToString());

            wslProcess = new Process { StartInfo = psi };
            wslProcess.Start();

            using var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            handshakeCts.CancelAfter(TimeSpan.FromSeconds(15));

            string? nbdDevicePath = null;
            Stream stdoutBase = wslProcess.StandardOutput.BaseStream;
            while (!handshakeCts.Token.IsCancellationRequested)
            {
                string? line = await ReadLineFromStreamAsync(stdoutBase, handshakeCts.Token).ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }
                if (line.StartsWith(NbdDiskBridge.ReadyPrefix, StringComparison.Ordinal))
                {
                    nbdDevicePath = line[NbdDiskBridge.ReadyPrefix.Length..].Trim();
                    break;
                }
            }

            if (string.IsNullOrWhiteSpace(nbdDevicePath))
            {
                string stderr = await ReadStderrSafeAsync(wslProcess).ConfigureAwait(false);
                CleanupFailedAttach(wslProcess, diskStream);
                return new NbdBridgeAttachment(
                    false,
                    string.Empty,
                    string.IsNullOrWhiteSpace(stderr)
                        ? "Failed to initialize WSL2 NBD device (/dev/nbdX)."
                        : $"WSL2 NBD bridge failed: {stderr}",
                    null);
            }

            var session = new InProcessNbdSession(wslProcess, diskStream, partitionOffset, partitionLength, log);
            log?.Invoke($"Read-Only NBD bridge ready: {diskPath} [+{partitionOffset}, {partitionLength} B] -> {nbdDevicePath}");
            return new NbdBridgeAttachment(true, nbdDevicePath, string.Empty, session);
        }
        catch (Exception ex)
        {
            CleanupFailedAttach(wslProcess, diskStream);
            if (diskStream is null)
            {
                handle.Dispose();
            }
            return new NbdBridgeAttachment(false, string.Empty, $"NBD bridge error: {ex.Message}", null);
        }
    }

    private static async Task<string?> ReadLineFromStreamAsync(Stream stream, CancellationToken cancellationToken)
    {
        // Read byte-by-byte until '\n' so no binary NBD protocol bytes after the handshake line get buffered away.
        var bytes = new List<byte>(64);
        byte[] single = new byte[1];
        while (true)
        {
            int n = await stream.ReadAsync(single.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);
            if (n == 0)
            {
                return bytes.Count == 0 ? null : Encoding.UTF8.GetString(bytes.ToArray()).TrimEnd('\r');
            }
            if (single[0] == (byte)'\n')
            {
                return Encoding.UTF8.GetString(bytes.ToArray()).TrimEnd('\r');
            }
            bytes.Add(single[0]);
        }
    }

    private static async Task<string> ReadStderrSafeAsync(Process process)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            return (await process.StandardError.ReadToEndAsync(cts.Token).ConfigureAwait(false)).Trim();
        }
        catch
        {
            return string.Empty;
        }
    }

    private static void CleanupFailedAttach(Process? process, Stream? diskStream)
    {
        if (process is not null)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // Ignore kill errors during cleanup
            }
            process.Dispose();
        }
        diskStream?.Dispose();
    }
}

/// <summary>
/// Represents a running in-process NBD bridge loop feeding Linux `/dev/nbdX` read requests from <see cref="AlignedDiskStream"/>.
/// </summary>
public sealed class InProcessNbdSession : IAsyncDisposable
{
    private readonly Process _process;
    private readonly AlignedDiskStream _diskStream;
    private readonly CancellationTokenSource _cts = new();

    public Task Completion { get; }

    internal InProcessNbdSession(
        Process process,
        AlignedDiskStream diskStream,
        long partitionOffset,
        long partitionLength,
        Action<string>? log)
    {
        _process = process;
        _diskStream = diskStream;

        Completion = Task.Run(async () =>
        {
            try
            {
                // Drain stderr in background so the child process never blocks on stderr pipe.
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _process.StandardError.ReadToEndAsync(_cts.Token).ConfigureAwait(false);
                    }
                    catch
                    {
                        // Ignore
                    }
                });

                await ReadOnlyNbdServer.RunStreamLoopAsync(
                    _process.StandardOutput.BaseStream,
                    _process.StandardInput.BaseStream,
                    _diskStream,
                    partitionOffset,
                    partitionLength,
                    _cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected when torn down early
            }
            catch (Exception ex)
            {
                log?.Invoke($"NBD bridge loop ended: {ex.Message}");
            }
            finally
            {
                try
                {
                    _process.StandardInput.Close();
                }
                catch
                {
                    // Ignore
                }
                _diskStream.Dispose();
                try
                {
                    if (!_process.HasExited)
                    {
                        using var waitCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                        await _process.WaitForExitAsync(waitCts.Token).ConfigureAwait(false);
                    }
                }
                catch
                {
                    try { _process.Kill(entireProcessTree: true); } catch { }
                }
                _process.Dispose();
                _cts.Dispose();
            }
        });
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            _cts.Cancel();
        }
        catch
        {
            // Ignore if already disposed
        }

        try
        {
            await Completion.ConfigureAwait(false);
        }
        catch
        {
            // Ignore completion errors during disposal
        }
    }
}

/// <summary>
/// Spawns a detached CLI background daemon (`__nbd-daemon`) so CLI `mount` calls can exit immediately
/// while the NBD bridge continues serving `/dev/nbdX` until unmounted.
/// </summary>
public sealed class DetachedProcessNbdBridgeProvider : INbdBridgeProvider
{
    public async Task<NbdBridgeAttachment> AttachPartitionAsync(
        int diskIndex,
        long partitionOffset,
        long partitionLength,
        int sectorSize,
        string distro,
        Action<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        string? exePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exePath))
        {
            return new NbdBridgeAttachment(false, string.Empty, "Cannot locate executable path to launch background NBD daemon.", null);
        }

        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        if (Path.GetFileNameWithoutExtension(exePath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            string? entryDll = Assembly.GetEntryAssembly()?.Location;
            if (!string.IsNullOrWhiteSpace(entryDll))
            {
                psi.ArgumentList.Add(entryDll);
            }
        }

        psi.ArgumentList.Add("__nbd-daemon");
        psi.ArgumentList.Add("--disk");
        psi.ArgumentList.Add(diskIndex.ToString());
        psi.ArgumentList.Add("--offset");
        psi.ArgumentList.Add(partitionOffset.ToString());
        psi.ArgumentList.Add("--length");
        psi.ArgumentList.Add(partitionLength.ToString());
        psi.ArgumentList.Add("--sector");
        psi.ArgumentList.Add(sectorSize.ToString());
        psi.ArgumentList.Add("--distro");
        psi.ArgumentList.Add(distro);

        var proc = new Process { StartInfo = psi };
        try
        {
            proc.Start();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(20));

            string? line = await proc.StandardOutput.ReadLineAsync(timeoutCts.Token).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(line) && line.StartsWith(NbdDiskBridge.ReadyPrefix, StringComparison.Ordinal))
            {
                string nbdDevicePath = line[NbdDiskBridge.ReadyPrefix.Length..].Trim();
                log?.Invoke($"Started background NBD daemon (PID {proc.Id}) serving {nbdDevicePath}.");
                // Dispose our local Process handle without killing the detached daemon process.
                proc.Dispose();
                return new NbdBridgeAttachment(true, nbdDevicePath, string.Empty, null);
            }

            string errMsg = !string.IsNullOrWhiteSpace(line) && line.StartsWith(NbdDiskBridge.ErrorPrefix, StringComparison.Ordinal)
                ? line[NbdDiskBridge.ErrorPrefix.Length..].Trim()
                : (await proc.StandardError.ReadToEndAsync(timeoutCts.Token).ConfigureAwait(false)).Trim();

            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
            proc.Dispose();

            return new NbdBridgeAttachment(
                false,
                string.Empty,
                string.IsNullOrWhiteSpace(errMsg) ? "Background NBD daemon failed to start." : errMsg,
                null);
        }
        catch (Exception ex)
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
            proc.Dispose();
            return new NbdBridgeAttachment(false, string.Empty, $"Failed to start background NBD daemon: {ex.Message}", null);
        }
    }
}
