# ADR-0004: `wsl --unmount` 実行順序とマルチパーティション管理

- **ステータス**: Accepted
- **日付**: 2026-11-17
- **関連文書**: [システムアーキテクチャ](../architecture.md)

## 1. 背景と課題 (Context)

Windows の WSL2 サービス（`wslservice.exe` / `WslCoreVm`）は `m_attachedDisks[disk].Mounts[partitionIndex]` でパーティション単位のアタッチ状態を管理していますが、以下の挙動によりエッジケースが発生しました。

1. **`umount` 先行によるデッドロック**: `wsl --unmount <target>` は内部テーブルからディスクを削除する前に `mini_init` へ `umount()` を指示するため、ホスト側が先に `wsl ... -- umount /mnt/wsl/<name>` を実行すると後続の `wsl --unmount` が `EINVAL` で失敗し、再マウント時に `WSL_E_DISK_ALREADY_MOUNTED` が残留します。
2. **マルチパーティション時の巻き込みと孤立**: 同一ディスク上の Partition 1 と Partition 2 を同時マウントしているときに `wsl --unmount \\.\PhysicalDriveN` を呼ぶと両方強制切断され、逆に片方だけ WSL 内で `umount` した後に再マウントすると `WSL_E_DISK_ALREADY_MOUNTED` になり、さらに最後の1件を `wsl --unmount` で外す際も先に外れた方のマウントポイントが `EINVAL` を返してデタッチに失敗します。

## 2. 検討した選択肢 (Alternatives Considered)

### 案 A: 1 ディスク 1 パーティションに制限し、毎回 `wsl --shutdown` でリセットする
- **メリット**: 実装が簡単。
- **デメリット / リスク**: ユーザーの他 WSL2 作業（Docker やターミナル）まで全終了させてしまい実用的でない。

### 案 B: ステートレス復元 ＋ 個別/一括分岐 ＋ 自己バインド補完
- **メリット**:
  1. 毎回 `/proc/mounts` と `DriveMapper.GetMappedNetworkDrives()` を突き合わせてセッション復元し、CLI 再実行や GUI 再起動でも整合を保つ。
  2. 同一ディスクに他パーティションが残存する場合は WSL 内の個別 `umount` のみに留め、最後の 1 件または全体アンマウント時は `umount` を先行させず直接 `wsl --unmount` を呼ぶ。
  3. 再マウント時に `WSL_E_DISK_ALREADY_MOUNTED` かつ兄弟パーティションが存在する場合は、兄弟デバイス名（`/dev/sdc` や `/dev/nvme0n1`）から対象パーティションを直接マウントする（`TryMountSiblingPartitionInWslAsync`）。
  4. 最終デタッチで `wsl --unmount` が失敗した際は、空ディレクトリとして残る `/mnt/wsl/wslmountdrive_*` に無害な自己バインドマウント（`mount --bind "$d" "$d"`）を補ってからリトライし、`mini_init` の `umount() + rmdir()` を完了させる（`EnsureStaleMountPointsBoundForDetachAsync`）。
- **デメリット / リスク**: 状態遷移テストの網羅が必要。

## 3. 決定 (Decision)

**案 B** を採用する。`wsl --shutdown` に頼らずに同一ディスクの複数パーティション同時マウントと安全な個別・一括アンマウントを両立できるため。

## 4. 結果と影響 (Consequences)

- **良い影響**: CLI / GUI 双方からマルチパーティションを安全に同時利用・個別解除でき、GUI では意図しない兄弟切断を防ぐ確認ダイアログも表示できる。
- **受け入れるトレードオフ / 今後の制約**: 自動生成マウント名（`wslmountdrive_d<N>p<M>`）および `/proc/mounts` の書式に依存する。
- **関連する変更箇所**: [WslMountService.cs](../../src/WslMountDrive.Core/Wsl/WslMountService.cs), [MainWindow.xaml.cs](../../src/WslMountDrive.App/MainWindow.xaml.cs), [WslMountServiceTests.cs](../../tests/WslMountDrive.Tests/WslMountServiceTests.cs)
