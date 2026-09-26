using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SystemAgent.Core.Auditing;
using SystemAgent.Core.Health;
using SystemAgent.Core.Nodes;
using SystemAgent.Core.Security;
using SystemAgent.Core.Users;
using SystemAgent.Core.CapabilityProviders;
using SystemAgent.Infrastructure.Auditing;
using SystemAgent.Infrastructure.Cluster;
using SystemAgent.Infrastructure.CapabilityProviders;
using SystemAgent.Infrastructure.CapabilityProviders.Containers;
using SystemAgent.Infrastructure.CapabilityProviders.Network;
using SystemAgent.Infrastructure.CapabilityProviders.Ntp;
using SystemAgent.Infrastructure.CapabilityProviders.Templates;
using SystemAgent.Infrastructure.Commands;
using SystemAgent.Infrastructure.Health;
using SystemAgent.Infrastructure.Nodes;
using SystemAgent.Infrastructure.Persistence;
using SystemAgent.Infrastructure.Security;
using SystemAgent.Infrastructure.Users;

namespace SystemAgent.Infrastructure;

public static class DependencyInjection
{
    private const string DefaultSecretStorePath = "/var/lib/systemagent/secrets";
    // 現地でOS/バージョン対応を追加・上書きするためのテンプレート置き場（同梱テンプレートと同じIdなら上書き）
    private const string DefaultExtraTemplatePath = "/etc/systemagent/templates";

    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services, IConfiguration configuration, string contentRootPath)
    {
        // ServerVersion.AutoDetect()はDB接続を要求するため使用しない。
        // MariaDB起動前でもアプリを起動可能にする必要がある（基本設計書 5.2節）。
        var serverVersion = new MariaDbServerVersion(
            Version.Parse(configuration["Database:MariaDbVersion"] ?? "10.5.0"));

        // 接続文字列はDbContext生成のたびに取得する（WebUI/CLIから変更したら再起動なしで反映）
        services.AddSingleton<DatabaseConnection>();
        services.AddDbContext<AppDbContext>((sp, options) =>
            options.UseMySql(sp.GetRequiredService<DatabaseConnection>().ConnectionStringOrPlaceholder, serverVersion));
        services.AddScoped<DatabaseMigrator>();

        var secretStorePath = Path.Combine(contentRootPath, configuration["SecretStore:Path"] ?? DefaultSecretStorePath);
        services.AddSingleton<ILocalSecretStore>(sp =>
            new LocalSecretStore(secretStorePath, sp.GetRequiredService<ILogger<LocalSecretStore>>()));

        services.AddSingleton(TimeProvider.System);

        // Capability Provider（基本設計書 7章）
        services.AddSingleton<ICommandRunner, ProcessCommandRunner>();
        services.AddSingleton<IEnvironmentDetector, EnvironmentDetector>();
        services.AddSingleton(sp => new CommandTemplateStore(
            Path.Combine(AppContext.BaseDirectory, "CommandTemplates"),
            configuration["CommandTemplates:ExtraPath"] ?? DefaultExtraTemplatePath,
            sp.GetRequiredService<ILogger<CommandTemplateStore>>()));
        // クラスタ（自己CA・mTLS・ノード参加。ADR-018）
        services.AddSingleton<ClusterIdentity>();
        services.AddSingleton<ClusterEndpointSettings>();
        services.AddSingleton<ClusterHttpClientFactory>();
        services.AddScoped<ClusterService>();

        services.AddSingleton<CapabilityTemplateResolver>();
        services.AddSingleton<ContainerRuntimeResolver>();
        services.AddSingleton<NtpResolver>();
        services.AddSingleton<NetworkResolver>();
        services.AddScoped<INodeService, NodeService>();
        services.AddScoped<IUserService, UserService>();
        services.AddScoped<IAuditLogger, DbAuditLogger>();
        services.AddScoped<IDatabaseStatus, DatabaseStatus>();

        return services;
    }
}
