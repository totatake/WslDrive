using System.Diagnostics;
using System.Text;

namespace WslMountDrive.Core.Wsl;

public sealed record WslCommandResult(int ExitCode, string StandardOutput, string StandardError);

public interface IWslCommandRunner
{
    Task<WslCommandResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken = default);
}

/// <summary>
/// Executes `wsl.exe` commands and safely decodes both UTF-16LE (WSL management output)
/// and UTF-8 (Linux guest command output) streams.
/// </summary>
public sealed class WslCliRunner : IWslCommandRunner
{
    public async Task<WslCommandResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "wsl.exe",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (string arg in arguments)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = psi };
        process.Start();

        Task<byte[]> stdoutTask = ReadAllBytesAsync(process.StandardOutput.BaseStream, cancellationToken);
        Task<byte[]> stderrTask = ReadAllBytesAsync(process.StandardError.BaseStream, cancellationToken);

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        byte[] stdoutBytes = await stdoutTask.ConfigureAwait(false);
        byte[] stderrBytes = await stderrTask.ConfigureAwait(false);

        return new WslCommandResult(
            process.ExitCode,
            DecodeWslBytes(stdoutBytes),
            DecodeWslBytes(stderrBytes));
    }

    public static string DecodeWslBytes(byte[] bytes)
    {
        if (bytes.Length == 0)
        {
            return string.Empty;
        }

        // UTF-16LE BOM
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2).Trim('\0', '\uFEFF');
        }

        // UTF-8 BOM
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3).Trim('\0', '\uFEFF');
        }

        // Heuristic: wsl.exe Windows-side commands emit UTF-16LE without BOM, so odd bytes are 0x00 for ASCII characters.
        if (bytes.Length % 2 == 0 && bytes.AsSpan().Contains((byte)0))
        {
            return Encoding.Unicode.GetString(bytes).Trim('\0', '\uFEFF');
        }

        return Encoding.UTF8.GetString(bytes).Trim('\0', '\uFEFF');
    }

    private static async Task<byte[]> ReadAllBytesAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms, cancellationToken).ConfigureAwait(false);
        return ms.ToArray();
    }
}
