using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WslMountDrive.App.Localization;
using WslMountDrive.Core.Disk;
using WslMountDrive.Core.Interop;
using WslMountDrive.Core.Models;
using WslMountDrive.Core.Wsl;

namespace WslMountDrive.App;

public sealed class PartitionRowViewModel
{
    public PhysicalDiskEntry Disk { get; init; } = null!;
    public PartitionEntry? Partition { get; init; }
    public bool IsVhdImage { get; init; }
    public MountSession? ActiveSession { get; init; }

    public bool IsMounted => ActiveSession is not null;
    public bool CanMount => !IsMounted && (IsVhdImage || (Disk.DiskIndex >= 0 && (!Disk.HasWindowsMountedVolumes || Partition?.IsLinuxFileSystem == true)));

    public string DiskDisplay => IsVhdImage ? "VHDX" : (Disk.DiskIndex >= 0 ? $"Disk {Disk.DiskIndex}" : "Image");
    public string Model => Disk.Model;
    public string PartitionDisplay => Partition is null ? AppStrings.Instance.PartitionAuto : (Partition.PartitionNumber == 0 ? "RAW" : Partition.PartitionNumber.ToString());
    public string FileSystemDisplay => Partition?.FileSystemType.ToString() ?? "VHDX";
    public string LabelDisplay => Partition is null
        ? Path.GetFileNameWithoutExtension(Disk.DevicePath)
        : (!string.IsNullOrEmpty(Partition.VolumeLabel) ? Partition.VolumeLabel : Partition.PartitionName);
    public string SizeDisplay => Partition?.FormattedSize ?? Disk.FormattedSize;
    public string UuidDisplay => string.IsNullOrEmpty(Partition?.Uuid) ? "-" : Partition.Uuid;

    public string MountedDriveDisplay => ActiveSession?.MappedDriveLetter ?? "-";

    public string StatusDisplay
    {
        get
        {
            var s = AppStrings.Instance;
            if (ActiveSession is not null)
            {
                string drive = !string.IsNullOrEmpty(ActiveSession.MappedDriveLetter)
                    ? ActiveSession.MappedDriveLetter
                    : ActiveSession.MountName;
                return s.StatusMounted(drive, ActiveSession.MountOptions);
            }
            if (IsVhdImage) return s.StatusVhdxMountable;
            if (Disk.DiskIndex < 0) return s.StatusRawImgInspectOnly;
            if (!string.IsNullOrWhiteSpace(Disk.ScanError)) return Disk.ScanError;
            if (Disk.HasWindowsMountedVolumes)
            {
                return Partition is { IsLinuxFileSystem: true }
                    ? s.StatusSharedNbdMountable(Disk.WindowsDriveLetters)
                    : s.StatusWindowsInUseNotMountable(Disk.WindowsDriveLetters);
            }
            if (Partition is null) return Disk.PartitionStyle;
            return Partition.IsLinuxFileSystem ? s.StatusLinuxMountable : Partition.PartitionTypeDescription;
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

    private sealed record LanguageOption(UiLanguageMode Mode, string Label);

    private bool _allowExit;
    private bool _isBusy;
    private bool _suppressDistroChange;

    public MainWindow()
    {
        InitializeComponent();

        _trayManager = new TrayIconManager(
            onOpenWindow: () => Dispatcher.Invoke(RestoreFromTray),
            onOpenDrive: drive => Dispatcher.Invoke(() => OpenDriveInExplorer(drive)),
            onUnmountSession: session => Dispatcher.Invoke(() => _ = UnmountSessionAsync(session)),
            onUnmountAllAndExit: () => Dispatcher.Invoke(() => _ = UnmountAllAndExitAsync()),
            onExitOnly: () => Dispatcher.Invoke(ExitApplication));

        LanguageComboBox.ItemsSource = new LanguageOption[]
        {
            new(UiLanguageMode.Auto, "Auto"),
            new(UiLanguageMode.Japanese, "日本語"),
            new(UiLanguageMode.English, "English")
        };
        LanguageComboBox.SelectedValue = UiLanguageMode.Auto;
        ApplyLanguage();

        Loaded += MainWindow_Loaded;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        UpdateAdminBadge();
        UpdateMountModeUi();
        await RefreshAllAsync();
    }

    private void LanguageComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LanguageComboBox.SelectedValue is not UiLanguageMode mode) return;

        AppStrings.Instance.Mode = mode;
        ApplyLanguage();
        if (IsLoaded)
        {
            RebuildPartitionRows();
            _trayManager.UpdateMounts(_activeMounts);
        }
    }

    private void ApplyLanguage()
    {
        var s = AppStrings.Instance;

        Title = s.WindowTitle;
        HeaderSubtitleText.Text = s.HeaderSubtitle;
        LanguageLabel.Text = s.LanguageLabel;
        WslDistroLabel.Text = s.WslDistroLabel;
        RelaunchAdminButton.Content = s.RelaunchAdmin;
        BannerUnmountAllButton.Content = s.UnmountAll;

        RefreshButton.Content = s.RefreshDisks;
        InspectImageButton.Content = s.OpenImage;
        OnlyLinuxCheckBox.Content = s.OnlyLinuxPartitions;

        PartitionsGroupBox.Header = s.PartitionsHeader;
        ColDisk.Header = s.ColDisk;
        ColModel.Header = s.ColModel;
        ColPartition.Header = s.ColPartition;
        ColFs.Header = s.ColFs;
        ColLabel.Header = s.ColLabel;
        ColSize.Header = s.ColSize;
        ColDrive.Header = s.ColDrive;
        ColStatus.Header = s.ColStatus;

        DriveLetterLabel.Text = s.DriveLetterLabel;
        ReadWriteCheckBox.Content = s.ReadWriteCheck;
        ReadWriteCheckBox.ToolTip = s.ReadWriteToolTip;
        NoLoadCheckBox.Content = s.NoLoadCheck;
        NoLoadCheckBox.ToolTip = s.NoLoadToolTip;
        OpenExplorerCheckBox.Content = s.OpenExplorerCheck;
        UnmountButton.Content = s.UnmountSelected;

        ActiveMountsGroupBox.Header = s.ActiveMountsHeader;
        ColActiveDrive.Header = s.ColActiveDrive;
        ColActiveMode.Header = s.ColActiveMode;
        ColActiveMountName.Header = s.ColActiveMountName;
        ColActiveFs.Header = s.ColActiveFs;
        ColActiveDevice.Header = s.ColActiveDevice;
        ColActiveOptions.Header = s.ColActiveOptions;
        ColActiveUnc.Header = s.ColActiveUnc;
        ColActiveActions.Header = s.ColActiveActions;

        LogExpander.Header = s.LogHeader;
        CopyLogButton.Content = s.CopyLog;
        ClearLogButton.Content = s.ClearLog;

        UpdateAdminBadge();
        UpdateMountModeUi();
        UpdateStatusBanner();
    }

    private void UpdateAdminBadge()
    {
        bool isAdmin = DiskScanner.IsRunningAsAdministrator();
        AdminBadge.Background = new SolidColorBrush(isAdmin
            ? System.Windows.Media.Color.FromRgb(46, 125, 50)
            : System.Windows.Media.Color.FromRgb(183, 28, 28));
        AdminBadgeText.Text = isAdmin ? AppStrings.Instance.AdminOk : AppStrings.Instance.AdminLimited;
        RelaunchAdminButton.Visibility = isAdmin ? Visibility.Collapsed : Visibility.Visible;
    }

    private async Task RefreshAllAsync()
    {
        var s = AppStrings.Instance;
        SetBusy(true);
        try
        {
            AppendLog(s.LogScanning);
            await RefreshDistrosAsync();

            _scannedDisks = await Task.Run(() => _diskScanner.ScanPhysicalDisks());
            await RefreshActiveMountsAndDriveLettersAsync();

            int linuxPartCount = _scannedDisks.Sum(d => d.Partitions.Count(p => p.IsLinuxFileSystem));
            AppendLog(s.LogScanComplete(_scannedDisks.Count, linuxPartCount));
        }
        catch (Exception ex)
        {
            AppendLog(s.LogScanError(ex.Message));
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task RefreshDistrosAsync()
    {
        _suppressDistroChange = true;
        try
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
        finally
        {
            _suppressDistroChange = false;
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

        RebuildPartitionRows();
        UpdateStatusBanner();
    }

    private void UpdateStatusBanner()
    {
        var s = AppStrings.Instance;
        if (_activeMounts.Count > 0)
        {
            string drives = string.Join(", ", _activeMounts.Select(m => $"{m.MappedDriveLetter ?? m.MountName} ({m.AccessModeDisplay})"));
            StatusBanner.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(232, 245, 233));
            StatusBanner.BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(165, 214, 167));
            StatusBannerText.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(27, 94, 32));
            StatusBannerText.Text = s.StatusActiveMounts(_activeMounts.Count, drives);
            BannerUnmountAllButton.Visibility = Visibility.Visible;
        }
        else
        {
            StatusBanner.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(235, 243, 254));
            StatusBanner.BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(182, 212, 254));
            StatusBannerText.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(8, 66, 152));
            StatusBannerText.Text = s.StatusIdle;
            BannerUnmountAllButton.Visibility = Visibility.Collapsed;
        }
    }

    private MountSession? FindMatchingActiveSession(PhysicalDiskEntry disk, PartitionEntry? part, bool isVhdx)
    {
        if (_activeMounts.Count == 0) return null;

        if (isVhdx)
        {
            var req = new MountRequest
            {
                VhdPath = disk.DevicePath,
                PartitionNumber = part?.PartitionNumber > 0 ? part.PartitionNumber : 1,
                DriveLetter = 'L'
            };
            string expectedName = req.ResolveMountName();
            return _activeMounts.FirstOrDefault(m =>
                m.MountName.Equals(expectedName, StringComparison.OrdinalIgnoreCase) ||
                (_mountNameToTargetPath.TryGetValue(m.MountName, out string? target) &&
                 target.Equals(disk.DevicePath, StringComparison.OrdinalIgnoreCase)));
        }

        if (disk.DiskIndex >= 0)
        {
            int partNum = part?.PartitionNumber ?? 0;
            string expectedWithPart = partNum > 0 ? $"wslmountdrive_d{disk.DiskIndex}p{partNum}" : $"wslmountdrive_d{disk.DiskIndex}";
            return _activeMounts.FirstOrDefault(m =>
                m.MountName.Equals(expectedWithPart, StringComparison.OrdinalIgnoreCase));
        }

        return null;
    }

    private void RebuildPartitionRows()
    {
        bool onlyLinux = OnlyLinuxCheckBox.IsChecked == true;
        var rows = new List<PartitionRowViewModel>();

        foreach (PhysicalDiskEntry disk in _scannedDisks.Concat(_inspectedImages))
        {
            bool isVhdx = disk.InterfaceType.Equals("VHDX", StringComparison.OrdinalIgnoreCase);
            if (isVhdx && disk.Partitions.Count == 0)
            {
                rows.Add(new PartitionRowViewModel
                {
                    Disk = disk,
                    Partition = null,
                    IsVhdImage = true,
                    ActiveSession = FindMatchingActiveSession(disk, null, isVhdx: true)
                });
                continue;
            }

            if (disk.Partitions.Count == 0 && (!onlyLinux || !string.IsNullOrWhiteSpace(disk.ScanError)))
            {
                rows.Add(new PartitionRowViewModel
                {
                    Disk = disk,
                    Partition = null,
                    IsVhdImage = false,
                    ActiveSession = FindMatchingActiveSession(disk, null, isVhdx: false)
                });
                continue;
            }

            foreach (PartitionEntry part in disk.Partitions)
            {
                if (onlyLinux && !part.IsLinuxFileSystem && !isVhdx)
                {
                    continue;
                }

                rows.Add(new PartitionRowViewModel
                {
                    Disk = disk,
                    Partition = part,
                    IsVhdImage = isVhdx,
                    ActiveSession = FindMatchingActiveSession(disk, part, isVhdx)
                });
            }
        }

        PartitionsGrid.ItemsSource = rows;
        if (rows.Count > 0 && PartitionsGrid.SelectedIndex < 0)
        {
            PartitionsGrid.SelectedIndex = 0;
        }
    }

    private void ReadWriteCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (IsLoaded)
        {
            UpdateMountModeUi();
        }
    }

    private void UpdateMountModeUi()
    {
        bool isRw = ReadWriteCheckBox.IsChecked == true;
        NoLoadCheckBox.IsEnabled = !isRw;
        MountButton.Content = isRw ? AppStrings.Instance.MountSelectedRw : AppStrings.Instance.MountSelectedRo;
        MountButton.Background = new SolidColorBrush(isRw
            ? System.Windows.Media.Color.FromRgb(0xD9, 0x77, 0x06)
            : System.Windows.Media.Color.FromRgb(0x1F, 0x6F, 0xEB));
        ModeBadge.Background = new SolidColorBrush(isRw
            ? System.Windows.Media.Color.FromRgb(0xB4, 0x53, 0x09)
            : System.Windows.Media.Color.FromRgb(0x2B, 0x54, 0x7E));
        ModeBadgeText.Text = isRw ? "Read-Write (rw)" : "Read-Only (ro,noload)";
    }

    private void OnlyLinuxCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (IsLoaded)
        {
            RebuildPartitionRows();
        }
    }

    private async void DistroComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _suppressDistroChange || _isBusy) return;
        await RefreshActiveMountsAndDriveLettersAsync();
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

    private void RelaunchAdminButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string? exePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exePath)) return;

            Process.Start(new ProcessStartInfo(exePath)
            {
                UseShellExecute = true,
                Verb = "runas"
            });
            ExitApplication();
        }
        catch (Exception ex)
        {
            AppendLog(AppStrings.Instance.LogRelaunchFailed(ex.Message));
        }
    }

    private void InspectImageButton_Click(object sender, RoutedEventArgs e)
    {
        var s = AppStrings.Instance;
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = s.OpenImageDialogTitle,
            Filter = s.OpenImageDialogFilter
        };

        if (dialog.ShowDialog(this) == true)
        {
            InspectAndAddImageFile(dialog.FileName);
        }
    }

    private void Window_PreviewDragOver(object sender, System.Windows.DragEventArgs e)
    {
        if (e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop))
        {
            e.Effects = System.Windows.DragDropEffects.Copy;
            e.Handled = true;
        }
    }

    private void Window_Drop(object sender, System.Windows.DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop)) return;
        if (e.Data.GetData(System.Windows.DataFormats.FileDrop) is not string[] files) return;

        foreach (string file in files)
        {
            if (File.Exists(file))
            {
                InspectAndAddImageFile(file);
            }
        }
    }

    private void InspectAndAddImageFile(string filePath)
    {
        try
        {
            PhysicalDiskEntry inspected = _diskScanner.InspectImageFile(filePath);
            _inspectedImages.RemoveAll(d => d.DevicePath.Equals(inspected.DevicePath, StringComparison.OrdinalIgnoreCase));
            _inspectedImages.Add(inspected);
            RebuildPartitionRows();
            AppendLog(AppStrings.Instance.LogImageAdded(filePath, inspected.PartitionStyle));
        }
        catch (Exception ex)
        {
            var s = AppStrings.Instance;
            System.Windows.MessageBox.Show(this, s.ImageReadError(ex.Message), s.ErrorTitle, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void PartitionsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_isBusy || PartitionsGrid.SelectedItem is not PartitionRowViewModel row) return;

        if (row.ActiveSession is not null)
        {
            OpenSessionInExplorer(row.ActiveSession);
        }
        else if (row.CanMount)
        {
            await MountRowAsync(row);
        }
    }

    private void ActiveRowOpenButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: MountSession session })
        {
            OpenSessionInExplorer(session);
        }
    }

    private void ActiveMountsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ActiveMountsGrid.SelectedItem is MountSession session)
        {
            OpenSessionInExplorer(session);
        }
    }

    private async void MountButton_Click(object sender, RoutedEventArgs e)
    {
        if (PartitionsGrid.SelectedItem is not PartitionRowViewModel row)
        {
            var s = AppStrings.Instance;
            System.Windows.MessageBox.Show(this, s.NoSelectionMsg, s.NoSelectionTitle, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (row.ActiveSession is not null)
        {
            OpenSessionInExplorer(row.ActiveSession);
            return;
        }

        await MountRowAsync(row);
    }

    private async Task MountRowAsync(PartitionRowViewModel row)
    {
        var s = AppStrings.Instance;
        if (!row.IsVhdImage && row.Disk.DiskIndex < 0)
        {
            System.Windows.MessageBox.Show(
                this,
                s.RawImgUnsupportedMsg,
                s.UnsupportedImageFormatTitle,
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        bool isRw = ReadWriteCheckBox.IsChecked == true;

        if (!row.IsVhdImage && row.Disk.HasWindowsMountedVolumes)
        {
            if (row.Partition?.IsLinuxFileSystem == true)
            {
                if (isRw)
                {
                    System.Windows.MessageBox.Show(
                        this,
                        s.NbdRwUnsupportedMsg,
                        s.NbdRwUnsupportedTitle,
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }
                AppendLog(s.LogUsingNbdBridge(row.Disk.WindowsDriveLetters));
            }
            else
            {
                var confirm = System.Windows.MessageBox.Show(
                    this,
                    s.WindowsInUseWarningMsg(row.Disk.DiskIndex, row.Disk.Model, row.Disk.WindowsDriveLetters),
                    s.WindowsInUseWarningTitle,
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);
                if (confirm != MessageBoxResult.Yes)
                {
                    return;
                }
            }
        }

        string? selectedLetter = DriveLetterComboBox.SelectedItem as string;
        if (string.IsNullOrWhiteSpace(selectedLetter))
        {
            var free = DriveMapper.GetAvailableDriveLetters();
            if (free.Count == 0)
            {
                System.Windows.MessageBox.Show(this, s.NoFreeDriveLettersMsg, s.NoFreeDriveLettersTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            selectedLetter = $"{free[0]}:";
        }

        char driveLetter = selectedLetter[0];
        string? distro = DistroComboBox.SelectedItem as string;

        var request = new MountRequest
        {
            DiskIndex = row.IsVhdImage ? null : row.Disk.DiskIndex,
            VhdPath = row.IsVhdImage ? row.Disk.DevicePath : null,
            PartitionNumber = row.IsVhdImage
                ? (row.Partition?.PartitionNumber > 0 ? row.Partition.PartitionNumber : 1)
                : (row.Partition?.PartitionNumber > 0 ? row.Partition.PartitionNumber : null),
            DriveLetter = driveLetter,
            FileSystemType = row.Partition?.FileSystemType.ToWslTypeName(),
            ReadWrite = isRw,
            UseNoLoadJournal = NoLoadCheckBox.IsChecked == true,
            DistroName = distro,
            PartitionOffsetBytes = row.Partition?.OffsetBytes,
            PartitionSizeBytes = row.Partition?.SizeBytes,
            SectorSize = row.Disk.SectorSize,
            ForceNbdBridge = !row.IsVhdImage && row.Disk.HasWindowsMountedVolumes && row.Partition?.IsLinuxFileSystem == true
        };

        SetBusy(true);
        try
        {
            MountOperationResult result = await _mountService.MountAsync(request, msg => Dispatcher.Invoke(() => AppendLog(msg)));
            if (!result.Success || result.Session is null)
            {
                AppendLog(s.LogMountFailed(result.Message));
                System.Windows.MessageBox.Show(this, result.Message, s.MountFailedTitle, MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            if (!string.IsNullOrWhiteSpace(result.Session.TargetWslDiskPath))
            {
                _mountNameToTargetPath[result.Session.MountName] = result.Session.TargetWslDiskPath;
            }

            AppendLog(s.LogSuccess(result.Message));
            _trayManager.ShowBalloon(s.BalloonMountCompleteTitle, s.BalloonMountCompleteBody(result.Session.MappedDriveLetter, result.Session.IsReadOnly));
            await RefreshActiveMountsAndDriveLettersAsync();

            if (OpenExplorerCheckBox.IsChecked == true)
            {
                OpenSessionInExplorer(result.Session);
            }
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void UnmountButton_Click(object sender, RoutedEventArgs e)
    {
        if (ActiveMountsGrid.SelectedItem is MountSession activeSession)
        {
            await UnmountSessionAsync(activeSession);
            return;
        }

        if (PartitionsGrid.SelectedItem is PartitionRowViewModel { ActiveSession: { } rowSession })
        {
            await UnmountSessionAsync(rowSession);
            return;
        }

        if (_activeMounts.Count == 1)
        {
            await UnmountSessionAsync(_activeMounts[0]);
            return;
        }

        if (PartitionsGrid.SelectedItem is PartitionRowViewModel selectedRow &&
            (selectedRow.IsVhdImage || (selectedRow.Disk.DiskIndex >= 0 && !selectedRow.Disk.HasWindowsMountedVolumes)))
        {
            await UnmountRowDiskTargetAsync(selectedRow);
            return;
        }

        var s = AppStrings.Instance;
        System.Windows.MessageBox.Show(this, s.SelectUnmountRowMsg, s.UnmountTitle, MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async void UnmountAllButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy || _activeMounts.Count == 0) return;

        foreach (MountSession session in _activeMounts.ToList())
        {
            await UnmountSessionAsync(session, promptIfSiblings: false);
        }
    }

    private async Task UnmountRowDiskTargetAsync(PartitionRowViewModel row)
    {
        SetBusy(true);
        try
        {
            string? distro = DistroComboBox.SelectedItem as string;
            UnmountOperationResult result = await _mountService.UnmountAsync(
                driveLetter: null,
                diskIndex: row.IsVhdImage ? null : row.Disk.DiskIndex,
                vhdPath: row.IsVhdImage ? row.Disk.DevicePath : null,
                distroName: distro,
                log: msg => Dispatcher.Invoke(() => AppendLog(msg)));

            AppendLog(AppStrings.Instance.LogWslDetachResult(result.Success, result.Message));
            await RefreshActiveMountsAndDriveLettersAsync();
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task UnmountSessionAsync(MountSession session, bool promptIfSiblings = true)
    {
        if (promptIfSiblings)
        {
            var siblings = _activeMounts
                .Where(m => !m.MountName.Equals(session.MountName, StringComparison.OrdinalIgnoreCase) &&
                            ((session.DiskIndex.HasValue && m.DiskIndex == session.DiskIndex) ||
                             (!string.IsNullOrWhiteSpace(session.TargetWslDiskPath) &&
                              string.Equals(m.TargetWslDiskPath, session.TargetWslDiskPath, StringComparison.OrdinalIgnoreCase))))
                .ToList();

            if (siblings.Count > 0)
            {
                string sessionDrive = !string.IsNullOrWhiteSpace(session.MappedDriveLetter)
                    ? session.MappedDriveLetter
                    : session.MountName;
                string siblingList = string.Join(", ", siblings.Select(s =>
                    !string.IsNullOrWhiteSpace(s.MappedDriveLetter) ? $"{s.MappedDriveLetter} ({s.MountName})" : s.MountName));

                var choice = System.Windows.MessageBox.Show(
                    this,
                    AppStrings.Instance.MultiPartitionUnmountMsg(siblingList, sessionDrive, session.MountName),
                    AppStrings.Instance.MultiPartitionUnmountTitle,
                    MessageBoxButton.YesNoCancel,
                    MessageBoxImage.Question);

                if (choice == MessageBoxResult.Cancel)
                {
                    return;
                }

                if (choice == MessageBoxResult.No)
                {
                    foreach (MountSession s in siblings.Append(session).ToList())
                    {
                        await UnmountSessionAsync(s, promptIfSiblings: false);
                    }
                    return;
                }
            }
        }

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
                log: msg => Dispatcher.Invoke(() => AppendLog(msg)),
                mountName: session.MountName);

            if (result.Success)
            {
                _mountNameToTargetPath.Remove(session.MountName);
            }

            AppendLog(AppStrings.Instance.LogUnmountResult(result.Success, result.Message));
            await RefreshActiveMountsAndDriveLettersAsync();
        }
        finally
        {
            SetBusy(false);
        }
    }

    private static void OpenSessionInExplorer(MountSession session)
    {
        if (!string.IsNullOrWhiteSpace(session.MappedDriveLetter))
        {
            OpenDriveInExplorer(session.MappedDriveLetter);
        }
        else if (!string.IsNullOrWhiteSpace(session.UncPath))
        {
            OpenDriveInExplorer(session.UncPath);
        }
    }

    private static void OpenDriveInExplorer(string driveOrPath)
    {
        try
        {
            string target = driveOrPath.Length == 2 && driveOrPath[1] == ':'
                ? $"{driveOrPath}\\"
                : driveOrPath;
            Process.Start(new ProcessStartInfo("explorer.exe", target) { UseShellExecute = true });
        }
        catch
        {
            // Ignore explorer launch errors
        }
    }

    private void CopyLogButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!string.IsNullOrEmpty(LogTextBox.Text))
            {
                System.Windows.Clipboard.SetText(LogTextBox.Text);
            }
        }
        catch
        {
            // Ignore clipboard contention
        }
    }

    private void ClearLogButton_Click(object sender, RoutedEventArgs e)
    {
        LogTextBox.Clear();
    }

    private void SetBusy(bool busy)
    {
        _isBusy = busy;
        RefreshButton.IsEnabled = !busy;
        MountButton.IsEnabled = !busy;
        UnmountButton.IsEnabled = !busy;
        InspectImageButton.IsEnabled = !busy;
        BannerUnmountAllButton.IsEnabled = !busy;
    }

    private void AppendLog(string message)
    {
        string line = $"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}";
        LogTextBox.AppendText(line);
        LogTextBox.ScrollToEnd();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_allowExit && _activeMounts.Count > 0)
        {
            var res = System.Windows.MessageBox.Show(
                this,
                AppStrings.Instance.ExitConfirmMsg(_activeMounts.Count),
                AppStrings.Instance.ExitConfirmTitle,
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Question);

            if (res == MessageBoxResult.Cancel)
            {
                e.Cancel = true;
                return;
            }

            if (res == MessageBoxResult.Yes)
            {
                e.Cancel = true;
                _ = UnmountAllAndExitAsync();
                return;
            }
        }

        _trayManager.Dispose();
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        if (!_allowExit)
        {
            System.Windows.Application.Current.Shutdown();
        }
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
            await UnmountSessionAsync(session, promptIfSiblings: false);
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
