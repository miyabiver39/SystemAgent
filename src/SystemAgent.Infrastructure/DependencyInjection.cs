using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SystemAgent.Infrastructure.Persistence;

namespace SystemAgent.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Default")
            ?? throw new InvalidOperationException("ConnectionStrings:Default が設定されていません。");

        // ServerVersion.AutoDetect()はDB接続を要求するため使用しない。
        // MariaDB起動前でもアプリを起動可能にする必要がある（基本設計書 5.2節）。
        var serverVersion = new MariaDbServerVersion(new Version(10, 11, 0));

        services.AddDbContext<AppDbContext>(options =>
            options.UseMySql(connectionString, serverVersion));

        return services;
    }
}
