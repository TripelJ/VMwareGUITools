using System.Windows;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Quartz;
using Serilog;
using VMwareGUITools.Data;
using VMwareGUITools.Infrastructure.Checks;
using VMwareGUITools.Infrastructure.Notifications;
using VMwareGUITools.Infrastructure.Scheduling;
using VMwareGUITools.Infrastructure.Security;
using VMwareGUITools.Infrastructure.VMware;
using VMwareGUITools.UI.ViewModels;
using VMwareGUITools.UI.Views;

namespace VMwareGUITools.UI;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    private IHost? _host;

    public App()
    {
        // Prevent WPF from automatically creating a window
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        // Don't call base.OnStartup(e) to prevent automatic window creation
        OnStartupAsync(e);
    }

    private async void OnStartupAsync(StartupEventArgs e)
    {
        // Initialize Serilog
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File("logs/vmware-gui-tools-.log", 
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 30)
            .WriteTo.Console()
            .CreateLogger();

        try
        {
            // Build host with dependency injection
            _host = CreateHostBuilder(e.Args).Build();

            // Initialize database
            await InitializeDatabaseAsync();

            // Start the host
            await _host.StartAsync();

            // Show the main window
            var mainWindow = _host.Services.GetRequiredService<MainWindow>();
            mainWindow.Show();

            // Now that we have our main window, set shutdown mode to close when main window closes
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            MainWindow = mainWindow;

            // Important: Don't call base.OnStartup(e) as it might trigger default window creation
            // base.OnStartup(e);
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Application startup failed");
            MessageBox.Show($"Application failed to start: {ex.Message}", "Startup Error", 
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_host != null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }

        Log.CloseAndFlush();
        base.OnExit(e);
    }

    private static IHostBuilder CreateHostBuilder(string[] args) =>
        Host.CreateDefaultBuilder(args)
            .UseSerilog()
            .ConfigureAppConfiguration((context, config) =>
            {
                config.AddJsonFile("appsettings.json", optional: true, reloadOnChange: true);
                config.AddJsonFile($"appsettings.{context.HostingEnvironment.EnvironmentName}.json", optional: true);
                config.AddEnvironmentVariables();
                config.AddCommandLine(args);
            })
            .ConfigureServices((context, services) =>
            {
                ConfigureServices(services, context.Configuration);
            });

    private static void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        // Database
        services.AddDbContext<VMwareDbContext>(options =>
        {
            var connectionString = configuration.GetConnectionString("DefaultConnection") 
                ?? "Data Source=vmware-gui-tools.db";
            options.UseSqlite(connectionString);
        });

        // vSphere REST API Services - Replaces PowerShell/PowerCLI to avoid execution policy issues
        services.Configure<VSphereRestAPIOptions>(configuration.GetSection(VSphereRestAPIOptions.SectionName));
        services.AddHttpClient();
        services.AddScoped<IVSphereRestAPIService, VSphereRestAPIService>();
        
        // Infrastructure Services
        services.AddSingleton<ICredentialService, CredentialService>();
        services.AddScoped<IVMwareConnectionService, RestVMwareConnectionService>();

        // Check Engine Services  
        services.AddScoped<ICheckExecutionService, CheckExecutionService>();
        services.AddScoped<ICheckEngine, RestAPICheckEngine>();
        services.Configure<CheckExecutionOptions>(configuration.GetSection(CheckExecutionOptions.SectionName));

        // Notification Services
        services.AddScoped<INotificationService, NotificationService>();

        // Scheduling Services with Quartz.NET
        services.AddQuartz(q =>
        {
            q.UseSimpleTypeLoader();
            q.UseInMemoryStore();
            q.UseDefaultThreadPool(tp =>
            {
                tp.MaxConcurrency = 10;
            });
        });

        services.AddQuartzHostedService(q => q.WaitForJobsToComplete = true);
        services.AddScoped<ISchedulingService, SchedulingService>();

        // ViewModels
        services.AddTransient<MainWindowViewModel>();
        services.AddTransient<VCenterListViewModel>();
        services.AddTransient<VCenterOverviewViewModel>();
        services.AddTransient<AddVCenterViewModel>();
        services.AddTransient<EditVCenterViewModel>();
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<ClusterListViewModel>();
        services.AddTransient<HostListViewModel>();
        services.AddTransient<CheckResultsViewModel>();

        // Views - Register with factory to ensure proper ViewModel injection
        services.AddTransient<MainWindow>(provider =>
        {
            var viewModel = provider.GetRequiredService<MainWindowViewModel>();
            return new MainWindow(viewModel);
        });
        services.AddTransient<AddVCenterWindow>();
        services.AddTransient<EditVCenterWindow>();
        services.AddTransient<SettingsWindow>();
        services.AddTransient<VCenterDetailsWindow>();

        // Configuration
        services.Configure<VMwareGUIToolsOptions>(configuration.GetSection("VMwareGUITools"));
    }

    private async Task InitializeDatabaseAsync()
    {
        try
        {
            using var scope = _host!.Services.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<VMwareDbContext>();
            
            Log.Information("Initializing database...");
            
            // Force recreation of database to ensure all tables exist with current schema
            await context.Database.EnsureDeletedAsync();
            await context.Database.EnsureCreatedAsync();
            
            Log.Information("Database initialized successfully");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to initialize database");
            throw;
        }
    }
}

/// <summary>
/// Configuration options for the VMware GUI Tools application
/// </summary>
public class VMwareGUIToolsOptions
{
    public const string SectionName = "VMwareGUITools";

    public bool UseMachineLevelEncryption { get; set; } = false;
    public int ConnectionTimeoutSeconds { get; set; } = 30;
    public int DefaultCheckTimeoutSeconds { get; set; } = 300;
    public bool EnableAutoDiscovery { get; set; } = true;
    public string PowerCLIModulePath { get; set; } = string.Empty;
    public string CheckScriptsPath { get; set; } = "Scripts";
    public string ReportsPath { get; set; } = "Reports";
} 