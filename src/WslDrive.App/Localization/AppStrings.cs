using System;
using System.ComponentModel;
using System.Globalization;

namespace WslDrive.App.Localization;

public enum UiLanguageMode
{
    Auto,
    Japanese,
    English
}

/// <summary>
/// Provides localized UI strings for Japanese and English, defaulting to Auto based on OS UI culture.
/// </summary>
public sealed class AppStrings : INotifyPropertyChanged
{
    public static AppStrings Instance { get; } = new();

    private UiLanguageMode _mode = UiLanguageMode.Auto;

    public event PropertyChangedEventHandler? PropertyChanged;

    public UiLanguageMode Mode
    {
        get => _mode;
        set
        {
            if (_mode == value) return;
            _mode = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
        }
    }

    public bool IsJapanese => _mode switch
    {
        UiLanguageMode.Japanese => true,
        UiLanguageMode.English => false,
        _ => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("ja", StringComparison.OrdinalIgnoreCase)
    };

    private string T(string ja, string en) => IsJapanese ? ja : en;

    // Window & Header
    public string WindowTitle => T("WslDrive - Linux パーティション マウンター (WSL2)", "WslDrive - Linux Partition Mounter (WSL2)");
    public string HeaderSubtitle => T(
        "別ドライブの Linux パーティション (ext4 / btrfs / xfs) を Windows ドライブ (L: 等) として安全にマウント",
        "Safely mount Linux partitions (ext4 / btrfs / xfs) as Windows drive letters (e.g. L:) via WSL2");
    public string WslDistroLabel => T("使用 WSL:", "WSL Distro:");
    public string LanguageLabel => T("言語:", "Lang:");
    public string AdminOk => T("管理者権限 (OK)", "Administrator (OK)");
    public string AdminLimited => T("非管理者 (制限あり)", "Not Admin (Limited)");
    public string RelaunchAdmin => T("管理者として再起動", "Relaunch as Admin");

    // Status Banner
    public string UnmountAll => T("すべてアンマウント", "Unmount All");
    public string StatusActiveMounts(int count, string drives) => T(
        $"🟢 現在 {count} 件の Linux パーティションをマウント中です ({drives})。",
        $"🟢 {count} Linux partition(s) currently mounted ({drives}).");
    public string StatusIdle => T(
        "💡 下のリストから Linux パーティションを選択して「選択行をマウント」を押すだけで、すぐにエクスプローラーで開けます（デフォルト: RO / RW 切替可、VHDX のドラッグ＆ドロップにも対応）。",
        "💡 Select a Linux partition below and click 'Mount Selected' to open it in File Explorer (Default: RO / RW toggleable, VHDX drag & drop supported).");

    // Toolbar
    public string RefreshDisks => T("🔄 ディスク再スキャン", "🔄 Rescan Disks");
    public string OpenImage => T("📂 VHDX / イメージを開く...", "📂 Open VHDX / Image...");
    public string OnlyLinuxPartitions => T("Linux パーティションのみ表示", "Show Linux partitions only");

    // Partitions Grid
    public string PartitionsHeader => T("① 検出されたディスク ＆ Linux パーティション一覧", "1. Detected Disks & Linux Partitions");
    public string ColDisk => T("ディスク", "Disk");
    public string ColModel => T("モデル名 / ソース", "Model / Source");
    public string ColPartition => T("番号", "Part");
    public string ColFs => T("FS種別", "FS");
    public string ColLabel => T("ラベル", "Label");
    public string ColSize => T("サイズ", "Size");
    public string ColDrive => T("割当", "Drive");
    public string ColStatus => T("状態", "Status");

    public string PartitionAuto => T("1 (自動)", "1 (Auto)");
    public string StatusMounted(string drive, string options) => T($"✅ マウント中 ({drive} / {options})", $"✅ Mounted ({drive} / {options})");
    public string StatusVhdxMountable => T("マウント可能 (.vhdx 仮想ディスク)", "Mountable (.vhdx virtual disk)");
    public string StatusRawImgInspectOnly => T("検査のみ (.img 生イメージ)", "Inspect only (.img raw image)");
    public string StatusSharedNbdMountable(string letters) => T(
        $"マウント可能 (Linux FS / Windows同居 {letters}・NBDブリッジ)",
        $"Mountable (Linux FS / Shared with Windows {letters}, NBD bridge)");
    public string StatusWindowsInUseNotMountable(string letters) => T(
        $"Windows使用中 ({letters}) - マウント不可",
        $"In use by Windows ({letters}) - Not mountable");
    public string StatusLinuxMountable => T("マウント可能 (Linux FS)", "Mountable (Linux FS)");

    // Mount Settings & Action Bar
    public string DriveLetterLabel => T("割当ドライブレター:", "Drive Letter:");
    public string ReadWriteCheck => T("読み書き可能 (RW) でマウント", "Mount Read-Write (RW)");
    public string ReadWriteToolTip => T(
        "チェックを入れると読み書き可能 (rw) でマウントします。未チェック時は安全な読み取り専用 (ro) でマウントします。",
        "Check to mount Read-Write (rw). Unchecked mounts safely as Read-Only (ro).");
    public string NoLoadCheck => T("ジャーナル再生を抑止 (ro,noload)", "Skip journal replay (ro,noload)");
    public string NoLoadToolTip => T(
        "ext4の未クリーン終了時でもディスクへの書き込みを一切行いません（RO 時の推奨設定）",
        "Prevents any disk writes even on dirty ext4 filesystems (recommended for RO).");
    public string OpenExplorerCheck => T("マウント後にエクスプローラーで自動的に開く", "Open in File Explorer after mount");
    public string MountSelectedRw => T("▶ 選択行をマウント (RW)", "▶ Mount Selected (RW)");
    public string MountSelectedRo => T("▶ 選択行をマウント (RO)", "▶ Mount Selected (RO)");
    public string UnmountSelected => T("⏏ 選択行をアンマウント", "⏏ Unmount Selected");
    public string UnmountSelectedToolTip => T(
        "選択中の行（またはマウント中のドライブ）をアンマウントし、ドライブ割り当てを解除します。",
        "Unmount the selected row (or active mount) and remove its drive letter mapping.");

    // Active Mounts Grid
    public string ActiveMountsHeader => T("② 現在マウント中の Linux ドライブ (WSL2 /mnt/wsl)", "2. Active Linux Mounts (WSL2 /mnt/wsl)");
    public string ColActiveDrive => T("ドライブ", "Drive");
    public string ColActiveMode => T("モード", "Mode");
    public string ColActiveMountName => T("マウント名", "Mount Name");
    public string ColActiveFs => "FS";
    public string ColActiveDevice => T("WSL デバイス", "WSL Device");
    public string ColActiveOptions => T("オプション", "Options");
    public string ColActiveUnc => T("UNC パス", "UNC Path");
    public string ColActiveActions => T("操作", "Actions");
    public string ActiveRowOpenButton => T("📂 開く", "📂 Open");

    // Log Expander
    public string LogHeader => T("実行ログ・詳細メッセージ", "Activity Log & Details");
    public string CopyLog => T("ログをコピー", "Copy Log");
    public string ClearLog => T("クリア", "Clear");

    // Log Messages & Dialogs
    public string LogScanning => T("物理ディスクおよび WSL2 マウント状態をスキャンしています...", "Scanning physical disks and WSL2 mount status...");
    public string LogScanComplete(int diskCount, int linuxPartCount) => T(
        $"スキャン完了: 物理ディスク {diskCount} 台、Linux パーティション {linuxPartCount} 件を検出しました。",
        $"Scan complete: detected {diskCount} physical disk(s) and {linuxPartCount} Linux partition(s).");
    public string LogScanError(string error) => T($"スキャン中にエラーが発生しました: {error}", $"Error during scan: {error}");
    public string LogRelaunchFailed(string error) => T($"管理者としての再起動がキャンセルまたは失敗しました: {error}", $"Administrator relaunch was canceled or failed: {error}");
    public string OpenImageDialogTitle => T("ディスクイメージ / VHDX ファイルを選択 (.vhdx / .vhd / .img)", "Select Disk Image / VHDX File (.vhdx / .vhd / .img)");
    public string OpenImageDialogFilter => T(
        "ディスクイメージ (*.vhdx;*.vhd;*.img;*.raw)|*.vhdx;*.vhd;*.img;*.raw|すべてのファイル (*.*)|*.*",
        "Disk Images (*.vhdx;*.vhd;*.img;*.raw)|*.vhdx;*.vhd;*.img;*.raw|All Files (*.*)|*.*");
    public string LogImageAdded(string filePath, string style) => T($"イメージを検査・追加しました: {filePath} ({style})", $"Inspected and added image: {filePath} ({style})");
    public string ErrorTitle => T("エラー", "Error");
    public string ImageReadError(string error) => T($"イメージの読み取りに失敗しました:\n{error}", $"Failed to read disk image:\n{error}");
    public string NoSelectionMsg => T("マウントする Linux パーティションまたは VHDX 行を選択してください。", "Please select a Linux partition or VHDX row to mount.");
    public string NoSelectionTitle => T("選択なし", "No Selection");
    public string RawImgUnsupportedMsg => T(
        "生イメージ (.img) は `wsl --mount` に直接渡せません。.vhdx 形式に変換するか、物理ディスクまたは .vhdx ファイルを選択してください。",
        "Raw disk images (.img) cannot be passed directly to `wsl --mount`. Convert to .vhdx or select a physical disk or .vhdx file.");
    public string UnsupportedImageFormatTitle => T("非対応のイメージ形式", "Unsupported Image Format");
    public string NbdRwUnsupportedMsg => T(
        "Windows 使用中の物理ディスク（NBD ブリッジ方式）は安全のため読み取り専用 (RO) マウントのみ対応しています。\n読み取り専用でマウントするか、別の物理ディスク / .vhdx を使用してください。",
        "Physical disks shared with Windows (NBD bridge) only support Read-Only (RO) mounts for safety.\nMount as Read-Only, or use a separate physical disk or .vhdx file.");
    public string NbdRwUnsupportedTitle => T("RW マウント非対応", "RW Mount Not Supported");
    public string LogUsingNbdBridge(string letters) => T(
        $"Windows 使用中の物理ディスク ({letters}) のため、Read-Only NBD ブリッジ方式でマウントします...",
        $"Physical disk is shared with Windows ({letters}); mounting via Read-Only NBD bridge...");
    public string WindowsInUseWarningMsg(int diskIndex, string model, string letters) => T(
        $"Disk {diskIndex} ({model}) には現在 Windows が使用中のドライブ [{letters}] が含まれています。\n`wsl --mount` は Windows 使用中の物理ディスクをアタッチできません。\n\nそれでもマウントを試行しますか？",
        $"Disk {diskIndex} ({model}) contains active Windows drive(s) [{letters}].\n`wsl --mount` cannot attach a physical disk in use by Windows.\n\nAttempt to mount anyway?");
    public string WindowsInUseWarningTitle => T("Windows 使用中ディスクの警告", "Windows-Shared Disk Warning");
    public string NoFreeDriveLettersMsg => T("利用可能な空きドライブレターがありません。", "No free Windows drive letters are available.");
    public string NoFreeDriveLettersTitle => T("ドライブレター不足", "No Free Drive Letters");
    public string LogMountFailed(string msg) => T($"マウント失敗: {msg}", $"Mount failed: {msg}");
    public string MountFailedTitle => T("マウント失敗", "Mount Failed");
    public string LogSuccess(string msg) => T($"成功: {msg}", $"Success: {msg}");
    public string BalloonMountCompleteTitle => T("WslDrive - マウント完了", "WslDrive - Mount Complete");
    public string BalloonMountCompleteBody(string? driveLetter, bool isReadOnly)
    {
        string modeText = isReadOnly ? T("読み取り専用 (RO)", "Read-Only (RO)") : T("読み書き可能 (RW)", "Read-Write (RW)");
        return T($"{driveLetter}\\ に {modeText} でマウントしました。", $"Mounted at {driveLetter}\\ as {modeText}.");
    }
    public string SelectUnmountRowMsg => T("アンマウントする行を選択してください。", "Please select a row to unmount.");
    public string UnmountTitle => T("アンマウント", "Unmount");
    public string LogWslDetachResult(bool success, string msg) => success
        ? T($"WSL ディスク切り離し完了: {msg}", $"WSL disk detached: {msg}")
        : T($"WSL ディスク切り離し結果: {msg}", $"WSL disk detach result: {msg}");
    public string MultiPartitionUnmountMsg(string siblingList, string sessionDrive, string mountName) => T(
        $"同じディスク上に他にもマウント中のパーティションがあります ({siblingList})。\n\n[はい]: 選択した {sessionDrive} ({mountName}) のみ個別にアンマウントする\n[いいえ]: このディスクの全パーティションを一括アンマウントして切り離す\n[キャンセル]: キャンセル",
        $"Other partitions on the same disk are currently mounted ({siblingList}).\n\n[Yes]: Unmount only {sessionDrive} ({mountName})\n[No]: Unmount all partitions on this disk and detach\n[Cancel]: Cancel");
    public string MultiPartitionUnmountTitle => T("複数パーティションのアンマウント確認", "Confirm Multi-Partition Unmount");
    public string LogUnmountResult(bool success, string msg) => success
        ? T($"アンマウント完了: {msg}", $"Unmounted: {msg}")
        : T($"アンマウント警告: {msg}", $"Unmount warning: {msg}");
    public string ExitConfirmMsg(int count) => T(
        $"現在 {count} 件の Linux パーティションがマウント中です。\n終了前にすべてアンマウント（安全な取り外し）しますか？\n\n[はい]: すべてアンマウントして終了\n[いいえ]: マウントを維持したまま終了\n[キャンセル]: 終了しない",
        $"{count} Linux partition(s) are currently mounted.\nUnmount all before exiting?\n\n[Yes]: Unmount all and exit\n[No]: Exit keeping mounts active\n[Cancel]: Do not exit");
    public string ExitConfirmTitle => T("WslDrive の終了確認", "Confirm WslDrive Exit");

    // Tray Icon
    public string TrayTooltipIdle => T("WslDrive - Linux パーティション マウンター (WSL2)", "WslDrive - Linux Partition Mounter (WSL2)");
    public string TrayTooltipActive(int count) => T($"WslDrive ({count} 件マウント中)", $"WslDrive ({count} mounted)");
    public string TrayOpenWindow => T("WslDrive ウィンドウを開く", "Open WslDrive Window");
    public string TrayNoMounts => T("現在マウント中のドライブはありません", "No drives currently mounted");
    public string TrayOpenInExplorer(string drive) => T($"{drive}\\ をエクスプローラーで開く", $"Open {drive}\\ in File Explorer");
    public string TrayUnmountSafely => T("アンマウント（安全な取り外し）", "Unmount (Safe Removal)");
    public string TrayUnmountAllAndExit => T("すべてアンマウントして終了", "Unmount All & Exit");
    public string TrayExitKeepMounts => T("マウントを維持したままアプリを終了", "Exit Keeping Mounts");
}
