# ADR-022: 冗長化（Keepalived + DB昇格・降格）

## ステータス
決定（基本設計書 7章 `IKeepalivedProvider` / HA。QA-0004: 検証はモック・WSLで行う）

## 決定
- **VIPを保持したノードのDBがマスター**。keepalived は SystemAgent が生成した設定で動かし、状態変化は notify で SystemAgent に通知する。
  - 設定は `state BACKUP` + `nopreempt`（元マスターが戻ってきてもVIPを奪い返さない。フェイルバックは運用者が判断する）。
  - notify は `/usr/bin/systemagent ha notify <STATE>`。API `POST /api/ha/notify` は**ループバックからのみ**、かつ `/var/lib/systemagent/ha-hook-token`（0600、起動時に生成）のトークンを `X-SystemAgent-Hook-Token` ヘッダで提示した場合だけ受け付ける。
- 状態ごとの動作
  - **MASTER**: 保留中の再参加判断を取り消し、DBを昇格（`STOP SLAVE; RESET SLAVE ALL; read_only=OFF`）。
  - **BACKUP / FAULT / STOP**: 直ちに `read_only=ON`（書き込みを止める）。BACKUPの場合は `HA:RejoinDelaySeconds`（既定15秒）待ってから再参加を判断する。keepalived は起動時に必ずBACKUPを経由するため、すぐMASTERになる場合に誤って再参加しないようにする。
  - 再参加: **自動**モードなら新マスター（既定はVIP、`ReplicationSourceHost`で変更可）のレプリカになる。**手動**モード（既定）は承認待ちとし、WebUI / `systemagent ha rejoin` で承認する。
- 再参加は `CHANGE MASTER TO ... MASTER_USE_GTID=current_pos`。`slave_pos` だと元マスター自身が書いたGTIDが反映済みとみなされず、新マスターのバイナリログを先頭から再適用してデータが壊れたことを検証で確認したため。
- 安全装置
  - 再参加の前に、接続先が `read_only=0`（本当にマスター）であることを確認する。レプリカ同士の循環レプリケーションを防ぐため。
  - 前提条件（全ノードの MariaDB で `log_bin`・`log_slave_updates`・`gtid_strict_mode` が有効、`server_id` が一意）を満たさない場合は自動再参加せず承認待ちにし、画面・CLIに理由を表示する。
  - マスター中の再参加要求は 409 で拒否する。
- keepalived 設定の適用は `keepalived -t -f` で検証 → 元ファイルを `.systemagent.orig` に退避 → 0600 で書き込み → 再起動。再起動に失敗したら元に戻す。無効化時は keepalived を停止する。
- HA設定（VRRP認証パスワード、DB管理者・レプリケーション用の資格情報を含む）はローカルの暗号化シークレット（`ha.settings`）に保存し、APIは値を返さない（有無だけ返す）。
- このノードのDB操作の失敗は 422（中央DBの停止 = 503 とは区別する）。状態の競合は 409。

## 検証
- 単体テスト: 設定生成・検証（`KeepalivedTests`）。
- WSL（sa-alma9 に MariaDB 3306 と 3307、dummy IF `sa-ha0`、VIP 192.0.2.100、ユニキャスト）で、起動・フェイルオーバー・手動再参加・フェイルバック（自動再参加）を通し、全テーブルのチェックサム一致とデータ欠落なしを確認した。

## 範囲外
- MariaDB 自体の初期構築（server_id・バイナリログ設定、レプリケーション用ユーザー作成、初回のデータコピー）は運用者が行う。
- スプリットブレイン時の自動調停は行わない（`nopreempt` と手動復帰を既定にして人の判断を挟む）。
