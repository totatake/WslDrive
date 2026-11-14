# LinuxRoDrive ビルド・利用・テストガイド

## 1. 前提環境

- **OS**: Windows 11 (22H2以降推奨。`wsl --mount` がサポートされていること)
- **WSL2**: 有効化済みで、1つ以上のLinuxディストリビューション（Ubuntu等）がインストールされていること
  ```powershell
  wsl --status
  wsl --list --verbose
  ```
- **ビルド環境**: .NET 8.0 SDK (`net8.0-windows`) または Visual Studio 2022

---

## 2. ビルド ＆ 発行 (Publish) 方法

PowerShellまたはコマンドプロンプトからリポジトリルートで実行します。

```powershell
# 依存パッケージの復元とビルド
dotnet build LinuxRoDrive.sln -c Release

# 単体テストの実行
dotnet test LinuxRoDrive.sln -c Release

# 単一実行ファイル（Self-Contained）として出力する例（GUIアプリ）
dotnet publish src/LinuxRoDrive.App/LinuxRoDrive.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish/App

# CLIツールの出力例
dotnet publish src/LinuxRoDrive.Cli/LinuxRoDrive.Cli.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish/Cli
```

---

## 3. CLI リファレンス (`LinuxRoDrive.Cli`)

物理ディスクの生読み取りおよび `wsl --mount` を実行するため、**管理者として実行したターミナル**から利用してください。

### 3.1 ディスク・Linuxパーティション一覧の表示 (`list`)
```powershell
# Linuxパーティションを検出したディスクのみ表示（推奨）
LinuxRoDrive.Cli.exe list

# Windowsパーティションを含め全パーティションを表示
LinuxRoDrive.Cli.exe list --all

# ディスクイメージファイル（.img / .vhd / .vhdx）の内部パーティションを事前検査
LinuxRoDrive.Cli.exe list --image C:\VMs\linux_disk.img
```

### 3.2 Read-Only マウント ＆ ドライブレター割当 (`mount`)
```powershell
# PhysicalDrive1 の Partition 2 を L: ドライブとして RO マウント（デフォルトで ro,noload 使用）
LinuxRoDrive.Cli.exe mount --disk 1 --partition 2 --letter L:

# 物理ディスクが1パーティションのみ / パーティション番号省略・マウント名・ディストロ指定の例
LinuxRoDrive.Cli.exe mount --disk 2 --partition 1 --letter M: --name mydata --distro Ubuntu

# btrfs 等で noload オプションを使わず通常の ro のみを使う場合
LinuxRoDrive.Cli.exe mount --disk 1 --partition 1 --letter L: --fs btrfs --no-noload

# .vhdx 仮想ディスクファイルをマウントする場合
LinuxRoDrive.Cli.exe mount --vhd C:\VMs\data.vhdx --partition 1 --letter L:
```

> [!NOTE]
> `wsl --mount` の仕様上、**WindowsがすでにC:やD:などを使用している物理ディスク全体**をWSLへアタッチすることはできません（`ERROR_SHARING_VIOLATION` になります）。Windowsとは**別の物理ドライブ（別SSD/HDD）**または **`.vhdx` ファイル**を指定してください。

### 3.3 マウント状態の確認 (`status`)
```powershell
LinuxRoDrive.Cli.exe status
```

### 3.4 アンマウント ＆ ドライブ解除 (`unmount`)
```powershell
# ドライブレターを指定してネットワークドライブ解除（関連するマウント名とディスクも自動切り離し）
LinuxRoDrive.Cli.exe unmount --letter L:

# 物理ディスク番号を直接指定してアンマウント
LinuxRoDrive.Cli.exe unmount --disk 1

# 両方を明示して確実に解除
LinuxRoDrive.Cli.exe unmount --letter L: --disk 1

# VHDXをアンマウントする場合
LinuxRoDrive.Cli.exe unmount --letter L: --vhd C:\VMs\data.vhdx
```

---

## 4. GUI ＆ タスクトレイアプリ (`LinuxRoDrive.App`) の使い方

1. `LinuxRoDrive.App.exe` を起動します（マニフェストにより自動でUAC管理者昇格プロンプトが表示されます）。
2. 上部リストに検出された物理ドライブとパーティション（`ext4` / `btrfs` / `xfs`、ボリュームラベル、UUID、容量）が表示されます。
   - **「Windows Volumes」欄に警告が表示されているディスク**はWindowsが使用中であるため `wsl --mount` 対象外です。別ディスクを選択してください。
3. 目的のLinuxパーティションを選択し、下のコントロールバーから：
   - **Drive Letter**（例: `L:`）を選択
   - **Read-Only + No Journal Replay (`ro,noload`)** にチェック（推奨）
   - **Open in Explorer** にチェック
   して **「Mount (RO)」** をクリックします。
4. 完了すると通常権限のWindowsエクスプローラーから `L:\` が開き、通常のドライブと同様にファイル閲覧・コピーが可能になります。
5. ウィンドウを閉じるとシステムトレイに最小化されます（トレイアイコンを右クリックしてマウント解除や完全終了が可能）。

---

## 5. トラブルシューティング（パーミッション制限について）

### Q. エクスプローラーで開いたが、`/root` や他人のUIDのファイルが「アクセス拒否」になる
**理由**: `\\wsl.localhost\<Distro>\mnt\wsl\...` は、WSLディストリビューションの既定ユーザー（通常 `uid=1000`）の権限で参照されるため、Linux側で `700` や `600`（`root` 所有等）に設定されたファイルは通常アクセス拒否になります。

**対処法**:
1. **一時的に確認したい場合**: WSLターミナル側で `sudo ls /mnt/wsl/<マウント名>` として参照する、またはWindows側で見えるディレクトリのみ閲覧する。
2. **別ディストロ（または専用軽量ディストロ）を root デフォルトで用意する方法**:
   閲覧専用の小さなWSLディストロ（Alpineなど）を用意し、そのディストロ内の `/etc/wsl.conf` に以下を設定して `--distro Alpine` でマウントすると、エクスプローラー経由でもすべてのファイルをRead-Onlyで閲覧できるようになります：
   ```ini
   [user]
   default=root
   ```
   （※Read-Onlyマウントされているため、`root` 権限であってもディスクへの書き込みは一切発生しません）

---

## 6. テスト計画・検証手順

### 6.1 自動単体テスト (`LinuxRoDrive.Tests`)
- **スーパーブロック解析検証**: `ext2` / `ext3` / `ext4` / `btrfs` / `xfs` / `swap` / `LUKS` のバイナリヘッダを解析し、ファイルシステム種別・ラベル・UUIDが正しく取得できること。
- **GPT / MBR パーティション解析検証**: 複数パーティションを持つ仮想ディスクストリームからオフセットとサイズが正確に計算されること。
- **WSLコマンド生成・出力パース検証**: `ro,noload` 引数構築、`wsl --list` UTF-16LE出力のパース、`/proc/mounts` 解析が正しいこと。

### 6.2 Windows 11 上での安全な結合テスト手順（VHDX使用）
物理ドライブを接続する前に、仮想ディスク（`.vhdx`）を使って安全に一連の動作を検証できます。

1. **テスト用 `.vhdx` の作成（管理者PowerShell + WSL）**:
   ```powershell
   # 128MBのVHDXを作成してWSLでフォーマットする例
   diskpart
   # diskpart内で以下を実行:
   # create vdisk file=C:\temp\test_ext4.vhdx maximum=128 type=expandable
   # exit
   ```
   ```powershell
   # WSLにマウントしてGPT + ext4でフォーマット（※ここだけテストデータ作成のためRWマウント）
   wsl --mount C:\temp\test_ext4.vhdx --vhd --bare
   # WSL内でデバイスを確認（例: /dev/sdd）し、パーティション作成＆ext4フォーマット
   wsl -u root -- bash -c "echo 'type=linux' | sfdisk /dev/sdd && mkfs.ext4 -L TestLinuxData /dev/sdd1"
   wsl -u root -- bash -c "mkdir -p /tmp/mnt && mount /dev/sdd1 /tmp/mnt && echo 'Hello from ext4' > /tmp/mnt/readme.txt && umount /tmp/mnt"
   wsl --unmount C:\temp\test_ext4.vhdx
   ```
2. **LinuxRoDrive によるマウント検証**:
   ```powershell
   LinuxRoDrive.Cli.exe mount --vhd C:\temp\test_ext4.vhdx --partition 1 --letter L:
   ```
3. **確認項目**:
   - [ ] `L:\readme.txt` が通常のエクスプローラー上に表示され、中身が読めること。
   - [ ] `L:\` に新規ファイルを作成しようとすると「書き込み禁止（アクセス拒否）」で弾かれ、Read-Onlyが機能していること。
   - [ ] `LinuxRoDrive.Cli.exe status` で `L:` と `/mnt/wsl/...` が表示されること。
   - [ ] `LinuxRoDrive.Cli.exe unmount --letter L: --vhd C:\temp\test_ext4.vhdx` で `L:` が消え、`.vhdx` ファイルがアンロックされること。
4. **物理ドライブ実機テスト**:
   - Linuxがインストールされた別SSD/HDDを接続し、`LinuxRoDrive.App.exe`（または `Cli list`）で `ext4` ラベルとUUIDが自動検出されることを確認。
   - `L:` にマウントし、大容量ファイルのコピーや日本語ファイル名の表示、アンマウントが正常に完了することを確認。
