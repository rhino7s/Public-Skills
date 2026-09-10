using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using SqlServerReadonlyMcp.Configuration;
using SqlServerReadonlyMcp.Logging;
using SqlServerReadonlyMcp.Security;
using SqlServerReadonlyMcp.Sql;
using SqlServerReadonlyMcp.Tools;

namespace SqlServerReadonlyMcp;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        DailyLogWriter? logWriter = null;

        try
        {
            var configPath = SettingsLoader.ResolveConfigPath(args);
            var settings = SettingsLoader.Load(configPath);
            logWriter = new DailyLogWriter(settings.Logging.Directory, settings.Logging.RetentionDays);
            logWriter.Initialize();

            var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
            {
                Args = args,
                ContentRootPath = AppContext.BaseDirectory,
            });

            builder.Logging.ClearProviders();
            var minimumLevel = Enum.Parse<LogLevel>(settings.Logging.MinimumLevel, true);
            builder.Logging.SetMinimumLevel(minimumLevel);
            builder.Logging.AddProvider(new DailyJsonLoggerProvider(logWriter, minimumLevel));

            builder.Services.AddSingleton(settings);
            builder.Services.AddSingleton(logWriter);
            builder.Services.AddSingleton<AuditLogger>();
            builder.Services.AddSingleton<SqlSafetyAnalyzer>();
            builder.Services.AddSingleton<SqlConnectionFactory>();
            builder.Services.AddSingleton<QueryConcurrencyGate>();
            builder.Services.AddSingleton<SqlQueryService>();
            builder.Services.AddSingleton<SqlMetadataService>();
            builder.Services.AddSingleton<ICapabilityStore, CapabilityStore>();
            builder.Services.AddSingleton<CapabilityService>();
            var toolJsonOptions = CreateToolJsonOptions();
            var mcp = builder.Services
                .AddMcpServer(options => options.ServerInstructions = McpServerInstructions.Text +
                    (settings.Capabilities.Enabled ? "\n\n开始业务操作前读取 list_capabilities 并遵守返回的业务说明；分页未结束时继续读取。收到用户没有访问权限后停止调用本 MCP，不尝试其他工具路径。" : string.Empty))
                .WithStdioServerTransport()
                .WithTools<SqlServerTools>(serializerOptions: toolJsonOptions)
                .WithRequestFilters(filters => filters.AddCallToolFilter(next => async (context, cancellationToken) =>
                {
                    var capabilities = context.Services?.GetService<CapabilityService>();
                    if (capabilities is null || !await capabilities.CanAccessAsync(cancellationToken).ConfigureAwait(false))
                        return CapabilityService.Denied();
                    return await next(context, cancellationToken).ConfigureAwait(false);
                }));
            if (settings.Capabilities.Enabled)
                mcp.WithTools<CapabilityTools>(serializerOptions: toolJsonOptions);

            await builder.Build().RunAsync().ConfigureAwait(false);
            return 0;
        }
        catch (SettingsException exception)
        {
            Console.Error.WriteLine($"配置错误：{exception.Message}");
            return 2;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"启动失败：{Limit(exception.Message, 2_048)}");
            return 1;
        }
        finally
        {
            logWriter?.Dispose();
        }
    }

    private static string Limit(string value, int maximumLength) =>
        value.Length <= maximumLength ? value : value[..maximumLength];

    internal static JsonSerializerOptions CreateToolJsonOptions() =>
        new(McpJsonUtilities.DefaultOptions)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        };
}
