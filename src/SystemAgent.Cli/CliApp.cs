using System.CommandLine;
using SystemAgent.Cli.Commands;

namespace SystemAgent.Cli;

/// <summary>全コマンドを登録したルートコマンドを作る（Program とテストで共用）。</summary>
internal static class CliApp
{
    public static RootCommand CreateRootCommand(CliContext cli)
    {
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
        return root;
    }
}
