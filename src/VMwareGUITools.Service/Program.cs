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
                ConfigureServices(builder.Services);
            }
            
            // Add Windows Service support
            builder.Services.AddWindowsService();
            
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
    
    [SupportedOSPlatform("windows")]
    private static void ConfigureServices(IServiceCollection services)
    {
        // Database
        services.AddDbContext<VMwareDbContext>();
        
        // Infrastructure services
        services.AddScoped<ICredentialService, CredentialService>();
        services.AddScoped<IVMwareConnectionService, VMwareConnectionService>();
        services.AddScoped<IPowerShellService, PowerShellService>();
        services.AddScoped<ICheckEngine, PowerCLICheckEngine>();
        services.AddScoped<ICheckExecutionService, CheckExecutionService>();
        services.AddScoped<INotificationService, NotificationService>();
        services.AddScoped<ISchedulingService, SchedulingService>();
    }
} 