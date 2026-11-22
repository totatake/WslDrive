# ADR-0002: `DefineDosDeviceW` と非昇格トークン偽装によるドライブ割当

- **ステータス**: Accepted
- **日付**: 2026-11-14
- **関連文書**: [システムアーキテクチャ](../architecture.md)

## 1. 背景と課題 (Context)

WSL2 でマウントしたパーティションは UNC パス `\\wsl.localhost\<Distro>\mnt\wsl\<MountName>` として公開されますが、通常のネットワークドライブ割り当てには 2 つの Windows 固有障壁がありました。

1. **Plan 9 Network Provider (`p9np.dll`) による拒否（エラー 64 / 67）**: `WNetAddConnection2W` や `net use L: \\wsl.localhost\Ubuntu\mnt\wsl\wsldrive_d2p2` を実行すると、`p9np.dll!NPAddConnection3` が UNC パス要素数 `== 2`（ルート共有のみ）を厳密検査するため、3 要素以上のサブディレクトリに対して `WN_BAD_NETNAME` (67) を返し、後続の SMB フォールバックでエラー 64 (`ERROR_NETNAME_DELETED`) で失敗します。また `subst.exe` は UNC パスを受け付けません。
2. **UAC セッション分離**: 物理ディスク読み取りや `wsl --mount` には管理者権限が必須ですが、昇格プロセスから `DefineDosDeviceW` を呼ぶと管理者用ログオンセッション（`\Sessions\0\DosDevices\<ElevatedLogonId>\`）にのみリンクが作られ、通常ユーザー権限の `explorer.exe` からはドライブが見えません。

## 2. 検討した選択肢 (Alternatives Considered)

### 案 A: レジストリ `EnableLinkedConnections = 1` の変更を要求する
- **メリット**: 実装が単純。
- **デメリット / リスク**: ユーザーのシステム全体設定を変更し、反映に OS 再起動が必要。

### 案 B: `DefineDosDeviceW` + `explorer.exe` 非昇格トークン偽装 (`ImpersonateLoggedOnUser`)
- **メリット**: `DefineDosDeviceW` は `RtlDosPathNameToNtPathName_U` 経由で `\??\UNC\wsl.localhost\...` へ直接シンボリックリンクを作成するため `p9np.dll` の 2 階層制限を回避できる。さらに `GetShellWindow()` から取得した `explorer.exe` の非昇格トークンを複製・偽装して呼び出すことで、CSRSS（`BaseSrvDefineDosDevice`）が通常ユーザーの `DosDevices` 名前空間にドライブを作成し、その後に管理者セッション側にも登録すれば双方から即時参照可能となる。
- **デメリット / リスク**: P/Invoke 実装が少し増える。

## 3. 決定 (Decision)

**案 B（`DefineDosDeviceW` + 非昇格トークン偽装）** を採用する。レジストリ変更や再起動なしに、インプロセスで確実かつ即座に通常ユーザーのエクスプローラーと管理者ターミナル双方へドライブを公開できるため。

## 4. 結果と影響 (Consequences)

- **良い影響**: インストール直後からエクスプローラーと管理者ターミナルの双方で `L:\` が即時に認識される（最後に `SHChangeNotify(SHCNE_DRIVEADD)` と `WM_SETTINGCHANGE` を通知）。
- **受け入れるトレードオフ / 今後の制約**: 状態照会・アンマウント時は `QueryDosDeviceW` が返す `\??\UNC\...` や `\Device\P9Rdr\;...` 形式を標準 UNC パスへ逆変換する必要がある（`DriveMapper.TryParseDosDeviceUncTarget`）。
- **関連する変更箇所**: [DriveMapper.cs](../../src/WslDrive.Core/Interop/DriveMapper.cs), [NativeMethods.cs](../../src/WslDrive.Core/Interop/NativeMethods.cs)
