# トラブルシューティング (`WslDrive`)

本ドキュメントでは、利用時に遭遇しやすいパーミッション制限、Windows 同居ディスク制約、および WSL2 状態不整合の対処法をまとめます。基本的な操作方法は [usage.md](usage.md) を参照してください。

## 1. パーミッション制限（`/root` や他人の UID が「アクセス拒否」になる）

### 症状
エクスプローラーでマウントしたドライブを開いた際、`/root` や Linux 側で `700` / `600`（`root` 所有等）に設定されたファイル・ディレクトリにアクセスしようとすると「アクセスが拒否されました」となる。

### 原因
`\\wsl.localhost\<Distro>\mnt\wsl\...` 経由のアクセスは、WSL ディストリビューションの既定ユーザー（通常 `uid=1000`）の権限で評価されるため、異なる UID や `root` のみに制限されたファイルは一般ユーザー権限から参照できません。

### 対処法
1. **一時的に確認したい場合**: WSL ターミナル側で `sudo ls /mnt/wsl/<マウント名>` として参照する、または Windows 側で見えるディレクトリのみ閲覧する。
2. **別ディストロ（または専用軽量ディストロ）を `root` デフォルトで用意する方法**:
   閲覧・操作用の小さな WSL ディストロ（Alpine など）を用意し、そのディストロ内の `/etc/wsl.conf` に以下を設定して `--distro Alpine`（GUI では右上の「使用 WSL」プルダウン）でマウントすると、エクスプローラー経由でもすべてのファイルを権限エラーなく操作できるようになります：
   ```ini
   [user]
   default=root
   ```
   （※既定の Read-Only マウントであれば、`root` 権限であってもディスクへの書き込みは一切発生しません）

## 2. Windows 同居ディスクで `--rw` / RW マウントが拒否される

### 症状
`C:` などの Windows ボリュームが存在する同一物理ディスク上の Linux パーティションに対し、CLI の `--rw` または GUI の「読み書き可能 (RW) でマウント」を実行するとエラーになる。

### 原因と仕様
Windows が使用中の物理ディスクは `wsl --mount` が `ERROR_SHARING_VIOLATION` でアタッチを拒否するため、`WslDrive` は安全性の高い **Read-Only 専用 NBD ブリッジ**（`AlignedDiskStream` ＋ `/dev/nbdX`）を使用します。Windows 側ファイルシステムとの競合・破損を防ぐため、NBD ブリッジ経路では RW マウントを禁止しています（[ADR-0005](../adr/0005-read-only-nbd-bridge-for-windows-shared-disks.md)）。

### 対処法
- 読み取り専用のままチェックを外して（`--rw` なしで）マウントするか、RW 操作が必要な場合は Windows 未使用の別物理ディスクまたは `.vhdx` ファイルを使用してください。

## 3. `WSL_E_DISK_ALREADY_MOUNTED` 等の残留エラーが解消しない

### 症状
過去に手動で `wsl` 内から `umount` を実行した等の理由で、`wslservice.exe` 内部に古いアタッチ状態が残留し、再マウントに失敗する。

### 対処法
1. `WslDrive` は `/proc/mounts` 再利用や `wsl --unmount` リトライ、`/mnt/wsl/wsldrive_*` 自己バインド補完を自動試行します（[ADR-0004](../adr/0004-wsl-unmount-ordering-and-multi-partition-management.md)）。
2. それでも WSL2 VM 側の状態不整合が解消しない場合は、すべての WSL2 作業を保存した上で管理者ターミナルから一度リセットしてください：
   ```powershell
   wsl --shutdown
   ```
