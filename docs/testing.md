# テスト方針・トレーサビリティ (`WslDrive`)

本ドキュメントは開発者向けのテスト戦略、自動テストスイート構成、実行手順、および [requirements.md](requirements.md) とのトレーサビリティを定義します。（ユーザー向けのインストール・利用手順は [guides/usage.md](guides/usage.md) に分離しています。）

## 1. テストピラミッドと検証対象

| レイヤー | 配置場所 | 目的 | 実行タイミング |
| :--- | :--- | :--- | :--- |
| Unit | `tests/WslDrive.Tests/` | スーパーブロック解析、MBR/GPT パース、`ro,noload`/`ro`/`rw` 引数構築、`/proc/mounts` 解析、NBD プロトコル、状態遷移ロジック | 常時 (CI) |
| Integration (Mocked) | `tests/WslDrive.Tests/` | `IWslCommandRunner` / `INbdBridgeProvider` モックを用いた複数パーティション・stale 解除・NBD フォールバック・RW 拒否検証 | 常時 (CI) |
| E2E / System (Manual) | Windows 11 実機 | `.vhdx` イメージおよび実物理ドライブを用いたマウント〜エクスプローラー閲覧〜RO/RW 動作〜アンマウント検証 | リリース前 / 該当変更時 |

## 2. テスト実行手順

```powershell
# ビルドと全単体テストの実行 (Release)
dotnet build WslDrive.sln -c Release
dotnet test WslDrive.sln -c Release --no-build

# またはヘルパースクリプトを使用
.\scripts\build-and-test.ps1
```

## 3. 要件・テスト トレーサビリティマトリクス

| 要件 ID | テスト ID / テストクラス・メソッド | テスト種別 | 検証ファイル | ステータス |
| :--- | :--- | :--- | :--- | :--- |
| `REQ-F-001` | `TEST-UNIT-001` (`SuperblockInspectorTests`, `PartitionTableReaderTests`) | Unit | [SuperblockInspectorTests.cs](../tests/WslDrive.Tests/SuperblockInspectorTests.cs), [PartitionTableReaderTests.cs](../tests/WslDrive.Tests/PartitionTableReaderTests.cs) | Passing |
| `REQ-F-002`, `REQ-NF-001` | `TEST-UNIT-002` (`BuildMountArguments_*`, `MountAsync_RetriesWithPlainRo_*`, `MountAsync_ReadWrite_*`, `MountAsync_AlreadyMountedWithDifferentMode_*`) | Unit | [WslMountServiceTests.cs](../tests/WslDrive.Tests/WslMountServiceTests.cs) | Passing |
| `REQ-F-003` | `TEST-UNIT-003` (`UnmountAsync_KeepsDiskAttached*`, `MountAsync_MountsSiblingPartitionDirectly*`, `MountAsync_DetachesAndRetries*`) | Unit | [WslMountServiceTests.cs](../tests/WslDrive.Tests/WslMountServiceTests.cs) | Passing |
| `REQ-F-004`, `REQ-NF-001` | `TEST-UNIT-004` (`ReadOnlyNbdServer_*`, `MountAsync_FallsBackToNbdBridge_*`, `MountAsync_ReadWrite_OnNbdBridge_ReturnsFailure`, `UnmountAsync_DisconnectsNbdDevice_*`) | Unit | [WslMountServiceTests.cs](../tests/WslDrive.Tests/WslMountServiceTests.cs) | Passing |
| `REQ-NF-003` | `TEST-UNIT-005` (`TryParseDosDeviceUncTarget_ParsesDosUncAndP9RdrTargets`) | Unit | [WslMountServiceTests.cs](../tests/WslDrive.Tests/WslMountServiceTests.cs) | Passing |

## 4. Windows 11 上での結合テスト手順（VHDX ＆ 物理ドライブ）

物理ドライブを接続する前に、仮想ディスク（`.vhdx`）を使って安全に一連の動作を検証できます。

### 4.1 VHDX 結合テスト手順

1. **テスト用 `.vhdx` の作成（管理者 PowerShell + WSL）**:
   ```powershell
   # 128MB の VHDX を作成
   diskpart
   # diskpart 内で以下を実行:
   # create vdisk file=C:\temp\test_ext4.vhdx maximum=128 type=expandable
   # exit
   ```
   ```powershell
   # WSL にマウントして GPT + ext4 でフォーマット（※テストデータ作成時のみ RW マウント）
   wsl --mount C:\temp\test_ext4.vhdx --vhd --bare
   # WSL 内でデバイスを確認（例: /dev/sdd）し、パーティション作成＆ext4 フォーマット
   wsl -u root -- bash -c "echo 'type=linux' | sfdisk /dev/sdd && mkfs.ext4 -L TestLinuxData /dev/sdd1"
   wsl -u root -- bash -c "mkdir -p /tmp/mnt && mount /dev/sdd1 /tmp/mnt && echo 'Hello from ext4' > /tmp/mnt/readme.txt && umount /tmp/mnt"
   wsl --unmount C:\temp\test_ext4.vhdx
   ```
2. **WslDrive によるマウント検証**:
   ```powershell
   WslDrive.Cli.exe mount --vhd C:\temp\test_ext4.vhdx --partition 1 --letter L:
   ```
3. **確認チェックリスト**:
   - [ ] `L:\readme.txt` が通常のエクスプローラー上に表示され、中身が読めること。
   - [ ] 既定マウント時に `L:\` へ新規ファイルを作成しようとすると「書き込み禁止（アクセス拒否）」で弾かれ、Read-Only が機能していること。
   - [ ] `WslDrive.Cli.exe status` で `L:` (`RO`) と `/mnt/wsl/...` が表示されること。
   - [ ] `WslDrive.Cli.exe unmount --letter L: --vhd C:\temp\test_ext4.vhdx` で `L:` が消え、`.vhdx` ファイルがアンロックされること。
   - [ ] `WslDrive.Cli.exe mount --vhd C:\temp\test_ext4.vhdx --partition 1 --letter L: --rw` で `status` が `RW` となり、`L:\` への新規ファイル作成・削除が可能であること。

### 4.2 物理ドライブ実機テスト

- Linux がインストールされた別 SSD/HDD を接続し、`WslDrive.App.exe`（または `WslDrive.Cli.exe list`）で `ext4` ラベルと UUID が自動検出されることを確認する。
- `L:` にマウントし、大容量ファイルのコピー、日本語ファイル名の表示、アンマウントが正常に完了することを確認する。

## 5. テストレポート・計測ログ

定点的な検証スナップショットやパフォーマンス計測レポートを作成する場合は、`docs/reports/` 配下に独立レポートとして記録します（[reports/README.md](reports/README.md) 参照）。
