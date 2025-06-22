using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Collections.ObjectModel;
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

            var result = await _powerShellService.ExecuteScriptAsync("Get-Module -ListAvailable VMware.PowerCLI");
            
            if (result.IsSuccess)
            {
                // TODO: Show success message
                _logger.LogInformation("PowerCLI test successful");
            }
            else
            {
                // TODO: Show error message
                _logger.LogWarning("PowerCLI test failed: {Error}", result.ErrorMessage);
            }
        }
        catch (Exception ex)
        {
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

            // TODO: Save settings to configuration
            await Task.Delay(500); // Simulate save

            _logger.LogInformation("Settings saved successfully");
            DialogResultRequested?.Invoke(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save settings");
        }
        finally
        {
            IsLoading = false;
        }
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