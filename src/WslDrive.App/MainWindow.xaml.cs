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
using WslDrive.Core.Disk;
using WslDrive.Core.Interop;
using WslDrive.Core.Models;
using WslDrive.Core.Wsl;

namespace WslDrive.App;

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
    public string PartitionDisplay => Partition is null ? "1 (自動)" : (Partition.PartitionNumber == 0 ? "RAW" : Partition.PartitionNumber.ToString());
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
            if (ActiveSession is not null)
            {
                string drive = !string.IsNullOrEmpty(ActiveSession.MappedDriveLetter)
                    ? ActiveSession.MappedDriveLetter
                    : ActiveSession.MountName;
                return $"✅ マウント中 ({drive} / {ActiveSession.MountOptions})";
            }
            if (IsVhdImage) return "マウント可能 (.vhdx 仮想ディスク)";
            if (Disk.DiskIndex < 0) return "検査のみ (.img 生イメージ)";
            if (!string.IsNullOrWhiteSpace(Disk.ScanError)) return Disk.ScanError;
            if (Disk.HasWindowsMountedVolumes)
            {
                return Partition is { IsLinuxFileSystem: true }
                    ? $"マウント可能 (Linux FS / Windows同居 {Disk.WindowsDriveLetters}・NBDブリッジ)"
                    : $"Windows使用中 ({Disk.WindowsDriveLetters}) - マウント不可";
            }
            if (Partition is null) return Disk.PartitionStyle;
            return Partition.IsLinuxFileSystem ? "マウント可能 (Linux FS)" : Partition.PartitionTypeDescription;
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

        Loaded += MainWindow_Loaded;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        UpdateAdminBadge();
        UpdateMountModeUi();
        await RefreshAllAsync();
    }

    private void UpdateAdminBadge()
    {
        bool isAdmin = DiskScanner.IsRunningAsAdministrator();
        AdminBadge.Background = new SolidColorBrush(isAdmin
            ? System.Windows.Media.Color.FromRgb(46, 125, 50)
            : System.Windows.Media.Color.FromRgb(183, 28, 28));
        AdminBadgeText.Text = isAdmin ? "管理者権限 (OK)" : "非管理者 (制限あり)";
        RelaunchAdminButton.Visibility = isAdmin ? Visibility.Collapsed : Visibility.Visible;
    }

    private async Task RefreshAllAsync()
    {
        SetBusy(true);
        try
        {
            AppendLog("物理ディスクおよび WSL2 マウント状態をスキャンしています...");
            await RefreshDistrosAsync();

            _scannedDisks = await Task.Run(() => _diskScanner.ScanPhysicalDisks());
            await RefreshActiveMountsAndDriveLettersAsync();

            int linuxPartCount = _scannedDisks.Sum(d => d.Partitions.Count(p => p.IsLinuxFileSystem));
            AppendLog($"スキャン完了: 物理ディスク {_scannedDisks.Count} 台、Linux パーティション {linuxPartCount} 件を検出しました。");
        }
        catch (Exception ex)
        {
            AppendLog($"スキャン中にエラーが発生しました: {ex.Message}");
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
        if (_activeMounts.Count > 0)
        {
            string drives = string.Join(", ", _activeMounts.Select(m => $"{m.MappedDriveLetter ?? m.MountName} ({m.AccessModeDisplay})"));
            StatusBanner.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(232, 245, 233));
            StatusBanner.BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(165, 214, 167));
            StatusBannerText.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(27, 94, 32));
            StatusBannerText.Text = $"🟢 現在 {_activeMounts.Count} 件の Linux パーティションをマウント中です ({drives})。";
            BannerUnmountAllButton.Visibility = Visibility.Visible;
        }
        else
        {
            StatusBanner.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(235, 243, 254));
            StatusBanner.BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(182, 212, 254));
            StatusBannerText.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(8, 66, 152));
            StatusBannerText.Text = "💡 下のリストから Linux パーティションを選択して「選択行をマウント」を押すだけで、すぐにエクスプローラーで開けます（デフォルト: RO / RW 切替可、VHDX のドラッグ＆ドロップにも対応）。";
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
            string expectedWithPart = partNum > 0 ? $"wsldrive_d{disk.DiskIndex}p{partNum}" : $"wsldrive_d{disk.DiskIndex}";
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
        MountButton.Content = isRw ? "▶ 選択行をマウント (RW)" : "▶ 選択行をマウント (RO)";
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
            AppendLog($"管理者としての再起動がキャンセルまたは失敗しました: {ex.Message}");
        }
    }

    private void InspectImageButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "ディスクイメージ / VHDX ファイルを選択 (.vhdx / .vhd / .img)",
            Filter = "ディスクイメージ (*.vhdx;*.vhd;*.img;*.raw)|*.vhdx;*.vhd;*.img;*.raw|すべてのファイル (*.*)|*.*"
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
            AppendLog($"イメージを検査・追加しました: {filePath} ({inspected.PartitionStyle})");
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, $"イメージの読み取りに失敗しました:\n{ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
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

    private async void ActiveRowUnmountButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: MountSession session })
        {
            await UnmountSessionAsync(session);
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
            System.Windows.MessageBox.Show(this, "マウントする Linux パーティションまたは VHDX 行を選択してください。", "選択なし", MessageBoxButton.OK, MessageBoxImage.Information);
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
        if (!row.IsVhdImage && row.Disk.DiskIndex < 0)
        {
            System.Windows.MessageBox.Show(
                this,
                "生イメージ (.img) は `wsl --mount` に直接渡せません。.vhdx 形式に変換するか、物理ディスクまたは .vhdx ファイルを選択してください。",
                "非対応のイメージ形式",
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
                        "Windows 使用中の物理ディスク（NBD ブリッジ方式）は安全のため読み取り専用 (RO) マウントのみ対応しています。\n読み取り専用でマウントするか、別の物理ディスク / .vhdx を使用してください。",
                        "RW マウント非対応",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }
                AppendLog($"Windows 使用中の物理ディスク ({row.Disk.WindowsDriveLetters}) のため、Read-Only NBD ブリッジ方式でマウントします...");
            }
            else
            {
                var confirm = System.Windows.MessageBox.Show(
                    this,
                    $"Disk {row.Disk.DiskIndex} ({row.Disk.Model}) には現在 Windows が使用中のドライブ [{row.Disk.WindowsDriveLetters}] が含まれています。\n`wsl --mount` は Windows 使用中の物理ディスクをアタッチできません。\n\nそれでもマウントを試行しますか？",
                    "Windows 使用中ディスクの警告",
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
                System.Windows.MessageBox.Show(this, "利用可能な空きドライブレターがありません。", "ドライブレター不足", MessageBoxButton.OK, MessageBoxImage.Warning);
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
                AppendLog($"マウント失敗: {result.Message}");
                System.Windows.MessageBox.Show(this, result.Message, "マウント失敗", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            if (!string.IsNullOrWhiteSpace(result.Session.TargetWslDiskPath))
            {
                _mountNameToTargetPath[result.Session.MountName] = result.Session.TargetWslDiskPath;
            }

            AppendLog($"成功: {result.Message}");
            string modeText = result.Session.IsReadOnly ? "読み取り専用 (RO)" : "読み書き可能 (RW)";
            _trayManager.ShowBalloon("WslDrive - マウント完了", $"{result.Session.MappedDriveLetter}\\ に {modeText} でマウントしました。");
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

        System.Windows.MessageBox.Show(this, "アンマウントする行を選択してください。", "アンマウント", MessageBoxButton.OK, MessageBoxImage.Information);
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

            AppendLog(result.Success ? $"WSL ディスク切り離し完了: {result.Message}" : $"WSL ディスク切り離し結果: {result.Message}");
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
                    $"同じディスク上に他にもマウント中のパーティションがあります ({siblingList})。\n\n[はい]: 選択した {sessionDrive} ({session.MountName}) のみ個別にアンマウントする\n[いいえ]: このディスクの全パーティションを一括アンマウントして切り離す\n[キャンセル]: キャンセル",
                    "複数パーティションのアンマウント確認",
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

            AppendLog(result.Success ? $"アンマウント完了: {result.Message}" : $"アンマウント警告: {result.Message}");
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
                $"現在 {_activeMounts.Count} 件の Linux パーティションがマウント中です。\n終了前にすべてアンマウント（安全な取り外し）しますか？\n\n[はい]: すべてアンマウントして終了\n[いいえ]: マウントを維持したまま終了\n[キャンセル]: 終了しない",
                "WslDrive の終了確認",
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
