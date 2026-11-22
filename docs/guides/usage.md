# 利用ガイド (`WslDrive`)

ユーザー・運用者向けの動作環境、ビルド＆発行方法、CLI リファレンス、および GUI アプリ（`WslDrive.App`）の使い方をまとめます。エラーやパーミッション制限への対処は [troubleshooting.md](troubleshooting.md) を参照してください。

## 1. 前提環境

- **OS**: Windows 11（22H2 以降推奨。`wsl --mount` がサポートされていること）
- **WSL2**: 有効化済みで、1 つ以上の Linux ディストリビューション（Ubuntu 等）がインストールされていること
  ```powershell
  wsl --status
  wsl --list --verbose
  ```
- **ビルド環境**（ソースからビルドする場合）: .NET 8.0 SDK (`net8.0-windows`) または Visual Studio 2022

## 2. ビルド ＆ 発行 (Publish) 方法

PowerShell またはコマンドプロンプトからリポジトリルートで実行します。

```powershell
# 依存パッケージの復元とビルド
dotnet build WslDrive.sln -c Release

# 単体テストの実行
dotnet test WslDrive.sln -c Release

# 単一実行ファイル（Self-Contained）として出力する例（GUI アプリ）
dotnet publish src/WslDrive.App/WslDrive.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish/App

# CLI ツールの出力例
dotnet publish src/WslDrive.Cli/WslDrive.Cli.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish/Cli
```

または同梱のヘルパースクリプトを使用できます：

```powershell
.\scripts\build-and-test.ps1 -Publish
```

## 3. CLI リファレンス (`WslDrive.Cli`)

物理ディスクの生読み取りおよび `wsl --mount` を実行するため、**管理者として実行したターミナル**から利用してください。

### 3.1 ディスク・Linux パーティション一覧の表示 (`list`)

```powershell
# Linux パーティションを検出したディスクのみ表示（推奨）
WslDrive.Cli.exe list

# Windows パーティションを含め全パーティションを表示
WslDrive.Cli.exe list --all

# ディスクイメージファイル（.img / .vhd / .vhdx）の内部パーティションを事前検査
WslDrive.Cli.exe list --image C:\VMs\linux_disk.img
```

### 3.2 マウント ＆ ドライブレター割当 (`mount`)

| オプション | デフォルト | 説明 |
| :--- | :--- | :--- |
| `--disk, -d <index>` | — | Windows 物理ディスク番号（例: `1` for `\\.\PhysicalDrive1`） |
| `--vhd <path>` | — | `.vhd` / `.vhdx` ファイルパス（`--disk` と排他） |
| `--partition, -p <num>` | 自動検出 | マウント対象の 1 始まりパーティション番号 |
| `--letter, -l <L:>` | 空き先頭文字 | 割り当てる Windows ドライブレター |
| `--name, -n <name>` | `wsldrive_*` | `/mnt/wsl/<name>` のマウント名 |
| `--fs, --type, -t <type>` | 自動判定 | ファイルシステム種別（`ext4`, `btrfs`, `xfs` 等） |
| `--rw` / `--read-write` | `false` (RO) | Read-Write（`rw`）モードでマウント |
| `--ro` / `--read-only` | `true` (既定) | 明示的に Read-Only モードを指定 |
| `--no-noload` | 既定で有効 | ext3/ext4 の RO マウント時に `noload` を付与しない（単純 `ro`） |
| `--distro <name>` | 既定ディストロ | UNC 公開に使う WSL ディストリビューション名 |

```powershell
# PhysicalDrive1 の Partition 2 を L: ドライブとして RO マウント（既定: ro,noload）
WslDrive.Cli.exe mount --disk 1 --partition 2 --letter L:

# Read-Write (読み書き可能) でマウントする場合 (--rw または --read-write)
WslDrive.Cli.exe mount --disk 1 --partition 2 --letter L: --rw

# パーティション番号省略・マウント名・ディストロ指定の例
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

# VHDX をアンマウントする場合
WslDrive.Cli.exe unmount --letter L: --vhd C:\VMs\data.vhdx
```

> [!TIP]
> 同じ物理ディスク上の複数パーティション（例: `--partition 1` を `L:`、`--partition 2` を `M:`）を同時にマウントすることも可能です。`unmount --letter L:` で片方だけを外した場合はディスクのアタッチを維持したまま `L:` のみを安全に解除し、最後の 1 つ（`M:`）を外したタイミングまたは `unmount --disk 1` 実行時に物理ディスクを WSL2 から切り離します。

## 4. GUI アプリ (`WslDrive.App`) の使い方

1. `WslDrive.App.exe` を起動します（マニフェストにより自動で UAC 管理者昇格プロンプトが表示されます。非昇格状態で起動した場合は画面右上の **「管理者として再起動」** ボタンからワンクリックで昇格できます）。
   - 表示言語は画面右上のプルダウン（`Auto` / `日本語` / `English`）から切り替えられます（既定は `Auto` で、OS の表示言語に応じて日本語または英語を自動選択します）。
2. 上部の **「① 検出されたディスク ＆ Linux パーティション一覧」** に物理ドライブおよびパーティション（`ext4` / `btrfs` / `xfs`、ボリュームラベル、容量、マウント状態）が自動表示されます。
   - `.vhdx` / `.vhd` / `.img` ファイルをウィンドウへ**ドラッグ＆ドロップ**して直接追加することも可能です。
3. **マウントの実行**:
   - 目的の Linux パーティション行を選択し、中央の **「▶ 選択行をマウント (RO)」** ボタンをクリック（または行をダブルクリック）するだけで、選択中の空きドライブレター（デフォルト `L:`）に `ro,noload` で安全にマウントされ、自動的にエクスプローラーで開きます。
   - 書き込みも行いたい場合は **「読み書き可能 (RW) でマウント」** チェックボックスをオンにするとボタンが **「▶ 選択行をマウント (RW)」** に切り替わり、`rw` オプションでマウントされます（Windows 同居ディスクの NBD ブリッジは安全のため RO 専用です）。
   - Windows (`C:` 等) と同居する物理ドライブ上の Linux パーティションも、自動的に Read-Only NBD ブリッジ経由でマウントできます（`.img` 生イメージ以外の物理 Linux パーティションおよび `.vhdx` に対応）。
   - 同一物理ディスク上の別パーティションも、別のドライブレターを選んで同様に同時マウントできます（下部「現在マウント中の Linux ドライブ」一覧に各ドライブの `RO` / `RW` モードが表示されます）。
4. **マウント中の操作・アンマウント**:
   - 中央の **「⏏ 選択行をアンマウント」** ボタン、または画面上部バナーの **「すべてアンマウント」** ボタンからいつでも安全に取り外せます（下部の **「② 現在マウント中の Linux ドライブ」** 一覧では **`📂 開く`** ボタンからエクスプローラーで開けます）。
   - 同じ物理ディスク上に他のマウント中パーティションが残っている状態で個別解除を押した場合は、「選択パーティションのみ個別アンマウントする」か「同一ディスクの全パーティションを一括アンマウントして切り離す」かを確認するダイアログが表示されます。
5. **終了時の保護**:
   - ウィンドウ右上の `×` ボタンでアプリを閉じる際、マウント中のドライブが残っている場合は「終了前にすべてアンマウントするか / マウントを維持したまま終了するか」の確認ダイアログが表示されます。
