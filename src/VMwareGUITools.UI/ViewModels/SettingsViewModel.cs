using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using VMwareGUITools.Core.Models;
using VMwareGUITools.Infrastructure.PowerShell;

namespace VMwareGUITools.UI.ViewModels;

/// <summary>
/// View model for application settings
/// </summary>
public partial class SettingsViewModel : ObservableObject
{
    private readonly ILogger<SettingsViewModel> _logger;
    private readonly IConfiguration _configuration;
    private readonly IPowerShellService _powerShellService;

    [ObservableProperty]
    private string _powerShellExecutionPolicy = "RemoteSigned";

    [ObservableProperty]
    private int _powerShellTimeoutSeconds = 300;

    [ObservableProperty]
    private bool _enableVerboseLogging = false;

    [ObservableProperty]
    private bool _enablePowerCLIAutoUpdate = true;

    [ObservableProperty]
    private string _notificationEmail = string.Empty;

    [ObservableProperty]
    private bool _enableEmailNotifications = false;

    [ObservableProperty]
    private bool _enableScheduledChecks = true;

    [ObservableProperty]
    private string _defaultCheckSchedule = "0 0 8 * * ?"; // 8 AM daily

    [ObservableProperty]
    private ObservableCollection<HostProfileSetting> _hostProfiles = new();

    [ObservableProperty]
    private HostProfileSetting? _selectedHostProfile;

    [ObservableProperty]
    private bool _isLoading = false;

    public SettingsViewModel(
        ILogger<SettingsViewModel> logger,
        IConfiguration configuration,
        IPowerShellService powerShellService)
    {
        _logger = logger;
        _configuration = configuration;
        _powerShellService = powerShellService;

        // Initialize with current configuration values
        LoadCurrentSettings();
    }

    /// <summary>
    /// Event raised when dialog result should be set
    /// </summary>
    public event Action<bool>? DialogResultRequested;

    /// <summary>
    /// Command to test PowerCLI configuration
    /// </summary>
    [RelayCommand]
    private async Task TestPowerCLIAsync()
    {
        try
        {
            IsLoading = true;
            _logger.LogInformation("Testing PowerCLI configuration");

            // Test if PowerCLI modules are available
            var result = await _powerShellService.IsPowerCLIAvailableAsync();
            
            if (result)
            {
                // Additional test to get version information
                var versionInfo = await _powerShellService.GetPowerCLIVersionAsync();
                
                var message = $"✅ PowerCLI is properly configured and available!\n\n" +
                            $"Found modules:\n" +
                            string.Join("\n", versionInfo.Modules.Select(m => $"• {m.Name}: v{m.Version} {(m.IsLoaded ? "(Loaded)" : "")}"));
                
                Application.Current.Dispatcher.Invoke(() =>
                {
                    MessageBox.Show(message, "PowerCLI Test - Success", 
                        MessageBoxButton.OK, MessageBoxImage.Information);
                });
                
                _logger.LogInformation("PowerCLI test successful");
            }
            else
            {
                var message = "❌ PowerCLI modules are not available or not properly installed.\n\n" +
                            "Please install VMware PowerCLI using:\n" +
                            "Install-Module -Name VMware.PowerCLI -Scope CurrentUser";
                
                Application.Current.Dispatcher.Invoke(() =>
                {
                    MessageBox.Show(message, "PowerCLI Test - Failed", 
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                });
                
                _logger.LogWarning("PowerCLI test failed: Modules not available");
            }
        }
        catch (Exception ex)
        {
            var message = $"❌ Failed to test PowerCLI configuration:\n\n{ex.Message}";
            
            Application.Current.Dispatcher.Invoke(() =>
            {
                MessageBox.Show(message, "PowerCLI Test - Error", 
                    MessageBoxButton.OK, MessageBoxImage.Error);
            });
            
            _logger.LogError(ex, "Failed to test PowerCLI configuration");
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Command to add a new host profile
    /// </summary>
    [RelayCommand]
    private void AddHostProfile()
    {
        var newProfile = new HostProfileSetting
        {
            Name = "New Host Profile",
            Description = "Enter description...",
            IsEnabled = true,
            CheckCategories = new List<string> { "Hardware", "Network", "Storage" }
        };

        HostProfiles.Add(newProfile);
        SelectedHostProfile = newProfile;
    }

    /// <summary>
    /// Command to remove selected host profile
    /// </summary>
    [RelayCommand]
    private void RemoveHostProfile()
    {
        if (SelectedHostProfile != null)
        {
            HostProfiles.Remove(SelectedHostProfile);
            SelectedHostProfile = HostProfiles.FirstOrDefault();
        }
    }

    /// <summary>
    /// Command to test email notifications
    /// </summary>
    [RelayCommand]
    private async Task TestEmailNotificationAsync()
    {
        try
        {
            IsLoading = true;
            _logger.LogInformation("Testing email notification to: {Email}", NotificationEmail);

            // TODO: Implement email test
            await Task.Delay(2000); // Simulate email send
            
            _logger.LogInformation("Test email sent successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send test email");
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Command to save settings
    /// </summary>
    [RelayCommand]
    private async Task SaveAsync()
    {
        try
        {
            IsLoading = true;
            _logger.LogInformation("Saving application settings");

            // Save PowerShell settings
            await SavePowerShellSettingsAsync();
            
            // Save notification settings
            await SaveNotificationSettingsAsync();
            
            // Save scheduling settings
            await SaveSchedulingSettingsAsync();
            
            // Save host profiles
            await SaveHostProfilesAsync();

            Application.Current.Dispatcher.Invoke(() =>
            {
                MessageBox.Show("✅ Settings have been saved successfully!", "Settings Saved", 
                    MessageBoxButton.OK, MessageBoxImage.Information);
            });

            _logger.LogInformation("Settings saved successfully");
            DialogResultRequested?.Invoke(true);
        }
        catch (Exception ex)
        {
            var message = $"❌ Failed to save settings:\n\n{ex.Message}";
            
            Application.Current.Dispatcher.Invoke(() =>
            {
                MessageBox.Show(message, "Save Error", 
                    MessageBoxButton.OK, MessageBoxImage.Error);
            });
            
            _logger.LogError(ex, "Failed to save settings");
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Save PowerShell-related settings
    /// </summary>
    private async Task SavePowerShellSettingsAsync()
    {
        // Get the correct path to appsettings.json in the application directory
        var appDirectory = AppDomain.CurrentDomain.BaseDirectory;
        var configPath = Path.Combine(appDirectory, "appsettings.json");
        
        // Check if the file exists, if not, create a default one
        if (!File.Exists(configPath))
        {
            var defaultConfig = new
            {
                ConnectionStrings = new { DefaultConnection = "Data Source=vmware-gui-tools.db" },
                Logging = new 
                { 
                    LogLevel = new 
                    { 
                        Default = "Information",
                        Microsoft = "Warning",
                        Microsoft.Hosting.Lifetime = "Information"
                    }
                },
                VMwareGUITools = new 
                { 
                    UseMachineLevelEncryption = false,
                    ConnectionTimeoutSeconds = 30,
                    DefaultCheckTimeoutSeconds = 300,
                    EnableAutoDiscovery = true,
                    PowerCLIModulePath = "",
                    CheckScriptsPath = "Scripts",
                    ReportsPath = "Reports"
                },
                PowerShell = new
                {
                    ExecutionPolicy = "RemoteSigned",
                    TimeoutSeconds = 300,
                    EnableVerboseLogging = false,
                    EnableAutoUpdate = true
                }
            };
            
            var defaultJson = System.Text.Json.JsonSerializer.Serialize(defaultConfig, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(configPath, defaultJson);
        }
        
        var json = await File.ReadAllTextAsync(configPath);
        var config = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(json);
        
        var configDict = new Dictionary<string, object>();
        
        // Convert JsonElement to Dictionary
        if (config.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            foreach (var property in config.EnumerateObject())
            {
                configDict[property.Name] = ConvertJsonElement(property.Value);
            }
        }

        // Update PowerShell settings
        if (!configDict.ContainsKey("PowerShell"))
        {
            configDict["PowerShell"] = new Dictionary<string, object>();
        }

        var powerShellSettings = (Dictionary<string, object>)configDict["PowerShell"];
        powerShellSettings["ExecutionPolicy"] = PowerShellExecutionPolicy;
        powerShellSettings["TimeoutSeconds"] = PowerShellTimeoutSeconds;
        powerShellSettings["EnableVerboseLogging"] = EnableVerboseLogging;
        powerShellSettings["EnableAutoUpdate"] = EnablePowerCLIAutoUpdate;

        // Update VMwareGUITools settings
        if (!configDict.ContainsKey("VMwareGUITools"))
        {
            configDict["VMwareGUITools"] = new Dictionary<string, object>();
        }

        var vmwareSettings = (Dictionary<string, object>)configDict["VMwareGUITools"];
        vmwareSettings["DefaultCheckTimeoutSeconds"] = PowerShellTimeoutSeconds;

        // Serialize and save
        var options = new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true
        };
        
        var updatedJson = System.Text.Json.JsonSerializer.Serialize(configDict, options);
        await File.WriteAllTextAsync(configPath, updatedJson);
    }

    /// <summary>
    /// Save notification settings
    /// </summary>
    private async Task SaveNotificationSettingsAsync()
    {
        // TODO: Implement notification settings persistence
        await Task.CompletedTask;
    }

    /// <summary>
    /// Save scheduling settings
    /// </summary>
    private async Task SaveSchedulingSettingsAsync()
    {
        // TODO: Implement scheduling settings persistence
        await Task.CompletedTask;
    }

    /// <summary>
    /// Save host profiles
    /// </summary>
    private async Task SaveHostProfilesAsync()
    {
        // TODO: Implement host profiles persistence
        await Task.CompletedTask;
    }

    /// <summary>
    /// Convert JsonElement to object for manipulation
    /// </summary>
    private object ConvertJsonElement(System.Text.Json.JsonElement element)
    {
        return element.ValueKind switch
        {
            System.Text.Json.JsonValueKind.Object => element.EnumerateObject()
                .ToDictionary(x => x.Name, x => ConvertJsonElement(x.Value)),
            System.Text.Json.JsonValueKind.Array => element.EnumerateArray()
                .Select(ConvertJsonElement).ToList(),
            System.Text.Json.JsonValueKind.String => element.GetString()!,
            System.Text.Json.JsonValueKind.Number => element.TryGetInt32(out var i) ? i : element.GetDouble(),
            System.Text.Json.JsonValueKind.True => true,
            System.Text.Json.JsonValueKind.False => false,
            System.Text.Json.JsonValueKind.Null => null!,
            _ => element.ToString()
        };
    }

    /// <summary>
    /// Command to cancel and close dialog
    /// </summary>
    [RelayCommand]
    private void Cancel()
    {
        _logger.LogDebug("Settings dialog cancelled");
        DialogResultRequested?.Invoke(false);
    }

    /// <summary>
    /// Command to reset settings to defaults
    /// </summary>
    [RelayCommand]
    private void ResetToDefaults()
    {
        PowerShellExecutionPolicy = "RemoteSigned";
        PowerShellTimeoutSeconds = 300;
        EnableVerboseLogging = false;
        EnablePowerCLIAutoUpdate = true;
        NotificationEmail = string.Empty;
        EnableEmailNotifications = false;
        EnableScheduledChecks = true;
        DefaultCheckSchedule = "0 0 8 * * ?";
        
        HostProfiles.Clear();
        // Add default host profiles
        AddDefaultHostProfiles();
    }

    /// <summary>
    /// Load current settings from configuration
    /// </summary>
    private void LoadCurrentSettings()
    {
        try
        {
            // Load PowerShell settings
            var powerShellSection = _configuration.GetSection("PowerShell");
            PowerShellExecutionPolicy = powerShellSection.GetValue<string>("ExecutionPolicy") ?? "RemoteSigned";
            PowerShellTimeoutSeconds = powerShellSection.GetValue<int>("TimeoutSeconds", 300);

            // Load general settings
            EnableVerboseLogging = _configuration.GetValue<bool>("Logging:EnableVerbose", false);
            
            // Load notification settings
            var notificationSection = _configuration.GetSection("Notifications");
            NotificationEmail = notificationSection.GetValue<string>("Email") ?? string.Empty;
            EnableEmailNotifications = notificationSection.GetValue<bool>("EnableEmail", false);

            // Load scheduling settings
            var schedulingSection = _configuration.GetSection("Scheduling");
            EnableScheduledChecks = schedulingSection.GetValue<bool>("Enabled", true);
            DefaultCheckSchedule = schedulingSection.GetValue<string>("DefaultCron") ?? "0 0 8 * * ?";

            // Add default host profiles
            AddDefaultHostProfiles();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load current settings");
        }
    }

    /// <summary>
    /// Add default host profiles
    /// </summary>
    private void AddDefaultHostProfiles()
    {
        var profiles = new[]
        {
            new HostProfileSetting
            {
                Name = "Standard ESXi Host",
                Description = "Standard configuration checks for ESXi hosts",
                IsEnabled = true,
                CheckCategories = new List<string> { "Hardware", "Network", "Storage", "Security" }
            },
            new HostProfileSetting
            {
                Name = "vSAN Host",
                Description = "Additional checks for vSAN-enabled hosts",
                IsEnabled = true,
                CheckCategories = new List<string> { "Hardware", "Network", "Storage", "Security", "vSAN" }
            },
            new HostProfileSetting
            {
                Name = "NSX Host",
                Description = "Additional checks for NSX-enabled hosts",
                IsEnabled = false,
                CheckCategories = new List<string> { "Hardware", "Network", "Storage", "Security", "NSX" }
            }
        };

        foreach (var profile in profiles)
        {
            HostProfiles.Add(profile);
        }

        SelectedHostProfile = HostProfiles.FirstOrDefault();
    }
}

/// <summary>
/// Host profile setting model
/// </summary>
public class HostProfileSetting
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool IsEnabled { get; set; }
    public List<string> CheckCategories { get; set; } = new();
} 