using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Quartz;
using Serilog;
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
        // Configure Serilog
        Log.Logger = new LoggerConfiguration()
            .WriteTo.Console()
            .WriteTo.File("logs/vmware-service-.txt", rollingInterval: RollingInterval.Day)
            .CreateLogger();

        try
        {
            Log.Information("Starting VMware GUI Tools Service");
            
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
            
            await host.RunAsync();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Service terminated unexpectedly");
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }
    
    private static async Task InitializeDatabaseAsync(IServiceProvider services)
    {
        try
        {
            using var scope = services.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<VMwareDbContext>();
            
            Log.Information("Initializing database...");
            await context.Database.EnsureCreatedAsync();
            
            // Run any pending migrations
            var pendingMigrations = await context.Database.GetPendingMigrationsAsync();
            if (pendingMigrations.Any())
            {
                Log.Information("Applying {Count} pending migrations", pendingMigrations.Count());
                await context.Database.MigrateAsync();
            }
            
            Log.Information("Database initialized successfully");
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
        services.AddScoped<IServiceConfigurationManager, ServiceConfigurationManager>();
    }
} 