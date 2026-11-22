using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using WslDrive.Core.Models;

namespace WslDrive.App;

/// <summary>
/// Manages the Windows system tray icon, programmatic icon rendering, and quick-mount context menu.
/// </summary>
public sealed class TrayIconManager : IDisposable
{
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _contextMenu;
    private readonly Icon _trayIcon;
    private readonly IntPtr _iconHandle;

    private readonly Action _onOpenWindow;
    private readonly Action<string> _onOpenDrive;
    private readonly Action<MountSession> _onUnmountSession;
    private readonly Action _onUnmountAllAndExit;
    private readonly Action _onExitOnly;

    private bool _disposed;

    public TrayIconManager(
        Action onOpenWindow,
        Action<string> onOpenDrive,
        Action<MountSession> onUnmountSession,
        Action onUnmountAllAndExit,
        Action onExitOnly)
    {
        _onOpenWindow = onOpenWindow;
        _onOpenDrive = onOpenDrive;
        _onUnmountSession = onUnmountSession;
        _onUnmountAllAndExit = onUnmountAllAndExit;
        _onExitOnly = onExitOnly;

        (_trayIcon, _iconHandle) = CreateDriveTrayIcon();
        _contextMenu = new ContextMenuStrip();

        _notifyIcon = new NotifyIcon
        {
            Icon = _trayIcon,
            Text = "WslDrive - Linux パーティション マウンター (WSL2)",
            ContextMenuStrip = _contextMenu,
            Visible = true
        };

        _notifyIcon.DoubleClick += (_, _) => _onOpenWindow();
        UpdateMounts([]);
    }

    public void UpdateMounts(IReadOnlyList<MountSession> activeMounts)
    {
        if (_disposed) return;

        _contextMenu.Items.Clear();

        var openWinItem = new ToolStripMenuItem("WslDrive ウィンドウを開く")
        {
            Font = new Font(_contextMenu.Font, FontStyle.Bold)
        };
        openWinItem.Click += (_, _) => _onOpenWindow();
        _contextMenu.Items.Add(openWinItem);
        _contextMenu.Items.Add(new ToolStripSeparator());

        if (activeMounts.Count == 0)
        {
            var emptyItem = new ToolStripMenuItem("現在マウント中のドライブはありません") { Enabled = false };
            _contextMenu.Items.Add(emptyItem);
        }
        else
        {
            foreach (MountSession session in activeMounts)
            {
                string modeLabel = session.IsReadOnly ? "RO" : "RW";
                string label = string.IsNullOrEmpty(session.MappedDriveLetter)
                    ? $"{session.MountName} ({session.FileSystemType}, {modeLabel})"
                    : $"{session.MappedDriveLetter}\\ - {session.MountName} ({session.FileSystemType}, {modeLabel})";

                var mountMenu = new ToolStripMenuItem(label);

                if (!string.IsNullOrEmpty(session.MappedDriveLetter))
                {
                    string drive = session.MappedDriveLetter;
                    var openDriveItem = new ToolStripMenuItem($"{drive}\\ をエクスプローラーで開く");
                    openDriveItem.Click += (_, _) => _onOpenDrive(drive);
                    mountMenu.DropDownItems.Add(openDriveItem);
                }

                var unmountItem = new ToolStripMenuItem("アンマウント（安全な取り外し）");
                unmountItem.Click += (_, _) => _onUnmountSession(session);
                mountMenu.DropDownItems.Add(unmountItem);

                _contextMenu.Items.Add(mountMenu);
            }
        }

        _contextMenu.Items.Add(new ToolStripSeparator());

        if (activeMounts.Count > 0)
        {
            var unmountAllExit = new ToolStripMenuItem("すべてアンマウントして終了");
            unmountAllExit.Click += (_, _) => _onUnmountAllAndExit();
            _contextMenu.Items.Add(unmountAllExit);
        }

        var exitItem = new ToolStripMenuItem("マウントを維持したままアプリを終了");
        exitItem.Click += (_, _) => _onExitOnly();
        _contextMenu.Items.Add(exitItem);

        _notifyIcon.Text = activeMounts.Count > 0
            ? $"WslDrive ({activeMounts.Count} 件マウント中)"
            : "WslDrive - Linux パーティション マウンター (WSL2)";
    }

    public void ShowBalloon(string title, string message, ToolTipIcon icon = ToolTipIcon.Info)
    {
        if (_disposed) return;
        _notifyIcon.ShowBalloonTip(2500, title, message, icon);
    }

    private static (Icon Icon, IntPtr Handle) CreateDriveTrayIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            // Rounded drive chassis
            using var bgBrush = new SolidBrush(Color.FromArgb(32, 74, 135));
            using var borderPen = new Pen(Color.FromArgb(220, 232, 248), 2f);
            g.FillRectangle(bgBrush, 4, 6, 24, 20);
            g.DrawRectangle(borderPen, 4, 6, 24, 20);

            // Status badge dot in green
            using var ledBrush = new SolidBrush(Color.FromArgb(78, 201, 135));
            g.FillEllipse(ledBrush, 20, 19, 5, 5);

            // "WD" label
            using var font = new Font("Segoe UI", 8.0f, FontStyle.Bold, GraphicsUnit.Point);
            using var textBrush = new SolidBrush(Color.White);
            g.DrawString("WD", font, textBrush, 4.5f, 8f);
        }

        IntPtr hIcon = bmp.GetHicon();
        return (Icon.FromHandle(hIcon), hIcon);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _contextMenu.Dispose();
        _trayIcon.Dispose();
        if (_iconHandle != IntPtr.Zero)
        {
            DestroyIcon(_iconHandle);
        }
    }
}
