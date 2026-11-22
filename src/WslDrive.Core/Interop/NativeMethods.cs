using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace WslDrive.Core.Interop;

internal static class NativeMethods
{
    internal const uint GENERIC_READ = 0x80000000;
    internal const uint FILE_SHARE_READ = 0x00000001;
    internal const uint FILE_SHARE_WRITE = 0x00000002;
    internal const uint OPEN_EXISTING = 3;
    internal const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;

    internal const uint IOCTL_DISK_GET_LENGTH_INFO = 0x0007405C;

    internal const uint DDD_RAW_TARGET_PATH = 0x00000001;
    internal const uint DDD_REMOVE_DEFINITION = 0x00000002;
    internal const uint DDD_EXACT_MATCH_ON_REMOVE = 0x00000004;

    internal const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    internal const uint TOKEN_DUPLICATE = 0x0002;
    internal const uint TOKEN_IMPERSONATE = 0x0004;
    internal const uint TOKEN_QUERY = 0x0008;
    internal const uint TOKEN_ASSIGN_PRIMARY = 0x0001;
    internal const uint TOKEN_ADJUST_DEFAULT = 0x0080;
    internal const uint TOKEN_ADJUST_SESSIONID = 0x0100;
    internal const uint TOKEN_ALL_ACCESS = 0xF01FF;

    internal const int SecurityImpersonation = 2;
    internal const int TokenPrimary = 1;
    internal const int TokenImpersonation = 2;

    internal const uint LOGON_WITH_PROFILE = 0x00000001;
    internal const uint CREATE_NO_WINDOW = 0x08000000;
    internal const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;

    internal const int SHCNE_DRIVEADD = 0x00000100;
    internal const int SHCNE_DRIVEREMOVED = 0x00000080;
    internal const uint SHCNF_PATHW = 0x0005;

    internal static readonly IntPtr HWND_BROADCAST = new(0xFFFF);
    internal const uint WM_SETTINGCHANGE = 0x001A;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern SafeFileHandle CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        IntPtr lpInBuffer,
        uint nInBufferSize,
        out long lpOutBuffer,
        uint nOutBufferSize,
        out uint lpBytesReturned,
        IntPtr lpOverlapped);

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    internal static extern int WNetCancelConnection2W(
        string lpName,
        uint dwFlags,
        [MarshalAs(UnmanagedType.Bool)] bool fForce);

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    internal static extern int WNetGetConnectionW(
        string lpLocalName,
        [Out] char[] lpRemoteName,
        ref int lpnLength);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetShellWindow();

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwProcessId);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool OpenProcessToken(IntPtr ProcessHandle, uint DesiredAccess, out SafeFileHandle TokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DuplicateTokenEx(
        SafeFileHandle hExistingToken,
        uint dwDesiredAccess,
        IntPtr lpTokenAttributes,
        int ImpersonationLevel,
        int TokenType,
        out SafeFileHandle phNewToken);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct STARTUPINFOW
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CreateProcessWithTokenW(
        SafeFileHandle hToken,
        uint dwLogonFlags,
        string? lpApplicationName,
        string? lpCommandLine,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref STARTUPINFOW lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DefineDosDeviceW(
        uint dwFlags,
        string lpDeviceName,
        string? lpTargetPath);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern uint QueryDosDeviceW(
        string? lpDeviceName,
        [Out] char[] lpTargetPath,
        uint ucchMax);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ImpersonateLoggedOnUser(SafeFileHandle hToken);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool RevertToSelf();

    [DllImport("shell32.dll")]
    internal static extern void SHChangeNotify(int wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SendNotifyMessageW(IntPtr hWnd, uint Msg, UIntPtr wParam, string lParam);
}
