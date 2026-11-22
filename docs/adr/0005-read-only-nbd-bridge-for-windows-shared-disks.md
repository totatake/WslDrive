# ADR-0005: Windows 同居ディスク向け Read-Only NBD ブリッジ

- **ステータス**: Accepted
- **日付**: 2026-11-17
- **関連文書**: [システムアーキテクチャ](../architecture.md), [要件定義書](../requirements.md)

## 1. 背景と課題 (Context)

`wsl --mount \\.\PhysicalDrive0` は Hyper-V 仮想 SCSI 経由で物理ディスク全体を WSL2 VM へパススルーするため、同じ物理ドライブ上に Windows がマウント中のボリューム（`C:` 等）が存在すると `ERROR_SHARING_VIOLATION` (`0x80070020`) でアタッチが拒否されます。一方、Windows カーネルは管理者権限プロセスによる `CreateFileW(..., GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE)` でのセクタ境界読み取りは許可しています。

## 2. 検討した選択肢 (Alternatives Considered)

### 案 A: Windows 同居ディスクのマウントを非対応とする
- **メリット**: 実装が不要。
- **デメリット / リスク**: 1 台の NVMe SSD に Windows と Linux をデュアルブートしている環境で全く利用できない。

### 案 B: `AlignedDiskStream` ＋ WSL2 カーネル標準 `nbd` モジュールによる Read-Only NBD ブリッジ
- **メリット**: WSL2 標準カーネルに搭載されている `CONFIG_BLK_DEV_NBD=m`（`modprobe nbd`）とディストロ標準の `python3`（`ctypes` + `socket.socketpair` + `NBD_SET_SOCK` / `NBD_SET_FLAGS(NBD_FLAG_READ_ONLY)` / `NBD_DO_IT` ioctl）を利用するため外部パッケージ（`apt` 等）が不要。Windows 側を `GENERIC_READ` 専用かつ対象パーティション範囲に限定し、NBD 層で `NBD_CMD_WRITE` 等を `EROFS (30)` で拒否、Linux 側でも `-o ro,noload` / `ro` でマウントする 3 重保護を実現できる。
- **デメリット / リスク**: CLI では呼び出し終了後もサーブを継続するバックグラウンド常駐プロセス（`__nbd-daemon`）が必要。また、Windows がオンライン使用中のディスクに対する安全確保のため Read-Only 専用に限定する必要がある。

## 3. 決定 (Decision)

**案 B（ハイブリッド自動切替・Read-Only 専用 NBD ブリッジ）** を採用する。Windows 未使用の別ドライブや `.vhdx` はネイティブ `wsl --mount`（RO/RW 対応）を使いつつ、Windows 同居ディスクでは自動で Read-Only NBD ブリッジへ切り替え、RW 要求は明示的に拒否する。

## 4. 結果と影響 (Consequences)

- **良い影響**: デュアルブート機の `Disk 0` 等にある Linux パーティションも追加ドライバや `apt` なしで安全に読み取れるようになった。
- **受け入れるトレードオフ / 今後の制約**: 対象 WSL ディストリビューションに `python3` が存在すること、およびカーネルに `nbd` モジュールが含まれることが前提となる。また NBD ブリッジ経路では安全性のため RW マウント不可。さらに、WSL 側で `mount` 等を実行する際はコマンドインジェクション防止のため `sh -c` 文字列組み立てを禁止し、`FileSystemType` の厳格な検証と直接引数配列での実行を必須とする。
- **関連する変更箇所**: [AlignedDiskStream.cs](../../src/WslMountDrive.Core/Disk/AlignedDiskStream.cs), [NbdDiskBridge.cs](../../src/WslMountDrive.Core/Wsl/NbdDiskBridge.cs), [WslMountService.cs](../../src/WslMountDrive.Core/Wsl/WslMountService.cs), [Program.cs](../../src/WslMountDrive.Cli/Program.cs)
