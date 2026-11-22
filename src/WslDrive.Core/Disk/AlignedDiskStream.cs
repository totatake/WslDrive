using System.Buffers;
using Microsoft.Win32.SafeHandles;

namespace WslDrive.Core.Disk;

/// <summary>
/// Strictly read-only, sector-aligned stream wrapper with an in-memory block cache.
/// Ensures raw Windows disk handles (\\.\PhysicalDriveX) are always read on aligned boundaries.
/// </summary>
public sealed class AlignedDiskStream : Stream
{
    private readonly SafeFileHandle? _handle;
    private readonly Stream? _innerStream;
    private readonly bool _leaveOpen;
    private readonly long _length;
    private readonly int _alignment;
    private readonly Dictionary<long, (byte[] Data, int ValidBytes)> _blockCache = new();
    private readonly Queue<long> _cacheOrder = new();
    private const int MaxCachedBlocks = 128;

    private long _position;
    private bool _disposed;

    public AlignedDiskStream(SafeFileHandle handle, long length, int alignment = 4096, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(handle);
        if (alignment <= 0 || (alignment & (alignment - 1)) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(alignment), "Alignment must be a positive power of two.");
        }

        _handle = handle;
        _length = Math.Max(0, length);
        _alignment = alignment;
        _leaveOpen = leaveOpen;
    }

    public AlignedDiskStream(Stream innerStream, long? length = null, int alignment = 4096, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(innerStream);
        if (!innerStream.CanRead || !innerStream.CanSeek)
        {
            throw new ArgumentException("Inner stream must be readable and seekable.", nameof(innerStream));
        }
        if (alignment <= 0 || (alignment & (alignment - 1)) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(alignment), "Alignment must be a positive power of two.");
        }

        _innerStream = innerStream;
        _length = length ?? innerStream.Length;
        _alignment = alignment;
        _leaveOpen = leaveOpen;
    }

    public override bool CanRead => !_disposed;
    public override bool CanSeek => !_disposed;
    public override bool CanWrite => false;
    public override long Length => _length;

    public override long Position
    {
        get => _position;
        set
        {
            if (value < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value), "Position cannot be negative.");
            }
            _position = value;
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        return Read(buffer.AsSpan(offset, count));
    }

    public override int Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (buffer.IsEmpty || _position >= _length)
        {
            return 0;
        }

        int maxToRead = (int)Math.Min(buffer.Length, _length - _position);
        int totalRead = 0;

        while (totalRead < maxToRead)
        {
            long currentPos = _position + totalRead;
            long blockOffset = (currentPos / _alignment) * _alignment;
            int offsetInBlock = (int)(currentPos - blockOffset);

            byte[] block = ReadAlignedBlock(blockOffset, out int validBytesInBlock);
            if (validBytesInBlock <= offsetInBlock)
            {
                break;
            }

            int availableInBlock = validBytesInBlock - offsetInBlock;
            int toCopy = Math.Min(maxToRead - totalRead, availableInBlock);
            block.AsSpan(offsetInBlock, toCopy).CopyTo(buffer.Slice(totalRead, toCopy));
            totalRead += toCopy;
        }

        _position += totalRead;
        return totalRead;
    }

    private byte[] ReadAlignedBlock(long blockOffset, out int validBytes)
    {
        if (_blockCache.TryGetValue(blockOffset, out var cached))
        {
            validBytes = cached.ValidBytes;
            return cached.Data;
        }

        byte[] block = new byte[_alignment];
        int bytesRead = ReadFromUnderlyingAligned(blockOffset, block);

        if (bytesRead > 0)
        {
            if (_blockCache.Count >= MaxCachedBlocks && _cacheOrder.Count > 0)
            {
                long oldest = _cacheOrder.Dequeue();
                _blockCache.Remove(oldest);
            }
            _blockCache[blockOffset] = (block, bytesRead);
            _cacheOrder.Enqueue(blockOffset);
        }

        validBytes = bytesRead;
        return block;
    }

    private int ReadFromUnderlyingAligned(long blockOffset, byte[] destination)
    {
        if (_handle is not null)
        {
            int total = 0;
            while (total < destination.Length)
            {
                int read = RandomAccess.Read(_handle, destination.AsSpan(total), blockOffset + total);
                if (read == 0)
                {
                    break;
                }
                total += read;
            }
            return total;
        }

        if (_innerStream is not null)
        {
            _innerStream.Position = blockOffset;
            int total = 0;
            while (total < destination.Length)
            {
                int read = _innerStream.Read(destination, total, destination.Length - total);
                if (read == 0)
                {
                    break;
                }
                total += read;
            }
            return total;
        }

        return 0;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        long target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => _length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };

        if (target < 0)
        {
            throw new IOException("Attempted to seek before the beginning of the stream.");
        }

        _position = target;
        return _position;
    }

    public override void Flush() { }

    public override void SetLength(long value) =>
        throw new NotSupportedException("AlignedDiskStream is strictly read-only.");

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException("AlignedDiskStream is strictly read-only.");

    protected override void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        if (disposing && !_leaveOpen)
        {
            _handle?.Dispose();
            _innerStream?.Dispose();
        }

        _blockCache.Clear();
        _cacheOrder.Clear();
        _disposed = true;
        base.Dispose(disposing);
    }
}
