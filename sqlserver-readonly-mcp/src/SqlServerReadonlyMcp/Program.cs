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
            builder.Services.AddSingleton<CatalogSqlAnalyzer>();
            builder.Services.AddSingleton<CatalogAccessService>();
            builder.Services.AddSingleton<SqlConnectionFactory>();
            builder.Services.AddSingleton<QueryConcurrencyGate>();
            builder.Services.AddSingleton<SqlQueryService>();
            builder.Services.AddSingleton<SqlMetadataService>();
            builder.Services.AddSingleton<ICapabilityStore, CapabilityStore>();
            builder.Services.AddSingleton<CapabilityService>();
            var toolJsonOptions = CreateToolJsonOptions();
            var mcp = builder.Services
                .AddMcpServer(options => options.ServerInstructions = McpServerInstructions.Build(settings.Capabilities.Enabled, settings.IsCatalogMode))
                .WithStdioServerTransport()
                .WithRequestFilters(filters => filters.AddCallToolFilter(next => async (context, cancellationToken) =>
                {
                    using var timing = new CallTiming(cancellationToken);
                    ModelContextProtocol.Protocol.CallToolResult? response = null;
                    try
                    {
                        var capabilities = context.Services?.GetService<CapabilityService>();
                        if (capabilities is null) return response = CapabilityService.Unavailable();
                        if (capabilities.RequiresCheck)
                        {
                            using var phase = timing.Measure("authorization");
                            var rejection = await capabilities.CheckAccessAsync(timing.Preflight.Token).ConfigureAwait(false);
                            if (timing.Preflight.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                                return response = CapabilityService.CheckTimedOut();
                            if (rejection is not null) return response = rejection;
                        }
                        if (context.Params?.Name is "execute_sql" or "execute_procedure")
                            return response = await next(context, cancellationToken).ConfigureAwait(false);
                        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        deadline.CancelAfter(TimeSpan.FromSeconds(settings.Query.TimeoutSeconds));
                        using var execution = timing.Measure("execution");
                        return response = await next(context, deadline.Token).ConfigureAwait(false);
                    }
                    finally
                    {
                        if (!timing.Audited)
                            context.Services?.GetService<AuditLogger>()?.WriteTool(new ToolAuditEvent(timing.RequestId,
                                context.Params?.Name ?? "unknown", null, Convert.ToInt64(timing.Snapshot()["total_ms"]),
                                response is { IsError: false } ? "success" : "error",
                                ErrorCategory: response?.StructuredContent is { } body && body.TryGetProperty("code", out var code)
                                    ? code.GetString() : response is { IsError: false } ? null : "tool_error"));
                    }
                }));
            if (settings.IsCatalogMode) mcp.WithTools<CatalogQueryTools>(serializerOptions: toolJsonOptions);
            else mcp.WithTools<SqlServerTools>(serializerOptions: toolJsonOptions);
            if (settings.Capabilities.Enabled)
            {
                mcp.WithTools<CapabilityTools>(serializerOptions: toolJsonOptions);
                if (settings.IsCatalogMode) mcp.WithTools<CatalogProcedureTools>(serializerOptions: toolJsonOptions);
                else mcp.WithTools<ProcedureTools>(serializerOptions: toolJsonOptions);
            }

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
