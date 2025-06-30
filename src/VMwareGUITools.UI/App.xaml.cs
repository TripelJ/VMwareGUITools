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
using VMwareGUITools.Infrastructure.Services;
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
            
            // Enable sensitive data logging in development
            var environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production";
            var enableSensitiveDataLogging = environment == "Development" || 
                                           configuration.GetValue<bool>("EntityFramework:EnableSensitiveDataLogging", false);
            
            if (enableSensitiveDataLogging)
            {
                options.EnableSensitiveDataLogging();
            }
            
            options.EnableDetailedErrors();
        });

        // vSphere REST API Services - For GUI data display only (no execution)
        services.Configure<VSphereRestAPIOptions>(configuration.GetSection(VSphereRestAPIOptions.SectionName));
        services.AddHttpClient();
        services.AddScoped<IVSphereRestAPIService, VSphereRestAPIService>();
        
        // VMware Connection Service - For GUI testing and data retrieval only
        services.AddScoped<IVMwareConnectionService, RestVMwareConnectionService>();

        // Credential Service - For GUI credential management
        services.AddSingleton<ICredentialService, CredentialService>();

        // Service Communication - For GUI to Windows Service communication
        services.AddScoped<IServiceConfigurationManager, ServiceConfigurationManager>();

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
        services.AddTransient<EditVCenterWindow>(provider =>
        {
            var viewModel = provider.GetRequiredService<EditVCenterViewModel>();
            return new EditVCenterWindow { DataContext = viewModel };
        });
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
            
            // Check if database exists
            var databaseExists = await context.Database.CanConnectAsync();
            
            if (!databaseExists)
            {
                Log.Information("Creating new database...");
                await context.Database.EnsureCreatedAsync();
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
                        await context.Database.ExecuteSqlAsync($"SELECT 1 FROM {tableName} LIMIT 1");
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