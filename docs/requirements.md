# 要件定義書 (`WslDrive`)

本ドキュメントでは、**WslDrive** が満たすべき機能要件および非機能要件を定義します。各要件 ID は [testing.md](testing.md) のテスト仕様およびトレーサビリティ表と対応付けます。

## 1. 機能要件 (Functional Requirements)

| ID | 要件名 | 内容・受入基準 | 優先度 | ステータス |
| :--- | :--- | :--- | :--- | :--- |
| `REQ-F-001` | 物理ディスク・パーティション事前自動検出 | 接続中の物理ドライブ・イメージ（`.img` / `.vhd` / `.vhdx`）の MBR/EBR/GPT およびスーパーブロックを解析し、マウント前に FS 種別・ラベル・UUID・サイズを一覧表示できること。 | Must | Implemented |
| `REQ-F-002` | RO/RW マウント ＆ ドライブレター割当 | 指定パーティションまたは `.vhdx` を `wsl --mount` で `/mnt/wsl/<MountName>` にマウントし（既定 `ro,noload` / 任意 `rw`）、指定ドライブレターへ `DefineDosDeviceW` で割り当ててエクスプローラーと管理者ターミナル双方からアクセス可能にすること。 | Must | Implemented |
| `REQ-F-003` | 安全なアンマウント ＆ マルチパーティション制御 | ドライブレター解除、個別 `umount`、および `wsl --unmount` を整合ある順序で実行し、同一ディスク上の他パーティションが残存する場合は個別解除・最後の1件または全体指定時はディスク切り離しを行うこと。 | Must | Implemented |
| `REQ-F-004` | Windows 同居ディスク向け Read-Only NBD ブリッジ | Windows ボリュームと同居する物理ディスク（`HasWindowsMountedVolumes` / `ERROR_SHARING_VIOLATION`）に対し、`AlignedDiskStream` ＋ WSL2 `/dev/nbdX` 経由で Read-Only マウントでき、NBD 経路での RW 要求は拒否すること。 | Must | Implemented |
| `REQ-F-005` | CLI ＆ WPF GUI ハイブリッド提供 | `WslDrive.Core` を共有し、`WslDrive.Cli`（`list` / `mount` / `unmount` / `status`）と `WslDrive.App`（WPF ウィンドウ＋タスクトレイ常駐＋RO/RW 切替 UI）を提供すること。 | Must | Implemented |

### 詳細仕様

#### `REQ-F-001`: 物理ディスク・パーティション事前自動検出 (`DiskScanner` / `PartitionTableReader` / `SuperblockInspector`)
- **入力**: Windows 物理ドライブ（`\\.\PhysicalDrive0`, `1`, ...）またはディスクイメージファイル（`.img` / `.vhd` / `.vhdx`）。
- **処理 / 契約**: WMI と Win32 API（`CreateFileW` の `GENERIC_READ`）で走査し、各物理ドライブが Windows マウント済みボリューム（`C:` 等）を含むか判定する。MBR / EBR / GPT / Raw FS を解析し、`ext2/3/4`（`+1024`）、`btrfs`（`+0x10000`）、`xfs`（`+0x00`）、`swap`、`LUKS` のスーパーブロックからラベル・UUID を抽出する。
- **出力 / エラー条件**: `PhysicalDiskEntry` および `PartitionEntry` の一覧。権限不足や I/O エラー時は `ScanError` に詳細を格納する。

#### `REQ-F-002`: RO/RW マウント ＆ ドライブレター割当 (`WslMountService` / `DriveMapper`)
- **入力**: `MountRequest`（`DiskIndex` または `VhdPath`、`PartitionNumber`、`DriveLetter`、`ReadWrite`、`UseNoLoadJournal`、`DistroName` 等）。
- **処理 / 契約**:
  - `ReadWrite == false`（既定）時は `ro,noload` を付与し、`noload` 非対応 FS / カーネルでは自動的に `ro` へフォールバックする。
  - `ReadWrite == true` 時は `--options rw` でマウントする（`UseNoLoadJournal` は無視）。
  - 既に `/mnt/wsl/<MountName>` がアクティブな場合、要求モード（RO/RW）と一致すれば再利用し、不一致の場合はエラーを返して混同を防ぐ。
- **出力 / エラー条件**: `MountOperationResult`（`MountSession` と割り当て結果）。ドライブマッピング失敗時は WSL マウントをロールバックする。

#### `REQ-F-003`: 安全なアンマウント ＆ マルチパーティション制御 (`WslMountService.UnmountAsync`)
- **入力**: `DriveLetter`、`DiskIndex`、`VhdPath`、`MountName`、`ForceWholeDisk` のいずれかまたは組み合わせ。
- **処理 / 契約**:
  - 同一物理ディスク・ベースデバイスに他パーティションが残存し個別アンマウントの場合のみ、ドライブ解除＋対象マウントポイントの `umount` を行いディスクを保持する。
  - 最後の 1 件またはディスク全体解除時は、先行 `umount` を行わずに `wsl --unmount <target>` を実行し、失敗時は `/mnt/wsl/wsldrive_*` の自己バインドマウント補完後にリトライする。
- **出力 / エラー条件**: `UnmountOperationResult`。

#### `REQ-F-004`: Windows 同居ディスク向け Read-Only NBD ブリッジ (`ReadOnlyNbdServer` / `NbdDiskBridge`)
- **入力**: `DiskIndex`、`PartitionOffsetBytes`、`PartitionSizeBytes`、`SectorSize`、`DistroName`。
- **処理 / 契約**: `GENERIC_READ` のみの `AlignedDiskStream` で対象パーティション範囲に限定し、WSL2 内の `modprobe nbd` + `python3` ioctl（`NBD_FLAG_READ_ONLY`）で `/dev/nbdX` を構成する。`NBD_CMD_WRITE` / `NBD_CMD_WRITE_ZEROES` は `EROFS (30)` で拒否する。
- **出力 / エラー条件**: `ReadWrite == true` 要求時は即座に失敗レスポンスを返す。

#### `REQ-F-005`: CLI ＆ WPF GUI ハイブリッド提供 (`WslDrive.Cli` / `WslDrive.App`)
- **入力**: CLI 引数（`--rw` / `--ro` / `--no-noload` 等）または GUI 操作（選択行、チェックボックス、ドラッグ＆ドロップ）。
- **処理 / 契約**: 既定は RO（青色ボタン・`Read-Only (ro,noload)` バッジ）、RW 選択時は明確に視覚差別化（アンバー色ボタン・`Read-Write (rw)` バッジ、`NoLoadCheckBox` 無効化）し、一覧やトレイメニューに `RO` / `RW` を表示する。

## 2. 非機能要件 (Non-Functional Requirements)

| ID | カテゴリ | 要件名 | 定量目標 / 制約 | ステータス |
| :--- | :--- | :--- | :--- | :--- |
| `REQ-NF-001` | Safety / Security | 既定 Read-Only 保証（Safe by Default） | 明示的 RW 指定がない限り `ro,noload`（または `ro`）を使用し、物理ディスクスキャンは `GENERIC_READ` のみ、NBD ブリッジは 3 重の Write 拒否を行うこと。 | Implemented |
| `REQ-NF-002` | Portability | 外部ドライバ不要・軽量構成 | .NET 8 (`net8.0-windows`) 単体でビルドでき、Windows 11 + WSL2 標準機能のみで動作し、Win-x64 単一実行ファイルとして配布可能であること。 | Implemented |
| `REQ-NF-003` | Usability | UAC 透過ドライブアクセス | レジストリ（`EnableLinkedConnections`）変更や OS 再起動なしに、通常ユーザーのエクスプローラーと管理者ターミナルの双方へ即時ドライブ公開できること。 | Implemented |

## 3. 対象外・前提制約 (Out of Scope / Constraints)

- 生ディスクイメージ（`.img` / `.raw`）の直接マウント（`wsl --mount` 非対応のため事前構造検査のみ対応し、`.vhdx` 変換を案内）。
- Windows 使用中物理ディスク（NBD ブリッジ経由）での Read-Write マウント（Windows 側ファイルシステムとの競合・破損防止のため対象外）。
- LUKS 暗号化ボリュームの自動復号（スーパーブロックによる LUKS 検出・種別表示のみ対応）。
