# NomadExeMonitor 監視バッチ

`NomadExeMonitor-watch.bat` は、`NomadExeMonitor.exe` を継続監視モードで起動するための Windows バッチファイルです。

既定では5秒ごとにNomadの状態を取得し、Taskや直近イベントを含む詳細な一覧を表示します。

## ファイルの配置

次の2ファイルを同じフォルダーへ配置してください。

```text
NomadExeMonitor/
├─ NomadExeMonitor.exe
└─ NomadExeMonitor-watch.bat
```

バッチは自身と同じフォルダーにある `NomadExeMonitor.exe` を起動します。EXEが見つからない場合はエラーを表示して終了します。

## 基本的な使い方

1. 必要に応じて接続先やACLトークンを設定します。
2. `NomadExeMonitor-watch.bat` をダブルクリックします。
3. 終了するときはコンソールで `Ctrl+C` を押します。

コマンドプロンプトから起動する場合：

```bat
NomadExeMonitor-watch.bat
```

PowerShellから起動する場合：

```powershell
.\NomadExeMonitor-watch.bat
```

## 既定の設定

| 項目 | 既定値 | 内容 |
|---|---|---|
| Nomad接続先 | `http://127.0.0.1:4646` | `NOMAD_ADDR` が未設定の場合に使用 |
| Namespace | `*` | `NOMAD_NAMESPACE` が未設定の場合に使用 |
| 更新間隔 | 5秒 | `--watch 5` |
| 表示 | 詳細表示 | `--wide` |
| 警告 | 表示する | `--verbose` |

実行される基本コマンドは次の内容です。

```bat
NomadExeMonitor.exe --watch 5 --wide --verbose
```

`--only-problems` は既定では指定していません。復旧して `Running` へ戻ったことも画面上で確認できるようにするためです。

## Nomad接続先の変更

### バッチの既定値を変更する

`NomadExeMonitor-watch.bat` をテキストエディターで開き、次の行を書き換えます。

```bat
if not defined NOMAD_ADDR set "NOMAD_ADDR=http://127.0.0.1:4646"
```

例：

```bat
if not defined NOMAD_ADDR set "NOMAD_ADDR=https://nomad.example.local:4646"
```

### 実行時だけ変更する

PowerShellの場合：

```powershell
$env:NOMAD_ADDR = "https://nomad.example.local:4646"
.\NomadExeMonitor-watch.bat
```

コマンドプロンプトの場合：

```bat
set "NOMAD_ADDR=https://nomad.example.local:4646"
NomadExeMonitor-watch.bat
```

既に環境変数 `NOMAD_ADDR` が設定されている場合、バッチ内の既定値より環境変数が優先されます。

## Namespaceの変更

全Namespaceではなく、特定のNamespaceだけを監視する場合は `NOMAD_NAMESPACE` を設定します。

PowerShellの場合：

```powershell
$env:NOMAD_NAMESPACE = "production"
.\NomadExeMonitor-watch.bat
```

バッチに固定する場合は次の行を変更します。

```bat
if not defined NOMAD_NAMESPACE set "NOMAD_NAMESPACE=production"
```

`*` を使用した場合は、ACLトークンに読み取り権限がある全Namespaceが対象になります。

## ACLトークンの設定

ACLが有効なNomad環境では、実行前に `NOMAD_TOKEN` を設定してください。

PowerShellの場合：

```powershell
$env:NOMAD_TOKEN = "<Nomad ACL token>"
.\NomadExeMonitor-watch.bat
```

コマンドプロンプトの場合：

```bat
set "NOMAD_TOKEN=<Nomad ACL token>"
NomadExeMonitor-watch.bat
```

トークンをバッチファイルへ直接記載すると、ファイルを読める人にトークンが漏れる可能性があります。通常は環境変数として設定してください。

## 監視オプションの変更

バッチでは `NOMAD_MONITOR_ARGS` に起動オプションを設定しています。

```bat
if not defined NOMAD_MONITOR_ARGS set "NOMAD_MONITOR_ARGS=--watch 5 --wide --verbose"
```

### 更新間隔を10秒にする

```bat
if not defined NOMAD_MONITOR_ARGS set "NOMAD_MONITOR_ARGS=--watch 10 --wide --verbose"
```

### 問題のあるタスクだけ表示する

```bat
if not defined NOMAD_MONITOR_ARGS set "NOMAD_MONITOR_ARGS=--watch 5 --wide --verbose --only-problems"
```

この設定では、復旧して `Running` になった行は一覧から消えます。

### 特定のJobだけ表示する

```bat
if not defined NOMAD_MONITOR_ARGS set "NOMAD_MONITOR_ARGS=--watch 5 --wide --verbose --job api"
```

`--job` はJob IDの部分一致で、大文字と小文字を区別しません。

### 特定のサーバーだけ表示する

```bat
if not defined NOMAD_MONITOR_ARGS set "NOMAD_MONITOR_ARGS=--watch 5 --wide --verbose --server nomad-client-01"
```

`--server` はNomadのNodeNameに対する部分一致です。

### バッチを編集せず一時的に変更する

PowerShellの場合：

```powershell
$env:NOMAD_MONITOR_ARGS = "--watch 10 --wide --verbose --only-problems"
.\NomadExeMonitor-watch.bat
```

## HTTPSと証明書

独自CAを使用する場合は、`NOMAD_CACERT` にPEM形式のCA証明書を指定します。

```powershell
$env:NOMAD_ADDR = "https://nomad.example.local:4646"
$env:NOMAD_CACERT = "C:\certs\nomad-ca.pem"
$env:NOMAD_TOKEN = "<Nomad ACL token>"
.\NomadExeMonitor-watch.bat
```

mTLSを使用する場合は、クライアント証明書と秘密鍵をセットで指定してください。

```powershell
$env:NOMAD_CLIENT_CERT = "C:\certs\client-cert.pem"
$env:NOMAD_CLIENT_KEY = "C:\certs\client-key.pem"
```

証明書の検証を無効化する場合は `NOMAD_SKIP_VERIFY=true` を設定できますが、通信相手を検証できなくなります。一時的な切り分け以外では使用しないでください。

## バッチ終了時の動作

NomadExeMonitorが終了すると、バッチは終了理由を表示して `pause` します。そのため、ダブルクリックで実行した場合でもメッセージを確認できます。

| 終了コード | バッチ上の意味 |
|---:|---|
| `0` | 正常終了 |
| `1` | `NomadExeMonitor.exe` が見つからない |
| `3` | API、HTTP、TLS、または実行時エラーの可能性 |
| `130` | `Ctrl+C` による監視終了 |

`--watch` モードは継続動作するため、通常は `Ctrl+C` を押すまで終了しません。

## トラブルシューティング

### `NomadExeMonitor.exe was not found` と表示される

`NomadExeMonitor-watch.bat` と `NomadExeMonitor.exe` を同じフォルダーへ置いてください。EXEのファイル名も変更しないでください。

### Nomadへ接続できない

起動時に表示される `Address` を確認してください。接続先が異なる場合は `NOMAD_ADDR` またはバッチ内の既定値を変更します。

また、次も確認してください。

- Nomad APIのポートへネットワーク接続できる。
- HTTPとHTTPSの指定が正しい。
- ファイアウォールやプロキシに遮断されていない。
- HTTPSの場合、CA証明書の設定が正しい。

### `401` または `403` エラーになる

`NOMAD_TOKEN` が設定されているか、トークンに対象NamespaceのAllocationとJobを読み取る権限があるか確認してください。

### EXEが一覧に表示されない

次を確認してください。

1. Nomad Taskのドライバーが `raw_exec` である。
2. Taskの `config.command` が設定されている。
3. `NOMAD_NAMESPACE` が対象Namespaceと一致している。
4. `--job` や `--server` の条件で除外されていない。
5. `--only-problems` によって `Running` の行が非表示になっていない。

## 推奨設定

通常の目視監視では、既定の設定を推奨します。

```text
--watch 5 --wide --verbose
```

この設定は、状態変化をおおむね数秒単位で拾いながら、復旧したタスクも確認できます。ただしNomadのイベントストリームを購読するリアルタイム通知ではなく、一定間隔でAPIを再取得するポーリング方式です。
