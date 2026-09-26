# SystemAgent 基本設計書（ドラフト v0.1）

> 本書は要件ヒアリングに基づく設計ドラフトです。未決事項は「TBD」として明記しています。実装フェーズではAIエージェント(Claude Code等)への指示の土台として利用することを想定しています。

---

## 1. 概要

### 1.1 目的
コンテナ(Podman/Docker)、ホストネットワーク、NTP、Keepalived、コンテナレジストリ等、RHEL系（およびDebian/Ubuntu系）サーバーの運用管理を統合的に行うツール。自ノードだけでなく、登録済みの他ノードに対しても同一のUI/CLIから操作可能とする。自社の各種ソフトウェア（コンテナベース）のイメージ更新・デプロイ機能を含む。

### 1.2 背景
社内PoCツールが製品化される見込みとなったため、私的に開発する後継/代替のベースアプリケーションとして位置づける。

### 1.3 スコープ
- 対象: RHEL系、Debian/Ubuntu系（複数バージョン、EOL版含む）
- 対象外: macOS、Windows、Docker Compose対応、大規模一般公開利用

---

## 2. 要件サマリ（確定事項）

### 2.1 機能要件
| 項目 | 内容 |
|---|---|
| コンテナ管理 | Podman/Docker対応。Podmanの場合はPod(ネットワークグループ)を利用 |
| イメージ管理 | エアギャップ環境向けにWebUIからイメージをインポート可能 |
| ネットワーク設定 | ホストネットワーク変更 |
| NTP設定 | 時刻同期設定 |
| Keepalived設定・管理 | VIP管理、昇格スクリプト管理を含む |
| コンテナレジストリ管理 | プライベートレジストリの管理 |
| マルチノード操作 | 任意の1台から他ノードを操作可能 |
| デプロイ機能 | 自社ソフトウェア(コンテナ)のイメージ更新・デプロイ |
| 提供形態 | WebUI(全操作可能) + CLI(WebUIと同一操作が可能) |
| 配布形態 | RPM/DEB (nfpmで生成) |

### 2.2 非機能要件
| 項目 | 内容 |
|---|---|
| 可用性 | 24365想定。中央DB構成だがKeepalived+VIPで冗長化可能な設計とする |
| セキュリティ | 既存JWT基盤を踏襲しつつ、DB非依存のローカル緊急認証を別建て |
| スケーラビリティ | 想定不要（同時利用2〜3人） |
| 保守性 | チーム開発。将来の機能追加をAIエージェントが行うことを前提とした拡張性を確保 |
| 永続化 | 管理情報はMariaDB中心。ノードローカル保持も許可。バックアップ機能必須 |
| ネットワーク帯域 | バックアップ時の帯域制御が必要（業務影響回避） |
| オフライン対応 | エアギャップ環境が前提（必須） |
| 多言語対応 | 現状不要だが将来対応できる構造が望ましい |

### 2.3 技術要件（確定）
| 項目 | 内容 |
|---|---|
| OS | RHEL系、Debian/Ubuntu系（複数世代・EOL版含む） |
| バックエンド | C# .NET 10 / ASP.NET Core (Kestrel) |
| フロントエンド | Blazor Server（Vue3・Angularは不採用。ビルドパイプライン分離コスト回避のため） |
| DB | MariaDB（中央DB方式、任意でレプリカ冗長化） |
| 冗長化 | Keepalived + VIP（Galera等のクラスタ構成は不採用。運用スキル要件が高いため） |
| TLS終端 | Nginx（任意コンポーネント。無くてもHTTPで動作可、その場合は自己責任） |
| CI/CD | Jenkins |
| パッケージング | nfpm（Go製、RPM/DEB両対応） |
| 認証 | JWTベース（既存基盤 + DB非依存のローカル緊急認証を別建て） |

---

## 3. 全体アーキテクチャ

### 3.1 マルチノード構成図

```mermaid
graph TB
    subgraph "拠点ネットワーク（エアギャップ）"
        subgraph "Node A（代表ノード / VIP保持）"
            A_UI["Blazor Server WebUI"]
            A_API["ASP.NET Core WebAPI"]
            A_CLI["CLI"]
            A_DB[("MariaDB<br/>(Master)")]
            A_CA["自己CA"]
            A_KA["Keepalived"]
            A_UI --- A_API
            A_CLI --- A_API
            A_API --- A_DB
            A_KA -.VIP保持中.-> A_DB
        end

        subgraph "Node B（レプリカノード）"
            B_API["ASP.NET Core WebAPI"]
            B_DB[("MariaDB<br/>(Replica)")]
            B_KA["Keepalived"]
            B_API --- B_DB
        end

        subgraph "Node C（管理対象ノード）"
            C_API["ASP.NET Core WebAPI"]
            C_DB[("MariaDB<br/>(Replica)")]
        end

        VIP{{"VIP<br/>(代表アドレス)"}}
        A_KA === VIP
        B_KA -.待機.-> VIP

        A_API -- "mTLS" --> B_API
        A_API -- "mTLS" --> C_API

        Nginx["Nginx<br/>(任意/TLS終端)"]
        Nginx -.任意配置.-> A_API
    end

    User(["利用者<br/>(任意の1台から操作)"]) --> VIP
```

**ポイント**
- 利用者はVIP経由でどのノードが代表(Master)でも同じ入口からアクセスする。
- 各ノードのSystemAgentは自身のAPIを持ち、他ノードへはmTLSで通信する（分散エージェント方式）。
- MariaDBは中央DB方式。冗長化を希望するノードはレプリカを構築し、Keepalived+VIPで切替。

### 3.2 レイヤー構成図

```mermaid
graph TD
    subgraph "Presentation Layer"
        UI["Blazor Server<br/>WebUI"]
        CLI["CLI<br/>(System.CommandLine等)"]
    end

    subgraph "API Layer"
        API["ASP.NET Core WebAPI<br/>(OpenAPI定義が正)"]
    end

    subgraph "Application Layer"
        SVC_Container["Container管理サービス"]
        SVC_Network["Network管理サービス"]
        SVC_Ntp["NTP管理サービス"]
        SVC_Keepalived["Keepalived管理サービス"]
        SVC_Registry["レジストリ管理サービス"]
        SVC_Deploy["デプロイ管理サービス"]
        SVC_Node["ノード管理・通信サービス"]
        SVC_Secret["秘密情報管理サービス"]
        SVC_Auth["認証サービス<br/>(JWT本流 + ローカル緊急)"]
    end

    subgraph "Capability Provider Layer（OS差分吸収）"
        CP_IF["IContainerRuntimeProvider<br/>INetworkProvider<br/>INtpProvider<br/>IServiceManagerProvider<br/>IPackageManagerProvider"]
        CP_Template["OS/バージョン別<br/>コマンドテンプレート(JSON/YAML)"]
        CP_Detect["環境検出・自己診断ツール"]
        CP_IF --> CP_Template
        CP_Detect -.生成補助.-> CP_Template
    end

    subgraph "Infrastructure Layer"
        DB[("MariaDB")]
        FS["ローカル暗号化ストレージ<br/>(秘密情報)"]
        OS["OSコマンド / P-Invoke / netlink"]
    end

    UI --> API
    CLI --> API
    API --> SVC_Container & SVC_Network & SVC_Ntp & SVC_Keepalived & SVC_Registry & SVC_Deploy & SVC_Node & SVC_Secret & SVC_Auth

    SVC_Container & SVC_Network & SVC_Ntp & SVC_Keepalived --> CP_IF
    CP_IF --> OS

    SVC_Node --> DB
    SVC_Secret --> FS
    SVC_Auth --> FS
    SVC_Auth --> DB
```

**設計原則**
- WebUI・CLIは**同一のWebAPIを叩くクライアント**であり、業務ロジックを持たない。機能同一性はこの構造で自動的に担保される。
- OS差分は Capability Provider 層に隔離し、上位のApplication Layerからは意識させない。

---

## 4. マルチノード通信・ノード登録

### 4.1 ノード登録シーケンス

```mermaid
sequenceDiagram
    actor 管理者
    participant WebUI as WebUI(代表ノード)
    participant CA as 自己CA(代表ノード)
    participant NewNode as 新規ノード

    管理者->>WebUI: ノード登録開始（IPまたはホスト名を入力）
    WebUI->>NewNode: 初期化トークンを提示（画面 or CLI経由で新規ノードに投入）
    NewNode->>CA: CSR(証明書署名要求)を送信
    CA->>CA: トークン検証
    CA-->>NewNode: 署名済み証明書を発行
    NewNode-->>WebUI: 登録完了通知(mTLSハンドシェイク確立)
    WebUI->>WebUI: DBにノード情報を登録
    Note over WebUI,NewNode: 以降、mTLSで相互通信
```

- CA管理機能（発行・失効・更新）をWebUIの正式機能として実装する。
- ノード一覧は中央DBで一元管理し、全ノードから同一のノードリストが参照される。

### 4.2 Keepalived + フェイルオーバー（昇格）シーケンス

既存のBashによる昇格スクリプトの仕組みを、SystemAgent自身に組み込む方針。Keepalivedの`notify_master`フックがSystemAgentの昇格APIを呼び出す形にすることで、秘密情報(DB接続情報等)をBashスクリプト内に書かずに済み、権限もSystemAgentの実行ユーザーに閉じ込められる。

```mermaid
sequenceDiagram
    participant KA as Keepalived(Node B)
    participant Agent as SystemAgent(Node B)
    participant DB_B as MariaDB(Node B / 元Replica)
    participant DB_A as MariaDB(Node A / 元Master)

    Note over KA: Node A 監視断を検知、VIP取得
    KA->>Agent: notify_masterフック実行<br/>(SystemAgentの昇格APIをローカル呼び出し)
    Agent->>DB_B: STOP SLAVE
    Agent->>DB_B: 読み取り専用解除
    Agent->>Agent: 自ノードをMasterとして状態更新
    Note over Agent: 以降このノードが実質的な代表として応答

    alt 自動復帰モード
        Note over DB_A: Node A復帰
        Agent->>DB_A: 強制的にReplicaとして再参加させる
    else 手動復帰モード
        Note over DB_A: Node A復帰
        Agent->>Agent: 「復帰待ち」状態で待機し、WebUI上で管理者の承認を要求
        actor 管理者
        管理者->>Agent: 復帰承認（ポチッと操作）
        Agent->>DB_A: Replicaとして再参加させる
    end
```

- **自動復帰モード**／**手動復帰モード**を運用ポリシーとして選択可能にする（人間の操作は承認ボタン一つに集約）。
- Split Brain対策として、旧MasterがVIPを失った時点で自動的に読み取り専用へ強制する処理も併せて組み込む。

---

## 5. 秘密情報の保護

### 5.1 方針
- Git上ではテンプレート（RAW）状態のまま管理し、鍵は含めない。
- RPMインストール時（`%post`スクリプト）に、その場でホスト固有の鍵を生成し、初期秘密情報を暗号化して配置する。
- バイナリへの秘密鍵埋め込みは行わない。

### 5.2 インストール〜初回起動フロー

```mermaid
flowchart TD
    A["RPM/DEB インストール実行"] --> B["%postスクリプト起動"]
    B --> C["ホスト固有の暗号鍵を生成"]
    C --> D["鍵をローカルファイルに配置<br/>(パーミッション600 / 専用実行ユーザー所有)"]
    D --> E["初期秘密情報(テンプレート)を鍵で暗号化"]
    E --> F["暗号化済み秘密情報ファイルを配置"]
    F --> G["SystemAgentサービス起動"]
    G --> H{"MariaDB起動済み？"}
    H -- No --> I["ローカル緊急認証のみ有効化<br/>(DB非依存)"]
    H -- Yes --> J["通常のJWT認証(DB管理ユーザー)有効化"]
    I --> K["管理者がWebUIから復旧作業"]
    K --> J
```

### 5.3 TBD（要検討）
- TPM2搭載環境での鍵シール化の可否（ハードウェア依存のためフォールバック設計が必要）
- Linuxカーネルキーリング(`keyctl`)による実行時メモリ保持の要否

---

## 6. 認証・認可設計

| 認証経路 | 用途 | 保存先 |
|---|---|---|
| 通常JWT認証 | 既存システムと同一基盤。DB管理ユーザーによるログイン | MariaDB |
| ローカル緊急認証 | MariaDB起動前・停止中のログイン専用。DBとは独立したJWT発行基盤 | 暗号化ローカルファイル（5章と同一基盤） |

- 外部IdP等は使用しない。
- 秘密情報(appsettings、DB接続情報)の変更機能は、上記いずれの認証でも操作可能とし、操作ログを残す（監査要件は今後の検討事項）。

---

## 7. コマンド抽象化（Capability Provider パターン）

### 7.1 構成イメージ

```mermaid
classDiagram
    class IContainerRuntimeProvider {
        <<interface>>
        +ListContainers()
        +CreatePod()
        +PullImage()
        +DeployImage()
    }
    class INetworkProvider {
        <<interface>>
        +GetInterfaces()
        +SetIpAddress()
    }
    class INtpProvider {
        <<interface>>
        +GetSyncStatus()
        +SetNtpServer()
    }
    class IServiceManagerProvider {
        <<interface>>
        +StartService()
        +StopService()
    }

    class ProviderFactory {
        +Resolve(osInfo) IContainerRuntimeProvider
    }

    class CommandTemplateStore {
        +GetTemplate(os, version, capability)
    }

    class EnvironmentDetector {
        +DetectOsRelease()
        +DetectCommandAvailability()
        +GenerateTemplateSkeleton()
    }

    IContainerRuntimeProvider <|.. PodmanProvider_RHEL9
    IContainerRuntimeProvider <|.. DockerProvider_Ubuntu22
    ProviderFactory --> IContainerRuntimeProvider
    ProviderFactory --> CommandTemplateStore
    EnvironmentDetector --> CommandTemplateStore
```

### 7.2 方針
- ドメイン単位のインターフェース(C#)は固定。実装差分はOS/バージョンごとのテンプレート(JSON/YAML)として外部化し、人間（または将来的にAIエージェント）が埋める運用とする。
- 可能な範囲は.NETネイティブ/P-Invoke/netlink等で直接処理し、OSコマンド実行を避ける。
- 構造化出力(`--json`等)に対応するコマンドを優先し、非構造化出力のパースはテンプレート単位に隔離する。
- 環境検出・自己診断ツールをSystemAgentの初期機能として実装し、テンプレート雛形の生成を補助する。

---

## 8. デプロイ・CI/CD

```mermaid
flowchart LR
    Dev["開発(Claude Code等)"] --> Git["Gitリポジトリ"]
    Git --> Jenkins["Jenkins"]
    Jenkins --> Build[".NETビルド"]
    Build --> Nfpm["nfpmによる<br/>RPM/DEB生成"]
    Nfpm --> Artifact["成果物<br/>(RPM/DEB)"]
    Artifact --> AirGap["エアギャップ環境へ<br/>物理/オフライン搬入"]
    AirGap --> Install["各ノードにインストール"]
```

---

## 9. 拡張性・AIエージェント協働のための設計ルール

将来的にClaude Code等のAIエージェントが機能追加を行うことを前提に、以下をルール化する。

1. **拡張ポイントの明示**：新しい管理対象（ファイアウォール、ログローテーション等）は、Capability Providerのインターフェース実装追加のみで対応できる構造を維持する。
2. **ADR(Architecture Decision Record)方式の採用**：本書の意思決定は`docs/adr/`配下に決定記録として残し、後続の機能追加時に背景を参照可能にする（10章に初期ADR一覧を記載）。
3. **OpenAPI定義を機能一覧の正とする**：WebUI/CLIが叩くASP.NET Core WebAPIのOpenAPI定義を正とし、機能追加時は「API定義拡張 → 実装」の順序をルールとする。
4. **コマンドテンプレートのスキーマ化**：7章のテンプレートスキーマを固定し、新規OS/バージョン対応をテンプレート追加のみで完結しやすくする。

---

## 10. ADR一覧（初期）

| ID | タイトル | 決定内容 | 理由 |
|---|---|---|---|
| ADR-001 | フロントエンド技術選定 | Blazor Server採用、Vue3/Angular不採用 | ASP.NETとの親和性、単一ビルドパイプラインでのRPM配布のしやすさ |
| ADR-002 | ノード間永続化方式 | 中央DB方式（Galera等のクラスタ方式は不採用） | 利用者のDB運用スキル・DNS運用制約を考慮し、Keepalived+VIPによる古典的冗長化を選定 |
| ADR-003 | 秘密情報の保護方式 | インストール時にホスト固有鍵を生成しローカル暗号化。バイナリへの鍵埋め込みは行わない | エアギャップ環境でクラウドKMS等が使用できないため |
| ADR-004 | TLS終端の扱い | Nginxは任意コンポーネント。無い場合のHTTP平文アクセスを許可 | エアギャップ・小規模運用における導入コストを優先。リスクは利用者が許容 |
| ADR-005 | コマンド抽象化方式 | Capability Providerパターン + データ駆動(テンプレート外部化) | 多OS・多バージョン・EOL対応をコード変更なしで拡張可能にするため |
| ADR-006 | パッケージング方式 | nfpm(Go製)を用いてRPM/DEB両対応 | 単一ツールで両形式に対応できるため |

---

## 11. 未決事項（TBD）一覧

| # | 項目 | 内容 |
|---|---|---|
| 1 | TPM2活用可否 | 秘密鍵のハードウェアシール化。対応環境と非対応環境のフォールバック設計 |
| 2 | 監査ログ要件 | 秘密情報変更操作等の監査ログ保存・閲覧機能の要否 |
| 3 | バックアップの帯域制御方式 | 具体的な制御方式（QoS/アプリ側スロットリング等）の選定 |
| 4 | 多言語対応の実装範囲 | Blazor Serverでのリソースファイル設計方針 |
| 5 | 証明書失効時の運用 | CA失効リストの配布・反映タイミング（エアギャップのため即時反映不可） |

---

## 12. 次のアクション

- [ ] 上記TBD項目の方針決定
- [ ] APIエンドポイント一覧（OpenAPI草案）の作成
- [ ] コマンドテンプレートのスキーマ定義
- [ ] DBスキーマ設計（ノード管理・秘密情報メタデータ・ユーザー管理）
