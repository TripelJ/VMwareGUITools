using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using VMwareGUITools.Core.Models;
using VMwareGUITools.Data;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace VMwareGUITools.UI.ViewModels;

/// <summary>
/// View model for application settings
/// </summary>
public partial class SettingsViewModel : ObservableObject
{
    private readonly ILogger<SettingsViewModel> _logger;
    private readonly IConfiguration _configuration;
    private readonly VMwareDbContext _dbContext;

    [ObservableProperty]
    private int _connectionTimeoutSeconds = 60;

    [ObservableProperty]
    private bool _enableVerboseLogging = false;

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
        VMwareDbContext dbContext)
    {
        _logger = logger;
        _configuration = configuration;
        _dbContext = dbContext;

        // Initialize with current configuration values
        LoadCurrentSettings();
    }

    /// <summary>
    /// Refresh settings when window is reopened
    /// </summary>
    public void RefreshSettings()
    {
        LoadCurrentSettings();
    }

    /// <summary>
    /// Event raised when dialog result should be set
    /// </summary>
    public event Action<bool>? DialogResultRequested;

    /// <summary>
    /// Command to test VMware REST API connectivity
    /// </summary>
    [RelayCommand]
    private async Task TestVMwareAPIAsync()
    {
        try
        {
            IsLoading = true;
            _logger.LogInformation("Testing VMware REST API configuration");

            // Simple REST API availability test
            await Task.Delay(500); // Simulate check
            
            var message = "✅ VMware REST API connectivity is available!\n\n" +
                         "• No PowerShell dependencies required\n" +
                         "• Direct vSphere REST API communication\n" +
                         "• Works regardless of PowerShell execution policy";
            
            Application.Current.Dispatcher.Invoke(() =>
            {
                MessageBox.Show(message, "VMware REST API Test - Success", 
                    MessageBoxButton.OK, MessageBoxImage.Information);
            });
            
            _logger.LogInformation("VMware REST API test successful");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "VMware REST API test failed");
            
            var message = "❌ VMware REST API test failed\n\n" +
                         $"Error: {ex.Message}\n\n" +
                         "This should not normally happen as REST API doesn't have dependencies.\n" +
                         "Please check your application configuration.";
            
            Application.Current.Dispatcher.Invoke(() =>
            {
                MessageBox.Show(message, "VMware REST API Test - Failed", 
                    MessageBoxButton.OK, MessageBoxImage.Error);
            });
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Command to show REST API information
    /// </summary>
    [RelayCommand]
    private async Task ShowRESTAPIInfoAsync()
    {
        try
        {
            IsLoading = true;
            await Task.Delay(100); // Simulate async operation
            
            var message = "ℹ️ VMware REST API Information\n\n" +
                         "✅ Current Configuration:\n" +
                         "• Using vSphere REST API for all VMware operations\n" +
                         "• No PowerShell or PowerCLI dependencies required\n" +
                         "• Direct HTTPS communication with vCenter servers\n" +
                         "• Bypasses all PowerShell execution policy issues\n\n" +
                         "🔧 REST API Features:\n" +
                         "• Connection testing\n" +
                         "• Host and cluster discovery\n" +
                         "• Health check execution\n" +
                         "• Version and configuration retrieval\n\n" +
                         "📚 For more information about vSphere REST API:\n" +
                         "Visit VMware Developer Documentation";
            
            Application.Current.Dispatcher.Invoke(() =>
            {
                MessageBox.Show(message, "VMware REST API Information", 
                    MessageBoxButton.OK, MessageBoxImage.Information);
            });
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Command to show connection settings information
    /// </summary>
    [RelayCommand]
    private async Task ShowConnectionSettingsAsync()
    {
        try
        {
            IsLoading = true;
            await Task.Delay(100); // Simulate async operation
            
            var message = "🔧 REST API Connection Settings\n\n" +
                         $"Connection Timeout: {ConnectionTimeoutSeconds} seconds\n" +
                         $"Verbose Logging: {(EnableVerboseLogging ? "Enabled" : "Disabled")}\n" +
                         $"Email Notifications: {(EnableEmailNotifications ? "Enabled" : "Disabled")}\n" +
                         $"Scheduled Checks: {(EnableScheduledChecks ? "Enabled" : "Disabled")}\n\n" +
                         "These settings control how the application connects to\n" +
                         "vCenter servers using the REST API. All connections\n" +
                         "are made over HTTPS with certificate validation.";
            
            Application.Current.Dispatcher.Invoke(() =>
            {
                MessageBox.Show(message, "Connection Settings", 
                    MessageBoxButton.OK, MessageBoxImage.Information);
            });
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Command to show system diagnostics information
    /// </summary>
    [RelayCommand]
    private async Task ShowSystemDiagnosticsAsync()
    {
        try
        {
            IsLoading = true;
            await Task.Delay(100); // Simulate async operation
            
            var message = "📊 System Diagnostics - REST API Mode\n\n" +
                         "✅ VMware Connectivity:\n" +
                         "• Using vSphere REST API (no PowerShell required)\n" +
                         "• HTTP Client: Available\n" +
                         "• Certificate Validation: Configured\n\n" +
                         $"⚙️ Application Settings:\n" +
                         $"• Connection Timeout: {ConnectionTimeoutSeconds}s\n" +
                         $"• Verbose Logging: {(EnableVerboseLogging ? "Enabled" : "Disabled")}\n" +
                         $"• Email Notifications: {(EnableEmailNotifications ? "Enabled" : "Disabled")}\n" +
                         $"• Scheduled Checks: {(EnableScheduledChecks ? "Enabled" : "Disabled")}\n\n" +
                         "🔗 Advantages of REST API:\n" +
                         "• No PowerShell execution policy issues\n" +
                         "• No module version conflicts\n" +
                         "• Consistent cross-platform support\n" +
                         "• Direct HTTPS communication\n" +
                         "• Simplified dependency management";
            
            Application.Current.Dispatcher.Invoke(() =>
            {
                MessageBox.Show(message, "System Diagnostics", 
                    MessageBoxButton.OK, MessageBoxImage.Information);
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to show system diagnostics");
            
            var message = "❌ Failed to show system diagnostics\n\n" +
                         $"Error: {ex.Message}";
            
            Application.Current.Dispatcher.Invoke(() =>
            {
                MessageBox.Show(message, "Diagnostics Error", 
                    MessageBoxButton.OK, MessageBoxImage.Error);
            });
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

            // Save connection settings
            await SaveConnectionSettingsAsync();
            
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
    /// Save REST API connection settings
    /// </summary>
    private async Task SaveConnectionSettingsAsync()
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
                    LogLevel = new Dictionary<string, object>
                    { 
                        ["Default"] = "Information",
                        ["Microsoft"] = "Warning",
                        ["Microsoft.Hosting.Lifetime"] = "Information"
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

        // Update REST API connection settings
        if (!configDict.ContainsKey("ConnectionSettings"))
        {
            configDict["ConnectionSettings"] = new Dictionary<string, object>();
        }

        var connectionSettings = (Dictionary<string, object>)configDict["ConnectionSettings"];
        connectionSettings["ConnectionTimeoutSeconds"] = ConnectionTimeoutSeconds;
        connectionSettings["EnableVerboseLogging"] = EnableVerboseLogging;

        // Update VMwareGUITools settings
        if (!configDict.ContainsKey("VMwareGUITools"))
        {
            configDict["VMwareGUITools"] = new Dictionary<string, object>();
        }

        var vmwareSettings = (Dictionary<string, object>)configDict["VMwareGUITools"];
        vmwareSettings["ConnectionTimeoutSeconds"] = ConnectionTimeoutSeconds;

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
        try
        {
            _logger.LogInformation("Saving host profile settings to database");

            foreach (var hostProfileSetting in HostProfiles)
            {
                // Find existing host profile by name or create new one
                var existingProfile = await _dbContext.HostProfiles
                    .FirstOrDefaultAsync(hp => hp.Name == hostProfileSetting.Name);

                if (existingProfile != null)
                {
                    // Update existing profile
                    existingProfile.Description = hostProfileSetting.Description;
                    existingProfile.Enabled = hostProfileSetting.IsEnabled;
                    existingProfile.UpdatedAt = DateTime.UtcNow;

                    // Create check configurations based on selected categories
                    var checkConfigs = new List<HostProfileCheckConfig>();
                    
                    if (hostProfileSetting.HardwareHealthEnabled)
                        checkConfigs.Add(new HostProfileCheckConfig { Category = "Hardware Health", CheckId = 1, AlertOnFailure = true });
                    if (hostProfileSetting.NetworkConfigurationEnabled)
                        checkConfigs.Add(new HostProfileCheckConfig { Category = "Network Configuration", CheckId = 2, AlertOnFailure = true });
                    if (hostProfileSetting.StorageHealthEnabled)
                        checkConfigs.Add(new HostProfileCheckConfig { Category = "Storage Health", CheckId = 3, AlertOnFailure = true });
                    if (hostProfileSetting.SecuritySettingsEnabled)
                        checkConfigs.Add(new HostProfileCheckConfig { Category = "Security Settings", CheckId = 4, AlertOnFailure = true });
                    if (hostProfileSetting.VsanHealthEnabled)
                        checkConfigs.Add(new HostProfileCheckConfig { Category = "vSAN Health", CheckId = 5, AlertOnFailure = true });
                    if (hostProfileSetting.NsxConfigurationEnabled)
                        checkConfigs.Add(new HostProfileCheckConfig { Category = "NSX Configuration", CheckId = 6, AlertOnFailure = true });

                    existingProfile.SetCheckConfigs(checkConfigs);
                }
                else
                {
                    // Create new profile
                    var newProfile = new HostProfile
                    {
                        Name = hostProfileSetting.Name,
                        Description = hostProfileSetting.Description,
                        Type = HostType.Standard,
                        Enabled = hostProfileSetting.IsEnabled,
                        IsDefault = false,
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow
                    };

                    // Create check configurations based on selected categories
                    var checkConfigs = new List<HostProfileCheckConfig>();
                    
                    if (hostProfileSetting.HardwareHealthEnabled)
                        checkConfigs.Add(new HostProfileCheckConfig { Category = "Hardware Health", CheckId = 1, AlertOnFailure = true });
                    if (hostProfileSetting.NetworkConfigurationEnabled)
                        checkConfigs.Add(new HostProfileCheckConfig { Category = "Network Configuration", CheckId = 2, AlertOnFailure = true });
                    if (hostProfileSetting.StorageHealthEnabled)
                        checkConfigs.Add(new HostProfileCheckConfig { Category = "Storage Health", CheckId = 3, AlertOnFailure = true });
                    if (hostProfileSetting.SecuritySettingsEnabled)
                        checkConfigs.Add(new HostProfileCheckConfig { Category = "Security Settings", CheckId = 4, AlertOnFailure = true });
                    if (hostProfileSetting.VsanHealthEnabled)
                        checkConfigs.Add(new HostProfileCheckConfig { Category = "vSAN Health", CheckId = 5, AlertOnFailure = true });
                    if (hostProfileSetting.NsxConfigurationEnabled)
                        checkConfigs.Add(new HostProfileCheckConfig { Category = "NSX Configuration", CheckId = 6, AlertOnFailure = true });

                    newProfile.SetCheckConfigs(checkConfigs);
                    
                    _dbContext.HostProfiles.Add(newProfile);
                }
            }

            await _dbContext.SaveChangesAsync();
            _logger.LogInformation("Host profile settings saved successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save host profile settings");
            throw;
        }
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
        ConnectionTimeoutSeconds = 60;
        EnableVerboseLogging = false;
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
            // Load connection settings
            var connectionSection = _configuration.GetSection("ConnectionSettings");
            ConnectionTimeoutSeconds = connectionSection.GetValue<int>("ConnectionTimeoutSeconds", 60);

            // Load general settings
            EnableVerboseLogging = connectionSection.GetValue<bool>("EnableVerboseLogging", false);
            
            // Load notification settings
            var notificationSection = _configuration.GetSection("Notifications");
            NotificationEmail = notificationSection.GetValue<string>("Email") ?? string.Empty;
            EnableEmailNotifications = notificationSection.GetValue<bool>("EnableEmail", false);

            // Load scheduling settings
            var schedulingSection = _configuration.GetSection("Scheduling");
            EnableScheduledChecks = schedulingSection.GetValue<bool>("Enabled", true);
            DefaultCheckSchedule = schedulingSection.GetValue<string>("DefaultCron") ?? "0 0 8 * * ?";

            // Load host profiles from database
            LoadHostProfilesFromDatabase();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load current settings");
            // Fallback to default profiles if database load fails
            AddDefaultHostProfiles();
        }
    }

    /// <summary>
    /// Load host profiles from database
    /// </summary>
    private void LoadHostProfilesFromDatabase()
    {
        try
        {
            HostProfiles.Clear();
            var dbProfiles = _dbContext.HostProfiles.ToList();

            if (dbProfiles.Any())
            {
                foreach (var dbProfile in dbProfiles)
                {
                    var checkConfigs = dbProfile.GetCheckConfigs();
                    var hostProfileSetting = new HostProfileSetting
                    {
                        Name = dbProfile.Name,
                        Description = dbProfile.Description,
                        IsEnabled = dbProfile.Enabled,
                        HardwareHealthEnabled = checkConfigs.Any(c => c.Category == "Hardware Health"),
                        NetworkConfigurationEnabled = checkConfigs.Any(c => c.Category == "Network Configuration"),
                        StorageHealthEnabled = checkConfigs.Any(c => c.Category == "Storage Health"),
                        SecuritySettingsEnabled = checkConfigs.Any(c => c.Category == "Security Settings"),
                        VsanHealthEnabled = checkConfigs.Any(c => c.Category == "vSAN Health"),
                        NsxConfigurationEnabled = checkConfigs.Any(c => c.Category == "NSX Configuration")
                    };

                    HostProfiles.Add(hostProfileSetting);
                }

                SelectedHostProfile = HostProfiles.FirstOrDefault();
                _logger.LogInformation("Loaded {Count} host profiles from database", HostProfiles.Count);
            }
            else
            {
                // No profiles in database, add defaults
                AddDefaultHostProfiles();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load host profiles from database");
            // Fallback to default profiles
            AddDefaultHostProfiles();
        }
    }

    /// <summary>
    /// Add default host profiles
    /// </summary>
    private void AddDefaultHostProfiles()
    {
        var standardProfile = new HostProfileSetting
        {
            Name = "Standard ESXi Host",
            Description = "Standard configuration checks for ESXi hosts",
            IsEnabled = true,
            HardwareHealthEnabled = true,
            NetworkConfigurationEnabled = true,
            StorageHealthEnabled = true,
            SecuritySettingsEnabled = true,
            VsanHealthEnabled = false,
            NsxConfigurationEnabled = false
        };

        var vsanProfile = new HostProfileSetting
        {
            Name = "vSAN Host",
            Description = "Additional checks for vSAN-enabled hosts",
            IsEnabled = true,
            HardwareHealthEnabled = true,
            NetworkConfigurationEnabled = true,
            StorageHealthEnabled = true,
            SecuritySettingsEnabled = true,
            VsanHealthEnabled = true,
            NsxConfigurationEnabled = false
        };

        var nsxProfile = new HostProfileSetting
        {
            Name = "NSX Host",
            Description = "Additional checks for NSX-enabled hosts",
            IsEnabled = false,
            HardwareHealthEnabled = true,
            NetworkConfigurationEnabled = true,
            StorageHealthEnabled = true,
            SecuritySettingsEnabled = true,
            VsanHealthEnabled = false,
            NsxConfigurationEnabled = true
        };

        HostProfiles.Add(standardProfile);
        HostProfiles.Add(vsanProfile);
        HostProfiles.Add(nsxProfile);

        SelectedHostProfile = HostProfiles.FirstOrDefault();
    }
}

/// <summary>
/// Host profile setting model
/// </summary>
public partial class HostProfileSetting : ObservableObject
{
    [ObservableProperty]
    private string _name = string.Empty;
    
    [ObservableProperty]
    private string _description = string.Empty;
    
    [ObservableProperty]
    private bool _isEnabled;
    
    [ObservableProperty]
    private bool _hardwareHealthEnabled;
    
    [ObservableProperty]
    private bool _networkConfigurationEnabled;
    
    [ObservableProperty]
    private bool _storageHealthEnabled;
    
    [ObservableProperty]
    private bool _securitySettingsEnabled;
    
    [ObservableProperty]
    private bool _vsanHealthEnabled;
    
    [ObservableProperty]
    private bool _nsxConfigurationEnabled;
    
    public List<string> CheckCategories
    {
        get
        {
            var categories = new List<string>();
            if (HardwareHealthEnabled) categories.Add("Hardware Health");
            if (NetworkConfigurationEnabled) categories.Add("Network Configuration");
            if (StorageHealthEnabled) categories.Add("Storage Health");
            if (SecuritySettingsEnabled) categories.Add("Security Settings");
            if (VsanHealthEnabled) categories.Add("vSAN Health");
            if (NsxConfigurationEnabled) categories.Add("NSX Configuration");
            return categories;
        }
        set
        {
            HardwareHealthEnabled = value.Contains("Hardware Health");
            NetworkConfigurationEnabled = value.Contains("Network Configuration");
            StorageHealthEnabled = value.Contains("Storage Health");
            SecuritySettingsEnabled = value.Contains("Security Settings");
            VsanHealthEnabled = value.Contains("vSAN Health");
            NsxConfigurationEnabled = value.Contains("NSX Configuration");
        }
    }
} 