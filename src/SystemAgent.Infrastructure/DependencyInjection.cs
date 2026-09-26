using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SystemAgent.Core.Auditing;
using SystemAgent.Core.Health;
using SystemAgent.Core.Nodes;
using SystemAgent.Core.Security;
using SystemAgent.Core.Users;
using SystemAgent.Infrastructure.Auditing;
using SystemAgent.Infrastructure.Health;
using SystemAgent.Infrastructure.Nodes;
using SystemAgent.Infrastructure.Persistence;
using SystemAgent.Infrastructure.Security;
using SystemAgent.Infrastructure.Users;

namespace SystemAgent.Infrastructure;

public static class DependencyInjection
{
    private const string DefaultSecretStorePath = "/var/lib/systemagent/secrets";

    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services, IConfiguration configuration, string contentRootPath)
    {
        var connectionString = configuration.GetConnectionString("Default")
            ?? throw new InvalidOperationException("ConnectionStrings:Default が設定されていません。");

        // ServerVersion.AutoDetect()はDB接続を要求するため使用しない。
        // MariaDB起動前でもアプリを起動可能にする必要がある（基本設計書 5.2節）。
        var serverVersion = new MariaDbServerVersion(
            Version.Parse(configuration["Database:MariaDbVersion"] ?? "10.5.0"));

        services.AddDbContext<AppDbContext>(options =>
            options.UseMySql(connectionString, serverVersion));

        var secretStorePath = Path.Combine(contentRootPath, configuration["SecretStore:Path"] ?? DefaultSecretStorePath);
        services.AddSingleton<ILocalSecretStore>(sp =>
            new LocalSecretStore(secretStorePath, sp.GetRequiredService<ILogger<LocalSecretStore>>()));

        services.AddSingleton(TimeProvider.System);
        services.AddScoped<INodeService, NodeService>();
        services.AddScoped<IUserService, UserService>();
        services.AddScoped<IAuditLogger, DbAuditLogger>();
        services.AddScoped<IDatabaseStatus, DatabaseStatus>();

        return services;
    }
}
