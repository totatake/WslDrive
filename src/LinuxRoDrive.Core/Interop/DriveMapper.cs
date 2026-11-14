using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace LinuxRoDrive.Core.Interop;

public sealed record DriveMappingResult(bool Success, string DriveLetter, string Message, bool MappedInUserSession);

/// <summary>
/// Maps and unmaps Windows drive letters (e.g. L:) to WSL UNC paths (\\wsl.localhost\Distro\mnt\wsl\...),
/// using DOS device symbolic links (<c>DefineDosDeviceW</c>) and UAC split-token impersonation so
/// both elevated terminals and non-elevated Windows Explorer immediately see the drive.
/// </summary>
public static class DriveMapper
{
    private static readonly char[] PreferredOrder =
        ['L', 'M', 'N', 'O', 'P', 'R', 'S', 'T', 'U', 'V', 'W', 'X', 'Y', 'Z', 'E', 'F', 'G', 'H', 'I', 'J', 'K', 'Q', 'D'];

    public static List<char> GetAvailableDriveLetters()
    {
        var used = new HashSet<char>();

        if (OperatingSystem.IsWindows())
        {
            foreach (string drive in Environment.GetLogicalDrives())
            {
                if (!string.IsNullOrEmpty(drive))
                {
                    used.Add(char.ToUpperInvariant(drive[0]));
                }
            }

            char[] buffer = new char[1024];
            for (char c = 'A'; c <= 'Z'; c++)
            {
                string local = $"{c}:";
                if (NativeMethods.QueryDosDeviceW(local, buffer, (uint)buffer.Length) > 0)
                {
                    used.Add(c);
                }
            }

            if (IsElevated())
            {
                _ = TryRunAsDesktopUser(() =>
                {
                    char[] userBuf = new char[1024];
                    for (char c = 'A'; c <= 'Z'; c++)
                    {
                        string local = $"{c}:";
                        if (NativeMethods.QueryDosDeviceW(local, userBuf, (uint)userBuf.Length) > 0)
                        {
                            used.Add(c);
                        }
                    }
                    return true;
                }, out _, out _);
            }

            foreach (string mapped in GetMappedNetworkDrives().Keys)
            {
                if (!string.IsNullOrEmpty(mapped))
                {
                    used.Add(char.ToUpperInvariant(mapped[0]));
                }
            }
        }

        var available = new List<char>();
        foreach (char c in PreferredOrder)
        {
            if (!used.Contains(c))
            {
                available.Add(c);
            }
        }
        return available;
    }

    public static char NormalizeDriveLetter(string? driveLetter)
    {
        if (string.IsNullOrWhiteSpace(driveLetter))
        {
            throw new ArgumentException("Drive letter cannot be empty.", nameof(driveLetter));
        }

        char letter = char.ToUpperInvariant(driveLetter.Trim()[0]);
        if (letter is < 'A' or > 'Z')
        {
            throw new ArgumentException($"Invalid drive letter '{driveLetter}'.", nameof(driveLetter));
        }
        return letter;
    }

    public static Dictionary<string, string> GetMappedNetworkDrives()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!OperatingSystem.IsWindows())
        {
            return result;
        }

        for (char c = 'A'; c <= 'Z'; c++)
        {
            string local = $"{c}:";
            if (TryGetMappedUnc(local, out string? remote) && !string.IsNullOrEmpty(remote))
            {
                result[local] = remote;
            }
        }

        if (IsElevated())
        {
            _ = TryRunAsDesktopUser(() =>
            {
                for (char c = 'A'; c <= 'Z'; c++)
                {
                    string local = $"{c}:";
                    if (!result.ContainsKey(local) &&
                        TryGetMappedUnc(local, out string? remote) &&
                        !string.IsNullOrEmpty(remote))
                    {
                        result[local] = remote;
                    }
                }
                return true;
            }, out _, out _);
        }

        return result;
    }

    /// <summary>
    /// Parses a raw NT target path returned by <c>QueryDosDeviceW</c> (such as
    /// <c>\??\UNC\wsl.localhost\Ubuntu\mnt\wsl\linuxro_d2p2</c> or
    /// <c>\Device\P9Rdr\;L:0000000000012345\wsl.localhost\Ubuntu</c>) into a standard Win32 UNC path.
    /// </summary>
    public static bool TryParseDosDeviceUncTarget(string? rawTarget, out string? uncPath)
    {
        uncPath = null;
        if (string.IsNullOrWhiteSpace(rawTarget))
        {
            return false;
        }

        string trimmed = rawTarget.TrimEnd('\0').Trim();

        const string dosUncPrefix = @"\??\UNC\";
        if (trimmed.StartsWith(dosUncPrefix, StringComparison.OrdinalIgnoreCase) &&
            trimmed.Length > dosUncPrefix.Length)
        {
            uncPath = @"\\" + trimmed[dosUncPrefix.Length..];
            return true;
        }

        const string globalUncPrefix = @"\GLOBAL??\UNC\";
        if (trimmed.StartsWith(globalUncPrefix, StringComparison.OrdinalIgnoreCase) &&
            trimmed.Length > globalUncPrefix.Length)
        {
            uncPath = @"\\" + trimmed[globalUncPrefix.Length..];
            return true;
        }

        ReadOnlySpan<string> redirectorPrefixes =
        [
            @"\Device\P9Rdr\",
            @"\Device\LanmanRedirector\",
            @"\Device\Mup\"
        ];

        foreach (string prefix in redirectorPrefixes)
        {
            if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                trimmed.Length > prefix.Length)
            {
                string remainder = trimmed[prefix.Length..];
                if (remainder.StartsWith(';'))
                {
                    int slashIdx = remainder.IndexOf('\\');
                    if (slashIdx < 0 || slashIdx + 1 >= remainder.Length)
                    {
                        return false;
                    }
                    remainder = remainder[(slashIdx + 1)..];
                }

                uncPath = @"\\" + remainder.TrimStart('\\');
                return true;
            }
        }

        return false;
    }

    public static DriveMappingResult MapDriveLetter(char letter, string uncPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new DriveMappingResult(false, $"{char.ToUpperInvariant(letter)}:", "Windows OS is required for drive mapping.", false);
        }

        char normalized = char.ToUpperInvariant(letter);
        if (normalized is < 'A' or > 'Z')
        {
            return new DriveMappingResult(false, $"{normalized}:", $"Invalid drive letter '{letter}'.", false);
        }

        if (string.IsNullOrWhiteSpace(uncPath))
        {
            return new DriveMappingResult(false, $"{normalized}:", "UNC path cannot be empty.", false);
        }

        string localName = $"{normalized}:";
        bool mappedInUserSession = false;
        string? userSessionError = null;

        // Windows's Plan 9 Network Provider (p9np.dll!NPAddConnection3) rejects UNC paths with more than
        // 2 components (\\wsl.localhost\<Distro>), causing WNetAddConnection2 / `net use` to fall back to SMB
        // and fail with Win32 error 64 (ERROR_NETNAME_DELETED) on subdirectories like \mnt\wsl\<name>.
        // DefineDosDeviceW(0, "L:", uncPath) creates `\??\UNC\wsl.localhost\...` in DosDevices, which routes
        // directly through MUP -> P9Rdr and supports arbitrary subdirectory mounts.
        if (IsElevated())
        {
            if (TryRunAsDesktopUser(
                    () => NativeMethods.DefineDosDeviceW(0, localName, uncPath),
                    out int userWin32Err,
                    out string impError))
            {
                mappedInUserSession = true;
            }
            else
            {
                userSessionError = !string.IsNullOrEmpty(impError)
                    ? impError
                    : $"DefineDosDeviceW in user session failed ({userWin32Err}): {new Win32Exception(userWin32Err).Message}";
            }
        }

        // Also define the DOS device in the current process session so elevated CLI/GUI processes see the drive letter.
        bool currentSessionMapped = NativeMethods.DefineDosDeviceW(0, localName, uncPath);
        int currentErr = currentSessionMapped ? 0 : Marshal.GetLastWin32Error();

        if (!currentSessionMapped && !mappedInUserSession)
        {
            string errText = new Win32Exception(currentErr).Message;
            string detail = userSessionError is not null ? $" (User session: {userSessionError})" : string.Empty;
            return new DriveMappingResult(false, localName, $"DefineDosDeviceW failed ({currentErr}): {errText}{detail}", false);
        }

        NotifyExplorerDriveChange(normalized, added: true);

        string msg = mappedInUserSession
            ? $"Mapped {localName} -> {uncPath} (visible in Explorer user session)."
            : $"Mapped {localName} -> {uncPath}.";

        return new DriveMappingResult(true, localName, msg, mappedInUserSession);
    }

    public static DriveMappingResult UnmapDriveLetter(char letter)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new DriveMappingResult(false, $"{char.ToUpperInvariant(letter)}:", "Windows OS is required.", false);
        }

        char normalized = char.ToUpperInvariant(letter);
        string localName = $"{normalized}:";

        bool currentDosRemoved = NativeMethods.DefineDosDeviceW(NativeMethods.DDD_REMOVE_DEFINITION, localName, null);
        int currentDosErr = currentDosRemoved ? 0 : Marshal.GetLastWin32Error();
        int currentWNetRc = NativeMethods.WNetCancelConnection2W(localName, 0, fForce: true);

        bool userSessionRemoved = false;
        if (IsElevated())
        {
            if (TryRunAsDesktopUser(() =>
                {
                    bool dosOk = NativeMethods.DefineDosDeviceW(NativeMethods.DDD_REMOVE_DEFINITION, localName, null);
                    int wnetRc = NativeMethods.WNetCancelConnection2W(localName, 0, fForce: true);
                    return dosOk || wnetRc == 0;
                }, out _, out _))
            {
                userSessionRemoved = true;
            }
        }

        NotifyExplorerDriveChange(normalized, added: false);

        // Consider unmap successful if removed from either session or if the drive is already not connected (err 2 / 2250).
        if (currentDosRemoved || currentWNetRc == 0 || userSessionRemoved || currentDosErr == 2 || currentWNetRc == 2250)
        {
            return new DriveMappingResult(true, localName, $"Unmapped {localName}.", userSessionRemoved);
        }

        return new DriveMappingResult(
            false,
            localName,
            $"Failed to unmap {localName} (Win32 error {currentDosErr}: {new Win32Exception(currentDosErr).Message}).",
            false);
    }

    private static bool TryGetMappedUnc(string localName, out string? remoteUnc)
    {
        char[] buf = new char[1024];

        // 1. Check DefineDosDeviceW / NT Object Manager symbolic link first (covers \??\UNC\wsl.localhost\... mounts)
        uint chars = NativeMethods.QueryDosDeviceW(localName, buf, (uint)buf.Length);
        if (chars > 0)
        {
            int nul = Array.IndexOf(buf, '\0');
            string rawTarget = nul >= 0 ? new string(buf, 0, nul) : new string(buf, 0, (int)chars);
            if (TryParseDosDeviceUncTarget(rawTarget, out remoteUnc) && !string.IsNullOrEmpty(remoteUnc))
            {
                return true;
            }
        }

        // 2. Fallback to WNetGetConnectionW for traditional SMB/WNet share mappings
        int len = buf.Length;
        int rc = NativeMethods.WNetGetConnectionW(localName, buf, ref len);
        if (rc == 0 && len > 0)
        {
            int nul = Array.IndexOf(buf, '\0');
            remoteUnc = nul >= 0 ? new string(buf, 0, nul) : new string(buf).TrimEnd('\0');
            if (!string.IsNullOrEmpty(remoteUnc))
            {
                return true;
            }
        }

        remoteUnc = null;
        return false;
    }

    private static bool IsElevated()
    {
        if (!OperatingSystem.IsWindows()) return false;
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>
    /// Impersonates the non-elevated desktop shell (<c>explorer.exe</c>) token on the current thread
    /// and executes <paramref name="action"/>. Because <c>csrss.exe</c> (<c>BaseSrvDefineDosDevice</c>)
    /// impersonates the calling thread (<c>NtCurrentThreadEffectiveToken</c>), <c>DefineDosDeviceW</c>
    /// and <c>QueryDosDeviceW</c> operate directly on the interactive user's <c>DosDevices</c> namespace.
    /// </summary>
    private static bool TryRunAsDesktopUser(
        Func<bool> action,
        out int lastWin32Error,
        out string error)
    {
        lastWin32Error = 0;
        error = string.Empty;

        IntPtr shellWnd = NativeMethods.GetShellWindow();
        if (shellWnd == IntPtr.Zero)
        {
            error = "Desktop shell window (explorer.exe) not found.";
            return false;
        }

        _ = NativeMethods.GetWindowThreadProcessId(shellWnd, out uint shellPid);
        if (shellPid == 0)
        {
            error = "Failed to determine explorer.exe process ID.";
            return false;
        }

        IntPtr shellProcess = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, shellPid);
        if (shellProcess == IntPtr.Zero)
        {
            lastWin32Error = Marshal.GetLastWin32Error();
            error = $"OpenProcess failed ({lastWin32Error}).";
            return false;
        }

        try
        {
            if (!NativeMethods.OpenProcessToken(
                    shellProcess,
                    NativeMethods.TOKEN_DUPLICATE | NativeMethods.TOKEN_QUERY | NativeMethods.TOKEN_IMPERSONATE,
                    out SafeFileHandle shellToken))
            {
                lastWin32Error = Marshal.GetLastWin32Error();
                error = $"OpenProcessToken failed ({lastWin32Error}).";
                return false;
            }

            using (shellToken)
            {
                if (!NativeMethods.DuplicateTokenEx(
                        shellToken,
                        NativeMethods.TOKEN_QUERY | NativeMethods.TOKEN_IMPERSONATE | NativeMethods.TOKEN_DUPLICATE,
                        IntPtr.Zero,
                        NativeMethods.SecurityImpersonation,
                        NativeMethods.TokenImpersonation,
                        out SafeFileHandle impersonationToken))
                {
                    lastWin32Error = Marshal.GetLastWin32Error();
                    error = $"DuplicateTokenEx failed ({lastWin32Error}).";
                    return false;
                }

                using (impersonationToken)
                {
                    if (!NativeMethods.ImpersonateLoggedOnUser(impersonationToken))
                    {
                        lastWin32Error = Marshal.GetLastWin32Error();
                        error = $"ImpersonateLoggedOnUser failed ({lastWin32Error}).";
                        return false;
                    }

                    try
                    {
                        bool ok = action();
                        if (!ok)
                        {
                            lastWin32Error = Marshal.GetLastWin32Error();
                        }
                        return ok;
                    }
                    finally
                    {
                        _ = NativeMethods.RevertToSelf();
                    }
                }
            }
        }
        finally
        {
            _ = NativeMethods.CloseHandle(shellProcess);
        }
    }

    private static void NotifyExplorerDriveChange(char driveLetter, bool added)
    {
        string rootPath = $@"{driveLetter}:\";
        IntPtr pathPtr = Marshal.StringToHGlobalUni(rootPath);
        try
        {
            NativeMethods.SHChangeNotify(
                added ? NativeMethods.SHCNE_DRIVEADD : NativeMethods.SHCNE_DRIVEREMOVED,
                NativeMethods.SHCNF_PATHW,
                pathPtr,
                IntPtr.Zero);
            NativeMethods.SendNotifyMessageW(
                NativeMethods.HWND_BROADCAST,
                NativeMethods.WM_SETTINGCHANGE,
                UIntPtr.Zero,
                "Environment");
        }
        catch
        {
            // Best-effort shell notification
        }
        finally
        {
            Marshal.FreeHGlobal(pathPtr);
        }
    }
}
