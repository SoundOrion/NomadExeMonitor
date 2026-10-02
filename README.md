# NomadExeMonitor

Nomad で管理されている Windows の `raw_exec` タスクを取得し、実行コマンドに設定された EXE と Nomad 上の稼働状態をコンソールへ一覧表示する .NET アプリケーションです。

主に次の情報を確認できます。

- EXE 名
- Job ID
- 実行先の Nomad クライアント（NodeName）
- 稼働状態
- 再起動回数
- 起動日時
- Namespace、Task、直近イベントの詳細（`--wide` 使用時）

> [!IMPORTANT]
> このアプリは Windows のプロセス一覧を直接調査するものではありません。Nomad の Allocation と Job 定義を参照し、`raw_exec` タスクの `config.command` に設定された実行ファイルを表示します。

## 動作要件

- .NET 8 SDK
- C# 12
- Nomad HTTP API へ接続できる環境
- Allocation と Job Version を読み取れる Nomad ACL 権限
- 対象 Job のタスクドライバーが `raw_exec` であること

Windows 上での利用を想定していますが、アプリ自体は Nomad HTTP API を参照するため、API に到達できる別の端末からも実行できます。

## プロジェクトの作成

任意のフォルダーに次の2ファイルを配置します。

```text
NomadExeMonitor/
├─ NomadExeMonitor.csproj
└─ Program.cs
```

添付の修正版コードを `Program.cs` として保存してください。

`NomadExeMonitor.csproj` の例：

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
</Project>
```

## ビルド

```powershell
dotnet restore
dotnet build -c Release
```

ビルド後の DLL を実行する場合：

```powershell
dotnet .\bin\Release\net8.0\NomadExeMonitor.dll --help
```

Windows x64 向けの単一 EXE を作る場合：

```powershell
dotnet publish -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true
```

生成先：

```text
bin\Release\net8.0\win-x64\publish\NomadExeMonitor.exe
```

## 基本的な使い方

ローカルの Nomad API（既定値 `http://127.0.0.1:4646`）へ接続する場合：

```powershell
.\NomadExeMonitor.exe
```

接続先と Namespace を指定する場合：

```powershell
.\NomadExeMonitor.exe `
  --addr "https://nomad.example.local:4646" `
  --namespace "production"
```

ACL が有効な環境では、トークンを環境変数で渡すことを推奨します。

```powershell
$env:NOMAD_ADDR = "https://nomad.example.local:4646"
$env:NOMAD_NAMESPACE = "production"
$env:NOMAD_TOKEN = "<Nomad ACL token>"

.\NomadExeMonitor.exe
```

トークンは `--token` でも指定できますが、コマンド履歴やプロセス情報へ残る可能性があるため、通常は `NOMAD_TOKEN` を使用してください。

## 表示例

```text
Nomad EXE Monitor  2026-10-03 07:30:00 +09:00
Nomad: https://nomad.example.local:4646   Namespace: production

STATUS    EXE                            JOB                        SERVER                   RESTART STARTED
--------------------------------------------------------------------------------------------------------------------------
Failed    BatchWorker.exe                batch-worker               nomad-client-02          3       2026-10-03 06:20:11
Running   TestServer.exe                 test-server                nomad-client-01          0       2026-10-03 07:15:42

Total: 2  Running: 1  Failed: 1  Stopped: 0  Pending: 0  Unknown: 0
```

問題のあるタスクだけを詳細表示する場合：

```powershell
.\NomadExeMonitor.exe --only-problems --wide
```

5秒ごとに更新する場合：

```powershell
.\NomadExeMonitor.exe --watch 5 --wide
```

CSV または JSON へ出力する場合：

```powershell
.\NomadExeMonitor.exe --format csv > result.csv
.\NomadExeMonitor.exe --format json > result.json
```

## コマンドラインオプション

| オプション | 説明 | 既定値 |
|---|---|---|
| `--addr URL` | Nomad HTTP API の URL | `NOMAD_ADDR` または `http://127.0.0.1:4646` |
| `--namespace NS` | 対象 Namespace。`*` は権限のある全 Namespace | `NOMAD_NAMESPACE` または `*` |
| `--token TOKEN` | Nomad ACL トークン | `NOMAD_TOKEN` |
| `--ca-cert FILE` | PEM 形式の CA 証明書 | `NOMAD_CACERT` |
| `--client-cert FILE` | PEM 形式のクライアント証明書 | `NOMAD_CLIENT_CERT` |
| `--client-key FILE` | PEM 形式のクライアント秘密鍵 | `NOMAD_CLIENT_KEY` |
| `--skip-tls-verify` | TLS サーバー証明書の検証を無効化 | `NOMAD_SKIP_VERIFY` または無効 |
| `--timeout SEC` | HTTP タイムアウト（秒） | `15` |
| `--parallel N` | API リクエストの最大並列数 | `8` |
| `--format table\|json\|csv` | 出力形式 | `table` |
| `--wide` | Namespace、Task、直近イベントなどを追加表示 | 無効 |
| `--watch SEC` | 指定秒ごとに継続更新 | 無効 |
| `--only-problems` | `Running` の行を非表示 | 無効 |
| `--job TEXT` | Job ID の部分一致フィルター（大文字小文字を区別しない） | なし |
| `--server TEXT` | NodeName の部分一致フィルター（大文字小文字を区別しない） | なし |
| `--verbose` | 警告を標準エラー出力へ表示 | 無効 |
| `--no-fail-exit` | API アクセス成功時は Failed の有無にかかわらず終了コード `0` | 無効 |
| `-h`, `--help` | ヘルプを表示 | - |

## 環境変数

| 環境変数 | 内容 |
|---|---|
| `NOMAD_ADDR` | Nomad HTTP API の URL |
| `NOMAD_TOKEN` | ACL トークン |
| `NOMAD_NAMESPACE` | 対象 Namespace |
| `NOMAD_CACERT` | PEM 形式の CA 証明書ファイル |
| `NOMAD_CLIENT_CERT` | PEM 形式のクライアント証明書ファイル |
| `NOMAD_CLIENT_KEY` | PEM 形式のクライアント秘密鍵ファイル |
| `NOMAD_SKIP_VERIFY` | `1`、`true`、`yes` のいずれかで TLS 検証を無効化 |

`NOMAD_CLIENT_CERT` と `NOMAD_CLIENT_KEY` は必ずセットで指定してください。

## 状態の判定

表示状態は Nomad の Allocation と TaskState から次のように判定されます。

| 表示 | 主な条件 |
|---|---|
| `Running` | Task の状態が `running` |
| `Pending` | Task または Allocation の状態が `pending` |
| `Stopped` | 停止が要求されている、または完了済みの Task が `dead` |
| `Failed` | Task の `Failed` が真、Allocation が `failed` / `lost`、または予期せず Task が `dead` |
| `Unknown` | 上記のどれにも該当しない |

行は `Failed`、`Unknown`、`Pending`、`Stopped`、`Running` の順に表示されるため、問題のあるタスクを上部で確認できます。

## 終了コード

| コード | 意味 |
|---:|---|
| `0` | API アクセス成功かつ Failed なし、または `--no-fail-exit` 使用時 |
| `2` | 1件以上の Failed を検出 |
| `3` | Nomad API、HTTP、TLS、または実行時エラー |
| `64` | コマンドライン引数の誤り |
| `130` | `Ctrl+C` による中断 |

終了コード `2` の判定は単発実行時に行われます。`--watch` は継続動作するため、終了するまで状態を監視します。

PowerShell で終了コードを確認する例：

```powershell
.\NomadExeMonitor.exe --only-problems
$LASTEXITCODE
```

## 取得対象と制約

次のような Nomad Job は対象になります。

```hcl
task "api" {
  driver = "raw_exec"

  config {
    command = "C:\\Apps\\MyApi\\MyApi.exe"
  }
}
```

この場合、`MyApi.exe` が表示されます。

一方、次の点に注意してください。

- `exec`、`docker` など、`raw_exec` 以外のタスクは表示されません。
- `command = "powershell.exe"` からスクリプトや別の EXE を起動している場合、表示されるのは `powershell.exe` です。
- Nomad 上の状態を表示するため、OS 上でプロセスが確実に存在することや、アプリが正常応答していることまでは保証しません。
- HTTP ヘルスチェックや Windows プロセスの直接確認は行いません。
- ACL トークンにアクセス権がない Namespace、Job、Allocation は取得できません。

## TLS の設定例

独自 CA と mTLS を使う場合：

```powershell
$env:NOMAD_ADDR = "https://nomad.example.local:4646"
$env:NOMAD_CACERT = "C:\\certs\\nomad-ca.pem"
$env:NOMAD_CLIENT_CERT = "C:\\certs\\client-cert.pem"
$env:NOMAD_CLIENT_KEY = "C:\\certs\\client-key.pem"
$env:NOMAD_TOKEN = "<Nomad ACL token>"

.\NomadExeMonitor.exe --wide
```

`--skip-tls-verify` はサーバー証明書の検証を無効化します。通信相手を検証できなくなるため、一時的な切り分け用途以外では使用しないでください。

## トラブルシューティング

### `401` または `403` が返る

`NOMAD_TOKEN` が設定されているか、トークンに Allocation と Job の読み取り権限があるか確認してください。

### EXE が表示されない

次を確認してください。

1. 対象 Task のドライバーが `raw_exec` になっている。
2. `config.command` が設定されている。
3. `--namespace`、`--job`、`--server` の条件に一致している。
4. ACL トークンに対象 Namespace の読み取り権限がある。
5. `--only-problems` によって Running 行が除外されていない。

### 証明書エラーになる

- `NOMAD_CACERT` または `--ca-cert` に正しい CA 証明書を指定してください。
- mTLS を使う場合はクライアント証明書と秘密鍵の両方を指定してください。
- 切り分け時だけ `--skip-tls-verify` を使用できます。

### Job 定義または Allocation の警告を確認したい

`--verbose` を付けると、解決できなかった Job Version や Allocation に関する警告が標準エラー出力へ表示されます。

```powershell
.\NomadExeMonitor.exe --verbose --wide
```

## 仕組み

アプリは概ね次の順で情報を取得します。

1. `/v1/allocations` から TaskState を含む Allocation 一覧を取得する。
2. ローリング更新や再スケジュールで残った履歴から、論理スロットごとの現在の Allocation を選ぶ。
3. `/v1/job/{job_id}/versions` から Allocation と同じ Job Version を解決する。
4. 必要に応じて `/v1/allocation/{allocation_id}` 内の Job スナップショットへフォールバックする。
5. 対象 TaskGroup の `raw_exec` タスクから `config.command` と EXE 名を取得する。
6. TaskState と Allocation の状態を分類して、表・JSON・CSV のいずれかで出力する。

## ライセンス

必要に応じて、プロジェクトで採用するライセンスをここに記載してください。
