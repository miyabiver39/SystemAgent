using SystemAgent.Cli;
using SystemAgent.Cli.Commands;

// WebUIと同一の操作をWebAPI経由で提供するCLI（基本設計書 2.1節・9章）。業務ロジックは持たず、SystemAgent.ClientのApiClientだけを使う。
// コマンドは Commands/ にコマンド群ごとに分けている。接続先の決定・ノード転送・エラー表示は CliContext に共通化している。

// Windowsの既定コードページ(CP932等)では日本語が化けるため、出力は常にUTF-8にする
Console.OutputEncoding = System.Text.Encoding.UTF8;

var cli = new CliContext();
var root = cli.CreateRootCommand();
SetupCommands.Register(root, cli);
AuthCommands.Register(root, cli);
ClusterCommands.Register(root, cli);
UserCommands.Register(root, cli);
DatabaseCommands.Register(root, cli);
HaCommands.Register(root, cli);
ServiceCommands.Register(root, cli);
RegistryCommands.Register(root, cli);
DeployCommands.Register(root, cli);
AuditCommands.Register(root, cli);
BackupCommands.Register(root, cli);
EnvironmentCommands.Register(root, cli);
ContainerCommands.Register(root, cli);
NtpCommands.Register(root, cli);
NetworkCommands.Register(root, cli);

return await root.Parse(args).InvokeAsync();
