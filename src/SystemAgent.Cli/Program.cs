using SystemAgent.Cli;

// WebUIと同一の操作をWebAPI経由で提供するCLI（基本設計書 2.1節・9章）。業務ロジックは持たず、SystemAgent.ClientのApiClientだけを使う。
// コマンドは Commands/ にコマンド群ごとに分けている。接続先の決定・ノード転送・エラー表示は CliContext に共通化している。

// Windowsの既定コードページ(CP932等)では日本語が化けるため、出力は常にUTF-8にする
Console.OutputEncoding = System.Text.Encoding.UTF8;

var root = CliApp.CreateRootCommand(new CliContext());
return await root.Parse(args).InvokeAsync();
