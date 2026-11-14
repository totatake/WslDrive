using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LinuxRoDrive.Core.Disk;
using LinuxRoDrive.Core.Interop;
using LinuxRoDrive.Core.Models;
using LinuxRoDrive.Core.Wsl;

namespace LinuxRoDrive.App;

public sealed class PartitionRowViewModel
{
    public PhysicalDiskEntry Disk { get; init; } = null!;
    public PartitionEntry? Partition { get; init; }
    public bool IsVhdImage { get; init; }

    public string DiskDisplay => IsVhdImage ? "VHDX" : $"Disk {Disk.DiskIndex}";
    public string Model => Disk.Model;
    public string PartitionDisplay => Partition is null ? "1 (auto)" : (Partition.PartitionNumber == 0 ? "RAW" : Partition.PartitionNumber.ToString());
    public string FileSystemDisplay => Partition?.FileSystemType.ToString() ?? "VHDX";
    public string LabelDisplay => Partition is null
        ? Path.GetFileNameWithoutExtension(Disk.DevicePath)
        : (!string.IsNullOrEmpty(Partition.VolumeLabel) ? Partition.VolumeLabel : Partition.PartitionName);
    public string SizeDisplay => Partition?.FormattedSize ?? Disk.FormattedSize;
    public string UuidDisplay => string.IsNullOrEmpty(Partition?.Uuid) ? "-" : Partition.Uuid;

    public string StatusDisplay
    {
        get
        {
            if (IsVhdImage) return "Ready (.vhdx image)";
            if (!string.IsNullOrWhiteSpace(Disk.ScanError)) return Disk.ScanError;
            if (Disk.HasWindowsMountedVolumes) return $"In use by Windows ({Disk.WindowsDriveLetters})";
            if (Partition is null) return Disk.PartitionStyle;
            return Partition.IsLinuxFileSystem ? "Ready (Linux FS)" : Partition.PartitionTypeDescription;
        }
    }
}

public partial class MainWindow : Window
{
    private readonly DiskScanner _diskScanner = new();
    private readonly WslMountService _mountService = new();
    private readonly TrayIconManager _trayManager;

    private List<PhysicalDiskEntry> _scannedDisks = [];
    private readonly List<PhysicalDiskEntry> _inspectedImages = [];
    private List<MountSession> _activeMounts = [];
    private readonly Dictionary<string, string> _mountNameToTargetPath = new(StringComparer.OrdinalIgnoreCase);

    private bool _allowExit;
    private bool _shownTrayHint;
    private bool _isBusy;

    public MainWindow()
    {
        InitializeComponent();

        _trayManager = new TrayIconManager(
            onOpenWindow: () => Dispatcher.Invoke(RestoreFromTray),
            onOpenDrive: drive => Dispatcher.Invoke(() => OpenDriveInExplorer(drive)),
            onUnmountSession: session => Dispatcher.Invoke(() => _ = UnmountSessionAsync(session)),
            onUnmountAllAndExit: () => Dispatcher.Invoke(() => _ = UnmountAllAndExitAsync()),
            onExitOnly: () => Dispatcher.Invoke(ExitApplication));

        Loaded += MainWindow_Loaded;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        UpdateAdminBadge();
        await RefreshAllAsync();
    }

    private void UpdateAdminBadge()
    {
        bool isAdmin = DiskScanner.IsRunningAsAdministrator();
        AdminBadge.Background = new SolidColorBrush(isAdmin
            ? System.Windows.Media.Color.FromRgb(46, 125, 50)
            : System.Windows.Media.Color.FromRgb(183, 28, 28));
        AdminBadgeText.Text = isAdmin ? "Administrator" : "Not Elevated (Limited)";
    }

    private async Task RefreshAllAsync()
    {
        SetBusy(true);
        try
        {
            AppendLog("Scanning physical disks and WSL2 status...");
            await RefreshDistrosAsync();

            _scannedDisks = await Task.Run(() => _diskScanner.ScanPhysicalDisks());
            RebuildPartitionRows();

            await RefreshActiveMountsAndDriveLettersAsync();
            AppendLog($"Scan complete: {_scannedDisks.Count} physical disk(s) detected.");
        }
        catch (Exception ex)
        {
            AppendLog($"ERROR during scan: {ex.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task RefreshDistrosAsync()
    {
        string? defaultDistro = await _mountService.GetDefaultDistroAsync();
        List<string> distros = await _mountService.GetDistrosAsync();

        string? previousSelection = DistroComboBox.SelectedItem as string;
        DistroComboBox.ItemsSource = distros;

        if (!string.IsNullOrEmpty(previousSelection) && distros.Contains(previousSelection))
        {
            DistroComboBox.SelectedItem = previousSelection;
        }
        else if (!string.IsNullOrEmpty(defaultDistro) && distros.Contains(defaultDistro))
        {
            DistroComboBox.SelectedItem = defaultDistro;
        }
        else if (distros.Count > 0)
        {
            DistroComboBox.SelectedIndex = 0;
        }
    }

    private async Task RefreshActiveMountsAndDriveLettersAsync()
    {
        string? distro = DistroComboBox.SelectedItem as string;
        _activeMounts = await _mountService.GetActiveMountsAsync(distro);
        ActiveMountsGrid.ItemsSource = _activeMounts;
        _trayManager.UpdateMounts(_activeMounts);

        List<char> freeLetters = DriveMapper.GetAvailableDriveLetters();
        List<string> letterLabels = freeLetters.Select(c => $"{c}:").ToList();
        string? currentSelection = DriveLetterComboBox.SelectedItem as string;
        DriveLetterComboBox.ItemsSource = letterLabels;
        if (!string.IsNullOrEmpty(currentSelection) && letterLabels.Contains(currentSelection))
        {
            DriveLetterComboBox.SelectedItem = currentSelection;
        }
        else if (letterLabels.Count > 0)
        {
            DriveLetterComboBox.SelectedIndex = 0;
        }
    }

    private void RebuildPartitionRows()
    {
        bool onlyLinux = OnlyLinuxCheckBox.IsChecked == true;
        var rows = new List<PartitionRowViewModel>();

        foreach (PhysicalDiskEntry disk in _scannedDisks.Concat(_inspectedImages))
        {
            bool isVhdx = disk.InterfaceType.Equals("VHDX", StringComparison.OrdinalIgnoreCase);
            if (isVhdx)
            {
                rows.Add(new PartitionRowViewModel
                {
                    Disk = disk,
                    Partition = null,
                    IsVhdImage = true
                });
                continue;
            }

            if (disk.Partitions.Count == 0 && (!onlyLinux || !string.IsNullOrWhiteSpace(disk.ScanError)))
            {
                rows.Add(new PartitionRowViewModel
                {
                    Disk = disk,
                    Partition = null,
                    IsVhdImage = false
                });
                continue;
            }

            foreach (PartitionEntry part in disk.Partitions)
            {
                if (onlyLinux && !part.IsLinuxFileSystem)
                {
                    continue;
                }

                rows.Add(new PartitionRowViewModel
                {
                    Disk = disk,
                    Partition = part,
                    IsVhdImage = false
                });
            }
        }

        PartitionsGrid.ItemsSource = rows;
        if (rows.Count > 0)
        {
            PartitionsGrid.SelectedIndex = 0;
        }
    }

    private void OnlyLinuxCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (IsLoaded)
        {
            RebuildPartitionRows();
        }
    }

    private void PartitionsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PartitionsGrid.SelectedItem is PartitionRowViewModel row && row.Partition is not null)
        {
            NoLoadCheckBox.IsChecked = row.Partition.FileSystemType is LinuxFileSystemType.Ext4 or LinuxFileSystemType.Ext3;
        }
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        await RefreshAllAsync();
    }

    private void InspectImageButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select Disk Image (.img / .vhd / .vhdx)",
            Filter = "Disk Images (*.vhdx;*.vhd;*.img;*.raw)|*.vhdx;*.vhd;*.img;*.raw|All Files (*.*)|*.*"
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            PhysicalDiskEntry inspected = _diskScanner.InspectImageFile(dialog.FileName);
            _inspectedImages.RemoveAll(d => d.DevicePath.Equals(inspected.DevicePath, StringComparison.OrdinalIgnoreCase));
            _inspectedImages.Add(inspected);
            RebuildPartitionRows();
            AppendLog($"Inspected image: {dialog.FileName} ({inspected.PartitionStyle})");
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, $"Failed to inspect image:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void MountButton_Click(object sender, RoutedEventArgs e)
    {
        if (PartitionsGrid.SelectedItem is not PartitionRowViewModel row)
        {
            System.Windows.MessageBox.Show(this, "Please select a partition or VHDX entry to mount.", "No Selection", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!row.IsVhdImage && row.Disk.DiskIndex < 0)
        {
            System.Windows.MessageBox.Show(
                this,
                "Raw .img files cannot be passed directly to `wsl --mount`. Convert to .vhdx or select a physical disk / .vhdx file.",
                "Unsupported Image Format",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        if (!row.IsVhdImage && row.Disk.HasWindowsMountedVolumes)
        {
            var confirm = System.Windows.MessageBox.Show(
                this,
                $"Disk {row.Disk.DiskIndex} ({row.Disk.Model}) currently hosts Windows drive(s) [{row.Disk.WindowsDriveLetters}].\n`wsl --mount` cannot attach physical disks currently in use by Windows.\n\nAttempt mount anyway?",
                "Disk In Use Warning",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes)
            {
                return;
            }
        }

        string? selectedLetter = DriveLetterComboBox.SelectedItem as string;
        if (string.IsNullOrWhiteSpace(selectedLetter))
        {
            System.Windows.MessageBox.Show(this, "No available drive letter selected.", "Drive Letter", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        char driveLetter = selectedLetter[0];
        string? distro = DistroComboBox.SelectedItem as string;

        var request = new MountRequest
        {
            DiskIndex = row.IsVhdImage ? null : row.Disk.DiskIndex,
            VhdPath = row.IsVhdImage ? row.Disk.DevicePath : null,
            PartitionNumber = row.IsVhdImage ? 1 : (row.Partition?.PartitionNumber > 0 ? row.Partition.PartitionNumber : null),
            DriveLetter = driveLetter,
            FileSystemType = row.Partition?.FileSystemType.ToWslTypeName(),
            UseNoLoadJournal = NoLoadCheckBox.IsChecked == true,
            DistroName = distro
        };

        SetBusy(true);
        try
        {
            MountOperationResult result = await _mountService.MountAsync(request, msg => Dispatcher.Invoke(() => AppendLog(msg)));
            if (!result.Success || result.Session is null)
            {
                AppendLog($"Mount failed: {result.Message}");
                System.Windows.MessageBox.Show(this, result.Message, "Mount Failed", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            if (!string.IsNullOrWhiteSpace(result.Session.TargetWslDiskPath))
            {
                _mountNameToTargetPath[result.Session.MountName] = result.Session.TargetWslDiskPath;
            }

            AppendLog($"SUCCESS: {result.Message}");
            await RefreshActiveMountsAndDriveLettersAsync();

            if (OpenExplorerCheckBox.IsChecked == true && !string.IsNullOrEmpty(result.Session.MappedDriveLetter))
            {
                OpenDriveInExplorer(result.Session.MappedDriveLetter);
            }
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void UnmountButton_Click(object sender, RoutedEventArgs e)
    {
        if (ActiveMountsGrid.SelectedItem is MountSession session)
        {
            await UnmountSessionAsync(session);
            return;
        }

        if (_activeMounts.Count == 1)
        {
            await UnmountSessionAsync(_activeMounts[0]);
            return;
        }

        System.Windows.MessageBox.Show(this, "Select an active mount in the bottom table to unmount.", "Unmount", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async Task UnmountSessionAsync(MountSession session)
    {
        SetBusy(true);
        try
        {
            char? letter = !string.IsNullOrWhiteSpace(session.MappedDriveLetter) ? session.MappedDriveLetter[0] : null;
            string? distro = DistroComboBox.SelectedItem as string;
            string? targetPath = session.TargetWslDiskPath ??
                                 (_mountNameToTargetPath.TryGetValue(session.MountName, out string? p) ? p : null);

            UnmountOperationResult result = await _mountService.UnmountAsync(
                driveLetter: letter,
                diskIndex: null,
                vhdPath: targetPath,
                distroName: distro,
                log: msg => Dispatcher.Invoke(() => AppendLog(msg)));

            if (result.Success)
            {
                _mountNameToTargetPath.Remove(session.MountName);
            }

            AppendLog(result.Success ? $"Unmounted: {result.Message}" : $"Unmount warning: {result.Message}");
            await RefreshActiveMountsAndDriveLettersAsync();
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void OpenDriveButton_Click(object sender, RoutedEventArgs e)
    {
        if (ActiveMountsGrid.SelectedItem is MountSession session && !string.IsNullOrEmpty(session.MappedDriveLetter))
        {
            OpenDriveInExplorer(session.MappedDriveLetter);
        }
        else if (_activeMounts.FirstOrDefault(m => !string.IsNullOrEmpty(m.MappedDriveLetter)) is { } first &&
                 !string.IsNullOrEmpty(first.MappedDriveLetter))
        {
            OpenDriveInExplorer(first.MappedDriveLetter);
        }
    }

    private static void OpenDriveInExplorer(string driveLetter)
    {
        try
        {
            string target = driveLetter.EndsWith('\\') ? driveLetter : $"{driveLetter}\\";
            Process.Start(new ProcessStartInfo("explorer.exe", target) { UseShellExecute = true });
        }
        catch
        {
            // Ignore explorer launch errors
        }
    }

    private void SetBusy(bool busy)
    {
        _isBusy = busy;
        RefreshButton.IsEnabled = !busy;
        MountButton.IsEnabled = !busy;
        UnmountButton.IsEnabled = !busy;
        InspectImageButton.IsEnabled = !busy;
    }

    private void AppendLog(string message)
    {
        string line = $"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}";
        LogTextBox.AppendText(line);
        LogTextBox.ScrollToEnd();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_allowExit)
        {
            e.Cancel = true;
            Hide();
            if (!_shownTrayHint)
            {
                _shownTrayHint = true;
                _trayManager.ShowBalloon("LinuxRoDrive", "Minimized to system tray. Right-click the tray icon to manage mounts or exit.");
            }
            return;
        }

        _trayManager.Dispose();
        base.OnClosing(e);
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private async Task UnmountAllAndExitAsync()
    {
        if (_isBusy) return;

        foreach (MountSession session in _activeMounts.ToList())
        {
            await UnmountSessionAsync(session);
        }

        ExitApplication();
    }

    private void ExitApplication()
    {
        _allowExit = true;
        _trayManager.Dispose();
        System.Windows.Application.Current.Shutdown();
    }
}
