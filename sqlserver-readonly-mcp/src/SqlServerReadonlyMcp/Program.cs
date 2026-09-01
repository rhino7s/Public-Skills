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
            var toolJsonOptions = CreateToolJsonOptions();
            builder.Services
                .AddMcpServer(options => options.ServerInstructions = McpServerInstructions.Text)
                .WithStdioServerTransport()
                .WithToolsFromAssembly(serializerOptions: toolJsonOptions);

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
