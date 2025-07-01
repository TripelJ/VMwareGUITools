using System.Runtime.CompilerServices;
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
using VMwareGUITools.Core.Models;

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
        Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder(args)
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
        // Database configuration
        services.Configure<DatabaseOptions>(configuration.GetSection(DatabaseOptions.SectionName));
        
        // Database
        services.AddDbContext<VMwareDbContext>(options =>
        {
            // Resolve database path using configuration
            var databaseOptions = new DatabaseOptions();
            configuration.GetSection(DatabaseOptions.SectionName).Bind(databaseOptions);
            var databasePath = databaseOptions.ResolveDatabasePath();
            
            var connectionStringTemplate = configuration.GetConnectionString("DefaultConnection") 
                ?? "Data Source=vmware-gui-tools.db";
            var connectionString = connectionStringTemplate.Replace("{DatabasePath}", databasePath);
            
            Log.Information("=== UI Database Configuration ===");
            Log.Information("Executable Directory: {ExecutableDirectory}", AppDomain.CurrentDomain.BaseDirectory);
            Log.Information("Relative Path Setting: {RelativePath}", databaseOptions.RelativePathFromExecutable);
            Log.Information("Resolved Database Path: {DatabasePath}", databasePath);
            Log.Information("Final Connection String: {ConnectionString}", connectionString);
            
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

        // Service Communication - For GUI to Windows Service communication
        // The GUI is now a pure frontend that communicates with the Windows Service for all operations
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
        services.AddTransient<AddVCenterWindow>(provider =>
        {
            var viewModel = provider.GetRequiredService<AddVCenterViewModel>();
            return new AddVCenterWindow(viewModel);
        });
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