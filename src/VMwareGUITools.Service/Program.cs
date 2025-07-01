using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Quartz;
using Serilog;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using VMwareGUITools.Data;
using VMwareGUITools.Infrastructure.Checks;
using VMwareGUITools.Infrastructure.Notifications;
using VMwareGUITools.Infrastructure.PowerShell;
using VMwareGUITools.Infrastructure.Scheduling;
using VMwareGUITools.Infrastructure.Security;
using VMwareGUITools.Infrastructure.Services;
using VMwareGUITools.Infrastructure.VMware;

namespace VMwareGUITools.Service;

public class Program
{
    public static async Task Main(string[] args)
    {
        // Get the service's own directory for logging
        var serviceDirectory = AppDomain.CurrentDomain.BaseDirectory;
        var logsDirectory = Path.Combine(serviceDirectory, "logs");
        
        // Ensure logs directory exists
        Directory.CreateDirectory(logsDirectory);
        
        var logFilePath = Path.Combine(logsDirectory, "vmware-service-.log");
        
        // Configure Serilog
        Log.Logger = new LoggerConfiguration()
            .WriteTo.Console()
            .WriteTo.File(logFilePath, rollingInterval: RollingInterval.Day)
            .CreateLogger();

        try
        {
            Log.Information("=== VMware GUI Tools Service Starting ===");
            Log.Information("Service Directory: {ServiceDirectory}", serviceDirectory);
            Log.Information("Logs Directory: {LogsDirectory}", logsDirectory);
            Log.Information("Log File Path: {LogFilePath}", logFilePath);
            
            var builder = Host.CreateApplicationBuilder(args);
            
            // Configure services
            if (OperatingSystem.IsWindows())
            {
                ConfigureServices(builder.Services, builder.Configuration);
            }
            
            // Add Windows Service support
            builder.Services.AddWindowsService();
            
            // Add our background service that manages the heartbeat and service operations
            builder.Services.AddHostedService<VMwareBackgroundService>();
            
            // Add Quartz.NET
            builder.Services.AddQuartz(q =>
            {
                q.UseSimpleTypeLoader();
                q.UseInMemoryStore();
                q.UseDefaultThreadPool(tp =>
                {
                    tp.MaxConcurrency = 10;
                });
                
                // Register our custom job types
                q.SchedulerId = "VMwareGUIToolsScheduler";
                q.SchedulerName = "VMware GUI Tools Scheduler";
            });
            
            builder.Services.AddQuartzHostedService(opt =>
            {
                opt.WaitForJobsToComplete = true;
            });
            
            // Configure logging
            builder.Services.AddLogging(loggingBuilder =>
            {
                loggingBuilder.ClearProviders();
                loggingBuilder.AddSerilog();
            });
            
            var host = builder.Build();
            
            // Initialize database before starting the service
            await InitializeDatabaseAsync(host.Services);
            
            Log.Information("=== VMware GUI Tools Service Started Successfully ===");
            
            await host.RunAsync();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Service terminated unexpectedly");
        }
        finally
        {
            Log.Information("=== VMware GUI Tools Service Stopped ===");
            Log.CloseAndFlush();
        }
    }
    
    private static async Task InitializeDatabaseAsync(IServiceProvider services)
    {
        try
        {
            using var scope = services.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<VMwareDbContext>();
            
            // Get connection string and database path
            var connectionString = context.Database.GetConnectionString();
            var dbPath = "";
            
            if (connectionString?.Contains("Data Source=") == true)
            {
                var dataSourceStart = connectionString.IndexOf("Data Source=") + "Data Source=".Length;
                var dataSourceEnd = connectionString.IndexOf(";", dataSourceStart);
                if (dataSourceEnd == -1) dataSourceEnd = connectionString.Length;
                
                dbPath = connectionString.Substring(dataSourceStart, dataSourceEnd - dataSourceStart);
                
                // If it's a relative path, make it absolute
                if (!Path.IsPathRooted(dbPath))
                {
                    dbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, dbPath);
                }
            }
            
            Log.Information("=== Database Initialization ===");
            Log.Information("Connection String: {ConnectionString}", connectionString);
            Log.Information("Database Path: {DatabasePath}", dbPath);
            Log.Information("Database File Exists: {Exists}", File.Exists(dbPath));
            
            // Check if database exists
            var databaseExists = await context.Database.CanConnectAsync();
            Log.Information("Database Can Connect: {CanConnect}", databaseExists);
            
            if (!databaseExists)
            {
                Log.Information("Creating new database...");
                await context.Database.EnsureCreatedAsync();
                Log.Information("Database created successfully at: {DatabasePath}", dbPath);
            }
            else
            {
                // Check if all required tables exist
                var requiredTables = new[] { "ServiceCommands", "ServiceStatuses", "ServiceConfigurations" };
                var missingTables = new List<string>();
                
                foreach (var tableName in requiredTables)
                {
                    try
                    {
                        // Use parameterized query with FormattableString for safe table name checking
                        var query = $"SELECT 1 FROM {tableName} LIMIT 1";
                        await context.Database.ExecuteSqlAsync(FormattableStringFactory.Create(query));
                    }
                    catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.Message.Contains("no such table"))
                    {
                        missingTables.Add(tableName);
                    }
                }
                
                if (missingTables.Any())
                {
                    Log.Information("Missing tables detected: {MissingTables}. Recreating database to add new schema...", string.Join(", ", missingTables));
                    
                    // Delete the database file and recreate it with the current schema
                    await context.Database.EnsureDeletedAsync();
                    await context.Database.EnsureCreatedAsync();
                    
                    Log.Information("Database recreated successfully with updated schema");
                }
                else
                {
                    // Run any pending migrations
                    var pendingMigrations = await context.Database.GetPendingMigrationsAsync();
                    if (pendingMigrations.Any())
                    {
                        Log.Information("Applying {Count} pending migrations", pendingMigrations.Count());
                        await context.Database.MigrateAsync();
                    }
                }
            }
            
            // Final verification
            var finalExists = await context.Database.CanConnectAsync();
            Log.Information("Database initialization completed successfully. Can Connect: {CanConnect}", finalExists);
            Log.Information("Final Database Path: {DatabasePath}", dbPath);
            
            if (File.Exists(dbPath))
            {
                var fileInfo = new FileInfo(dbPath);
                Log.Information("Database file size: {Size} bytes, Created: {Created}", fileInfo.Length, fileInfo.CreationTime);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to initialize database");
            throw;
        }
    }
    
    [SupportedOSPlatform("windows")]
    private static void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        // Database
        services.AddDbContext<VMwareDbContext>(options =>
        {
            var connectionString = configuration.GetConnectionString("DefaultConnection") 
                ?? "Data Source=vmware-gui-tools.db";
            options.UseSqlite(connectionString);
            
            // Enable sensitive data logging for better debugging
            var environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production";
            var enableSensitiveDataLogging = environment == "Development" || 
                                           configuration.GetValue<bool>("EntityFramework:EnableSensitiveDataLogging", false);
            var enableDetailedErrors = configuration.GetValue<bool>("EntityFramework:EnableDetailedErrors", true);
            
            if (enableSensitiveDataLogging)
            {
                options.EnableSensitiveDataLogging();
            }
            
            if (enableDetailedErrors)
            {
                options.EnableDetailedErrors();
            }
        });
        
        // vSphere REST API Services
        services.Configure<VSphereRestAPIOptions>(configuration.GetSection(VSphereRestAPIOptions.SectionName));
        services.AddHttpClient();
        services.AddScoped<IVSphereRestAPIService, VSphereRestAPIService>();
        
        // PowerShell/PowerCLI Services - Available in service context with proper execution policy
        services.Configure<PowerShellOptions>(configuration.GetSection(PowerShellOptions.SectionName));
        services.Configure<PowerCLIOptions>(configuration.GetSection(PowerCLIOptions.SectionName));
        services.AddScoped<IPowerShellService, PowerShellService>();
        services.AddScoped<IPowerCLIService, PowerCLIService>();
        
        // Infrastructure services
        services.AddScoped<ICredentialService, CredentialService>();
        services.AddScoped<IVMwareConnectionService, RestVMwareConnectionService>();
        
        // Check Engines - Both REST API and PowerCLI available
        services.AddScoped<ICheckEngine, RestAPICheckEngine>();
        services.AddScoped<ICheckEngine, PowerCLICheckEngine>();
        services.Configure<CheckExecutionOptions>(configuration.GetSection(CheckExecutionOptions.SectionName));
        
        // Check Execution and Scheduling
        services.AddScoped<ICheckExecutionService, CheckExecutionService>();
        services.AddScoped<INotificationService, NotificationService>();
        services.AddScoped<ISchedulingService, SchedulingService>();
        
        // Service Configuration and Communication
        services.AddSingleton<IServiceConfigurationManager>(provider =>
            new ServiceConfigurationManager(
                provider.GetRequiredService<ILogger<ServiceConfigurationManager>>(),
                provider,
                isServiceContext: true));
    }
} 