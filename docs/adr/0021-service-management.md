# ADR-021: サービス管理（systemd）

## ステータス
決定（基本設計書 7章 `IServiceManagerProvider`）

## 決定
- capability `service-manager`（テンプレート`systemctl`）。状態は`systemctl show`、ログは`journalctl -u`で取得する。
- **操作（起動・停止・再起動・自動起動の有効/無効）できるのは管理対象サービスのみ**（`Services:Managed`。未設定時は chronyd / chrony / systemd-timesyncd / keepalived / mariadb / mysqld / podman / podman.socket / docker / NetworkManager / systemd-networkd / nginx / sshd / ssh）。任意のサービスを操作できると影響範囲が大きいため。
- SystemAgent自身と sshd / ssh の**停止・無効化は拒否**する（リモートから復旧できなくなるため。再起動は可）。
- 一覧には管理対象のうちこのノードに存在するものだけを表示し、別名（mysqld → mariadb 等）は重複させない。
- 操作は監査ログに残す。他ノードのサービスは転送（ADR-018）で操作する。
