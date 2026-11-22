# WslDrive ビルド・利用・テストガイド

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
dotnet build WslDrive.sln -c Release

# 単体テストの実行
dotnet test WslDrive.sln -c Release

# 単一実行ファイル（Self-Contained）として出力する例（GUIアプリ）
dotnet publish src/WslDrive.App/WslDrive.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish/App

# CLIツールの出力例
dotnet publish src/WslDrive.Cli/WslDrive.Cli.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish/Cli
```

---

## 3. CLI リファレンス (`WslDrive.Cli`)

物理ディスクの生読み取りおよび `wsl --mount` を実行するため、**管理者として実行したターミナル**から利用してください。

### 3.1 ディスク・Linuxパーティション一覧の表示 (`list`)
```powershell
# Linuxパーティションを検出したディスクのみ表示（推奨）
WslDrive.Cli.exe list

# Windowsパーティションを含め全パーティションを表示
WslDrive.Cli.exe list --all

# ディスクイメージファイル（.img / .vhd / .vhdx）の内部パーティションを事前検査
WslDrive.Cli.exe list --image C:\VMs\linux_disk.img
```

### 3.2 マウント ＆ ドライブレター割当 (`mount`)
```powershell
# PhysicalDrive1 の Partition 2 を L: ドライブとして RO マウント（デフォルトで ro,noload 使用）
WslDrive.Cli.exe mount --disk 1 --partition 2 --letter L:

# Read-Write (読み書き可能) でマウントする場合 (--rw または --read-write)
WslDrive.Cli.exe mount --disk 1 --partition 2 --letter L: --rw

# 物理ディスクが1パーティションのみ / パーティション番号省略・マウント名・ディストロ指定の例
WslDrive.Cli.exe mount --disk 2 --partition 1 --letter M: --name mydata --distro Ubuntu

# btrfs 等で noload オプションを使わず通常の ro のみを使う場合
WslDrive.Cli.exe mount --disk 1 --partition 1 --letter L: --fs btrfs --no-noload

# .vhdx 仮想ディスクファイルをマウントする場合（--rw も利用可能）
WslDrive.Cli.exe mount --vhd C:\VMs\data.vhdx --partition 1 --letter L:
```

> [!NOTE]
> Windows がすでに `C:` や `D:` などを使用している**同一物理ディスク上**の Linux パーティション（例: `Disk 0` 上のデュアルブート Linux パーティション）は、`wsl --mount` が `ERROR_SHARING_VIOLATION` となるため、本ツールが自動的に **Read-Only 専用 NBD ブリッジ（`AlignedDiskStream` ＋ WSL2 カーネル `/dev/nbdX`）** に切り替えて対象パーティションのみを安全にマウントします（安全のため NBD ブリッジ経路では `--rw` は使用できません）。別ドライブ（`Disk 2` 等）や `.vhdx` ファイルはネイティブの `wsl --mount` を使用し、RO / RW の両方に対応します。

### 3.3 マウント状態の確認 (`status`)
```powershell
WslDrive.Cli.exe status
```

### 3.4 アンマウント ＆ ドライブ解除 (`unmount`)
```powershell
# ドライブレターを指定してネットワークドライブ解除
# ・他パーティションが同ディスクに残っていなければディスクも自動切り離し
# ・他パーティションが同ディスクでマウント中の場合は該当パーティションのみ個別アンマウント
WslDrive.Cli.exe unmount --letter L:

# 物理ディスク番号のみを指定して、そのディスク上の全ドライブを一括解除・切り離し
WslDrive.Cli.exe unmount --disk 1

# 明示的に指定して解除（--letter と併用時は該当セッションを個別解除）
WslDrive.Cli.exe unmount --letter L: --disk 1

# VHDXをアンマウントする場合
WslDrive.Cli.exe unmount --letter L: --vhd C:\VMs\data.vhdx
```

> [!TIP]
> 同じ物理ディスク上の複数パーティション（例: `--partition 1` を `L:`、`--partition 2` を `M:`）を同時にマウントすることも可能です。`unmount --letter L:` で片方だけを外した場合はディスクのアタッチを維持したまま `L:` のみを安全に解除し、最後の1つ（`M:`）を外したタイミングまたは `unmount --disk 1` 実行時に物理ディスクを WSL2 から切り離します。

---

## 4. GUI アプリ (`WslDrive.App`) の使い方

1. `WslDrive.App.exe` を起動します（マニフェストにより自動で UAC 管理者昇格プロンプトが表示されます。非昇格状態で起動した場合は画面右上の **「管理者として再起動」** ボタンからワンクリックで昇格できます）。
2. 上部の **「① 検出されたディスク ＆ Linux パーティション一覧」** に物理ドライブおよびパーティション（`ext4` / `btrfs` / `xfs`、ボリュームラベル、容量、マウント状態）が自動表示されます。
   - `.vhdx` / `.vhd` / `.img` ファイルをウィンドウへ**ドラッグ＆ドロップ**して直接追加することも可能です。
3. **マウントの実行**:
   - 目的の Linux パーティション行を選択し、中央の **「▶ 選択行をマウント (RO)」** ボタンをクリック（または行をダブルクリック）するだけで、選択中の空きドライブレター（デフォルト `L:`）に `ro,noload` で安全にマウントされ、自動的にエクスプローラーで開きます。
   - 書き込みも行いたい場合は **「読み書き可能 (RW) でマウント」** チェックボックスをオンにするとボタンが **「▶ 選択行をマウント (RW)」** に切り替わり、`rw` オプションでマウントされます（Windows 同居ディスクの NBD ブリッジは安全のため RO 専用です）。
   - Windows (`C:` 等) と同居する物理ドライブ上の Linux パーティションも、自動的に Read-Only NBD ブリッジ経由でマウントできます（`.img` 生イメージ以外の物理 Linux パーティションおよび `.vhdx` に対応）。
   - 同一物理ディスク上の別パーティションも、別のドライブレターを選んで同様に同時マウントできます（下部「現在マウント中の Linux ドライブ」一覧に各ドライブの `RO` / `RW` モードが表示されます）。
4. **マウント中の操作・アンマウント**:
   - 中央の **「⏏ アンマウント」** ボタン、または下部の **「② 現在マウント中の Linux ドライブ」** 一覧の **`📂 開く`** / **`⏏ 解除`** ボタン、あるいは画面上部バナーの **「すべてアンマウント」** ボタンからいつでも安全に取り外せます。
   - 同じ物理ディスク上に他のマウント中パーティションが残っている状態で個別解除を押した場合は、「選択パーティションのみ個別アンマウントする」か「同一ディスクの全パーティションを一括アンマウントして切り離す」かを確認するダイアログが表示されます。
5. **終了時の保護**:
   - ウィンドウ右上の `×` ボタンでアプリを閉じる際、マウント中のドライブが残っている場合は「終了前にすべてアンマウントするか / マウントを維持したまま終了するか」の確認ダイアログが表示されます。

---

## 5. トラブルシューティング（パーミッション制限について）

### Q. エクスプローラーで開いたが、`/root` や他人のUIDのファイルが「アクセス拒否」になる
**理由**: `\\wsl.localhost\<Distro>\mnt\wsl\...` は、WSLディストリビューションの既定ユーザー（通常 `uid=1000`）の権限で参照されるため、Linux側で `700` や `600`（`root` 所有等）に設定されたファイルは通常アクセス拒否になります。

**対処法**:
1. **一時的に確認したい場合**: WSLターミナル側で `sudo ls /mnt/wsl/<マウント名>` として参照する、またはWindows側で見えるディレクトリのみ閲覧する。
2. **別ディストロ（または専用軽量ディストロ）を root デフォルトで用意する方法**:
   閲覧専用の小さなWSLディストロ（Alpineなど）を用意し、そのディストロ内の `/etc/wsl.conf` に以下を設定して `--distro Alpine` でマウントすると、エクスプローラー経由でもすべてのファイルを権限エラーなく操作できるようになります：
   ```ini
   [user]
   default=root
   ```
   （※既定のRead-Onlyマウントであれば、`root` 権限であってもディスクへの書き込みは一切発生しません）

---

## 6. テスト計画・検証手順

### 6.1 自動単体テスト (`WslDrive.Tests`)
- **スーパーブロック解析検証**: `ext2` / `ext3` / `ext4` / `btrfs` / `xfs` / `swap` / `LUKS` のバイナリヘッダを解析し、ファイルシステム種別・ラベル・UUIDが正しく取得できること。
- **GPT / MBR パーティション解析検証**: 複数パーティションを持つ仮想ディスクストリームからオフセットとサイズが正確に計算されること。
- **WSLコマンド生成・出力パース検証**: `ro,noload` / `ro` / `rw` 引数構築、`wsl --list` UTF-16LE出力のパース、`/proc/mounts` 解析（RO/RW判定）が正しいこと。

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
2. **WslDrive によるマウント検証**:
   ```powershell
   WslDrive.Cli.exe mount --vhd C:\temp\test_ext4.vhdx --partition 1 --letter L:
   ```
3. **確認項目**:
   - [ ] `L:\readme.txt` が通常のエクスプローラー上に表示され、中身が読めること。
   - [ ] `L:\` に新規ファイルを作成しようとすると「書き込み禁止（アクセス拒否）」で弾かれ、Read-Onlyが機能していること。
   - [ ] `WslDrive.Cli.exe status` で `L:` (`RO`) と `/mnt/wsl/...` が表示されること。
   - [ ] `WslDrive.Cli.exe unmount --letter L: --vhd C:\temp\test_ext4.vhdx` で `L:` が消え、`.vhdx` ファイルがアンロックされること。
   - [ ] `WslDrive.Cli.exe mount --vhd C:\temp\test_ext4.vhdx --partition 1 --letter L: --rw` で `status` が `RW` となり、`L:\` への新規ファイル作成・削除が可能であること。
4. **物理ドライブ実機テスト**:
   - Linuxがインストールされた別SSD/HDDを接続し、`WslDrive.App.exe`（または `Cli list`）で `ext4` ラベルとUUIDが自動検出されることを確認。
   - `L:` にマウントし、大容量ファイルのコピーや日本語ファイル名の表示、アンマウントが正常に完了することを確認。
