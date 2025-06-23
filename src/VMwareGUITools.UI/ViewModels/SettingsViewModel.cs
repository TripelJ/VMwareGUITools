using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Collections.ObjectModel;
using System.IO;
using System.Management.Automation;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using VMwareGUITools.Core.Models;
using VMwareGUITools.Infrastructure.PowerShell;
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
    private readonly IPowerShellService _powerShellService;
    private readonly VMwareDbContext _dbContext;

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
        IPowerShellService powerShellService,
        VMwareDbContext dbContext)
    {
        _logger = logger;
        _configuration = configuration;
        _powerShellService = powerShellService;
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
                // Try to get more detailed error information
                try
                {
                    var detailedResult = await _powerShellService.ExecuteScriptAsync(@"
                        # Check for version conflicts and provide specific guidance
                        $vmwareModules = Get-Module -ListAvailable -Name '*VMware*' | Group-Object Name
                        $hasVMware = $vmwareModules.Count -gt 0
                        $vimModule = Get-Module -ListAvailable -Name 'VMware.Vim' -ErrorAction SilentlyContinue | Select-Object -First 1
                        $commonModule = Get-Module -ListAvailable -Name 'VMware.VimAutomation.Common' -ErrorAction SilentlyContinue | Select-Object -First 1
                        
                        $issues = @()
                        $solutions = @()
                        
                        if (-not $hasVMware) {
                            $issues += 'No VMware modules found'
                            $solutions += 'Install PowerCLI: Install-Module -Name VMware.PowerCLI -Scope CurrentUser'
                        } elseif ($vimModule -and $commonModule) {
                            $vimVersion = $vimModule.Version
                            $commonVersion = $commonModule.Version
                            if ($vimVersion.Major -eq 9 -and $commonVersion.Major -ne 9) {
                                $issues += ""Version conflict: VMware.Vim v$vimVersion incompatible with VMware.VimAutomation.Common v$commonVersion""
                                $solutions += 'Run the PowerCLI cleanup script from the application folder'
                                $solutions += 'Or manually: Uninstall-Module VMware.PowerCLI -AllVersions; Install-Module VMware.PowerCLI -Force'
                            }
                        }
                        
                        # Try to import and see what specific error occurs
                        try {
                            Import-Module VMware.PowerCLI -ErrorAction Stop
                            $issues += 'PowerCLI modules present but failed availability check'
                        } catch {
                            $issues += ""Import failed: $($_.Exception.Message)""
                        }
                        
                        [PSCustomObject]@{
                            Issues = $issues
                            Solutions = $solutions
                            ModuleCount = $vmwareModules.Count
                        }
                    ");

                    if (detailedResult.IsSuccess && detailedResult.Objects.Count > 0)
                    {
                        dynamic? resultObj = detailedResult.Objects[0];
                        var issues = resultObj?.Issues ?? new string[0];
                        var solutions = resultObj?.Solutions ?? new string[0];
                        
                        var message = "❌ PowerCLI is not properly configured.\n\n";
                        
                        if (issues.Length > 0)
                        {
                            message += "Issues found:\n";
                            foreach (var issue in issues)
                            {
                                message += $"• {issue}\n";
                            }
                            message += "\n";
                        }
                        
                        if (solutions.Length > 0)
                        {
                            message += "Recommended solutions:\n";
                            foreach (var solution in solutions)
                            {
                                message += $"• {solution}\n";
                            }
                        }
                        else
                        {
                            message += "Run the PowerCLI cleanup script (PowerCLI-CleanupVersions.ps1) from the application folder,\n";
                            message += "or reinstall PowerCLI:\n";
                            message += "Install-Module -Name VMware.PowerCLI -Force -AllowClobber";
                        }
                        
                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            MessageBox.Show(message, "PowerCLI Test - Issues Detected", 
                                MessageBoxButton.OK, MessageBoxImage.Warning);
                        });
                    }
                    else
                    {
                        // Fallback message
                        var message = "❌ PowerCLI modules are not available or not properly configured.\n\n" +
                                    "Try these solutions:\n" +
                                    "1. Run PowerCLI-CleanupVersions.ps1 script as Administrator\n" +
                                    "2. Or manually reinstall: Install-Module -Name VMware.PowerCLI -Force -AllowClobber";
                        
                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            MessageBox.Show(message, "PowerCLI Test - Failed", 
                                MessageBoxButton.OK, MessageBoxImage.Warning);
                        });
                    }
                }
                catch
                {
                    // Fallback if detailed analysis fails
                    var message = "❌ PowerCLI modules are not available or not properly installed.\n\n" +
                                "Please install VMware PowerCLI using:\n" +
                                "Install-Module -Name VMware.PowerCLI -Scope CurrentUser";
                    
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        MessageBox.Show(message, "PowerCLI Test - Failed", 
                            MessageBoxButton.OK, MessageBoxImage.Warning);
                    });
                }
                
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
    /// Command to run PowerCLI cleanup and reinstall
    /// </summary>
    [RelayCommand]
    private async Task RunPowerCLICleanupAsync()
    {
        try
        {
            IsLoading = true;
            _logger.LogInformation("Running PowerCLI cleanup and reinstall...");

            var confirmResult = MessageBox.Show(
                "This will:\n" +
                "1. Uninstall all VMware PowerCLI modules\n" +
                "2. Clean up any remaining files\n" +
                "3. Reinstall the latest PowerCLI\n\n" +
                "This process may take several minutes.\n\n" +
                "Do you want to continue?",
                "PowerCLI Cleanup Confirmation",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (confirmResult != MessageBoxResult.Yes)
            {
                return;
            }

            var cleanupScript = @"
                # PowerCLI Cleanup and Reinstall
                Write-Host 'Starting PowerCLI cleanup and reinstall...' -ForegroundColor Green
                
                $result = [PSCustomObject]@{
                    Success = $false
                    Steps = @()
                    Errors = @()
                    Message = ''
                }
                
                try {
                    # Step 1: Uninstall all VMware modules
                    $result.Steps += 'Uninstalling VMware modules...'
                    Write-Host 'Uninstalling VMware modules...' -ForegroundColor Yellow
                    
                    $vmwareModules = Get-InstalledModule | Where-Object { $_.Name -like '*VMware*' }
                    foreach ($module in $vmwareModules) {
                        try {
                            Uninstall-Module -Name $module.Name -AllVersions -Force -ErrorAction SilentlyContinue
                            Write-Host ""  Uninstalled: $($module.Name)"" -ForegroundColor Gray
                        } catch {
                            $result.Errors += ""Failed to uninstall $($module.Name): $($_.Exception.Message)""
                        }
                    }
                    
                    # Step 2: Clean module paths
                    $result.Steps += 'Cleaning module directories...'
                    Write-Host 'Cleaning module directories...' -ForegroundColor Yellow
                    
                    $modulePaths = $env:PSModulePath -split ';'
                    $vmwareDirectories = @()
                    foreach ($path in $modulePaths) {
                        if (Test-Path $path) {
                            $vmwareDirs = Get-ChildItem $path -Directory | Where-Object { $_.Name -like '*VMware*' }
                            $vmwareDirectories += $vmwareDirs
                        }
                    }
                    
                    foreach ($dir in $vmwareDirectories) {
                        try {
                            Remove-Item $dir.FullName -Recurse -Force -ErrorAction SilentlyContinue
                            Write-Host ""  Removed: $($dir.FullName)"" -ForegroundColor Gray
                        } catch {
                            $result.Errors += ""Failed to remove directory $($dir.FullName): $($_.Exception.Message)""
                        }
                    }
                    
                    # Step 3: Install latest PowerCLI
                    $result.Steps += 'Installing latest PowerCLI...'
                    Write-Host 'Installing latest PowerCLI...' -ForegroundColor Yellow
                    
                    # Ensure PSGallery is trusted
                    Set-PSRepository -Name PSGallery -InstallationPolicy Trusted -ErrorAction SilentlyContinue
                    
                    Install-Module -Name VMware.PowerCLI -AllowClobber -Force -SkipPublisherCheck -ErrorAction Stop
                    
                    # Step 4: Verify installation
                    $result.Steps += 'Verifying installation...'
                    Write-Host 'Verifying installation...' -ForegroundColor Yellow
                    
                    Import-Module VMware.PowerCLI -Force -ErrorAction Stop
                    $version = Get-PowerCLIVersion
                    
                    $result.Success = $true
                    $result.Message = ""PowerCLI cleanup and reinstall completed successfully! Version: $($version.PowerCLIVersion)""
                    
                    Write-Host 'PowerCLI cleanup and reinstall completed successfully!' -ForegroundColor Green
                    Write-Host ""Version: $($version.PowerCLIVersion)"" -ForegroundColor Green
                    
                } catch {
                    $result.Success = $false
                    $result.Errors += ""Installation failed: $($_.Exception.Message)""
                    $result.Message = 'PowerCLI cleanup and reinstall failed'
                    Write-Error ""Installation failed: $($_.Exception.Message)""
                }
                
                Write-Output $result
            ";

            var result = await _powerShellService.ExecuteScriptAsync(cleanupScript, timeoutSeconds: 600); // 10 minutes timeout

            if (result.IsSuccess && result.Objects.Count > 0)
            {
                dynamic? resultObj = result.Objects[0];
                bool success = resultObj?.Success ?? false;
                var steps = resultObj?.Steps ?? new string[0];
                var errors = resultObj?.Errors ?? new string[0];
                var message = resultObj?.Message ?? "Cleanup completed";

                var displayMessage = $"{message}\n\n";
                
                if (steps.Length > 0)
                {
                    displayMessage += "Steps completed:\n";
                    foreach (var step in steps)
                    {
                        displayMessage += $"✓ {step}\n";
                    }
                    displayMessage += "\n";
                }

                if (errors.Length > 0)
                {
                    displayMessage += "Warnings/Errors:\n";
                    foreach (var error in errors)
                    {
                        displayMessage += $"⚠ {error}\n";
                    }
                }

                var icon = success ? MessageBoxImage.Information : MessageBoxImage.Warning;
                var title = success ? "PowerCLI Cleanup - Success" : "PowerCLI Cleanup - Completed with Warnings";

                Application.Current.Dispatcher.Invoke(() =>
                {
                    MessageBox.Show(displayMessage, title, MessageBoxButton.OK, icon);
                });

                _logger.LogInformation("PowerCLI cleanup completed: Success={Success}", success);
            }
            else
            {
                var errorMessage = "❌ PowerCLI cleanup failed.\n\n";
                if (!string.IsNullOrEmpty(result.ErrorOutput))
                {
                    errorMessage += $"Error: {result.ErrorOutput}\n\n";
                }
                errorMessage += "You may need to run the cleanup manually as Administrator.";

                Application.Current.Dispatcher.Invoke(() =>
                {
                    MessageBox.Show(errorMessage, "PowerCLI Cleanup - Failed", 
                        MessageBoxButton.OK, MessageBoxImage.Error);
                });

                _logger.LogError("PowerCLI cleanup failed: {Error}", result.ErrorOutput);
            }
        }
        catch (Exception ex)
        {
            var message = $"❌ Failed to run PowerCLI cleanup:\n\n{ex.Message}";
            
            Application.Current.Dispatcher.Invoke(() =>
            {
                MessageBox.Show(message, "PowerCLI Cleanup - Error", 
                    MessageBoxButton.OK, MessageBoxImage.Error);
            });
            
            _logger.LogError(ex, "Failed to run PowerCLI cleanup");
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Command to run comprehensive PowerShell diagnostics
    /// </summary>
    [RelayCommand]
    private async Task RunPowerShellDiagnosticsAsync()
    {
        try
        {
            IsLoading = true;
            _logger.LogInformation("Running PowerShell diagnostics...");

            var diagnosticScript = @"
                try {
                    $result = [PSCustomObject]@{
                        PowerShellVersion = $PSVersionTable.PSVersion.ToString()
                        ExecutionPolicies = @{}
                        ModulePaths = $env:PSModulePath -split ';'
                        VMwareModules = @()
                        PowerCLIInstalled = $false
                        IsAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole] 'Administrator')
                        Errors = @()
                    }

                    # Get execution policies for all scopes
                    @('Process', 'CurrentUser', 'LocalMachine', 'MachinePolicy', 'UserPolicy') | ForEach-Object {
                        try {
                            $result.ExecutionPolicies[$_] = Get-ExecutionPolicy -Scope $_ -ErrorAction SilentlyContinue
                        } catch {
                            $result.ExecutionPolicies[$_] = 'Error: ' + $_.Exception.Message
                        }
                    }

                    # Check for VMware modules
                    try {
                        $vmwareModules = Get-Module -ListAvailable | Where-Object { $_.Name -like '*VMware*' }
                        $result.VMwareModules = $vmwareModules | Select-Object Name, Version, Path
                        $result.PowerCLIInstalled = ($vmwareModules | Where-Object { $_.Name -eq 'VMware.VimAutomation.Core' }) -ne $null
                    } catch {
                        $result.Errors += 'Failed to check VMware modules: ' + $_.Exception.Message
                    }

                    # Try to load PowerCLI core and get version
                    if ($result.PowerCLIInstalled) {
                        try {
                            Import-Module VMware.VimAutomation.Core -ErrorAction Stop
                            $powerCLIVersion = Get-PowerCLIVersion -ErrorAction Stop
                            $result.PowerCLIVersion = $powerCLIVersion.ProductLine
                        } catch {
                            $result.Errors += 'Failed to load PowerCLI: ' + $_.Exception.Message
                        }
                    }

                    return $result
                } catch {
                    return [PSCustomObject]@{
                        Error = $_.Exception.Message
                        PowerShellVersion = $PSVersionTable.PSVersion.ToString()
                        ExecutionPolicies = @{}
                        ModulePaths = @()
                        VMwareModules = @()
                        PowerCLIInstalled = $false
                        IsAdmin = $false
                        Errors = @($_.Exception.Message)
                    }
                }
            ";

            var psResult = await _powerShellService.ExecuteScriptAsync(diagnosticScript, timeoutSeconds: 60);
            
            var message = new StringBuilder();
            message.AppendLine("PowerShell Diagnostic Results:");
            message.AppendLine("=" + new string('=', 50));
            
            if (psResult.IsSuccess && psResult.Objects.Count > 0)
            {
                if (psResult.Objects[0] is PSObject diagObj)
                {
                    message.AppendLine($"PowerShell Version: {diagObj.Properties["PowerShellVersion"]?.Value}");
                    message.AppendLine($"Running as Administrator: {diagObj.Properties["IsAdmin"]?.Value}");
                    message.AppendLine($"PowerCLI Installed: {diagObj.Properties["PowerCLIInstalled"]?.Value}");
                    
                    var powerCLIVersion = diagObj.Properties["PowerCLIVersion"]?.Value?.ToString();
                    if (!string.IsNullOrEmpty(powerCLIVersion))
                    {
                        message.AppendLine($"PowerCLI Version: {powerCLIVersion}");
                    }
                    

                    
                    message.AppendLine();
                    message.AppendLine("Execution Policies:");
                    message.AppendLine("-" + new string('-', 30));
                    
                    var policies = diagObj.Properties["ExecutionPolicies"]?.Value;
                    if (policies is PSObject policiesObj)
                    {
                        foreach (var policy in policiesObj.Properties)
                        {
                            message.AppendLine($"  {policy.Name}: {policy.Value}");
                        }
                    }
                    
                    message.AppendLine();
                    message.AppendLine("Module Paths:");
                    message.AppendLine("-" + new string('-', 30));
                    
                    var modulePaths = diagObj.Properties["ModulePaths"]?.Value as object[];
                    if (modulePaths != null)
                    {
                        foreach (var path in modulePaths)
                        {
                            message.AppendLine($"  {path}");
                        }
                    }
                    
                    message.AppendLine();
                    message.AppendLine("VMware Modules Found:");
                    message.AppendLine("-" + new string('-', 30));
                    
                    var vmwareModules = diagObj.Properties["VMwareModules"]?.Value as object[];
                    if (vmwareModules != null && vmwareModules.Length > 0)
                    {
                        foreach (var module in vmwareModules)
                        {
                            if (module is PSObject moduleObj)
                            {
                                var name = moduleObj.Properties["Name"]?.Value;
                                var version = moduleObj.Properties["Version"]?.Value;
                                var path = moduleObj.Properties["Path"]?.Value;
                                message.AppendLine($"  {name} (v{version})");
                                message.AppendLine($"    Path: {path}");
                            }
                        }
                    }
                    else
                    {
                        message.AppendLine("  ❌ No VMware modules found!");
                        message.AppendLine("  Install with: Install-Module -Name VMware.PowerCLI -AllowClobber");
                    }
                    
                    var errors = diagObj.Properties["Errors"]?.Value as object[];
                    if (errors != null && errors.Length > 0)
                    {
                        message.AppendLine();
                        message.AppendLine("❌ Errors:");
                        message.AppendLine("-" + new string('-', 30));
                        foreach (var error in errors)
                        {
                            message.AppendLine($"  {error}");
                        }
                    }
                }
            }
            else
            {
                message.AppendLine("❌ Failed to run diagnostics:");
                message.AppendLine(psResult.ErrorOutput);
            }
            
            message.AppendLine();
            message.AppendLine("🔧 Solutions for Common Issues:");
            message.AppendLine("-" + new string('-', 30));
            message.AppendLine("FOR VERSION CONFLICTS (like your current issue):");
            message.AppendLine("1. Clean reinstall PowerCLI (recommended for version conflicts):");
            message.AppendLine("   # Run as Administrator:");
            message.AppendLine("   Uninstall-Module VMware.PowerCLI -AllVersions -Force");
            message.AppendLine("   Uninstall-Module VMware.Vim -AllVersions -Force -ErrorAction SilentlyContinue");
            message.AppendLine("   Install-Module VMware.PowerCLI -AllowClobber -Force");
            message.AppendLine();
            message.AppendLine("2. Or load specific compatible versions:");
            message.AppendLine("   Import-Module VMware.VimAutomation.Common -RequiredVersion 13.2.0");
            message.AppendLine("   Import-Module VMware.VimAutomation.Core -RequiredVersion 13.2.0");
            message.AppendLine();
            message.AppendLine("FOR EXECUTION POLICY ISSUES:");
            message.AppendLine("3. Set execution policy (as Administrator):");
            message.AppendLine("   Set-ExecutionPolicy -ExecutionPolicy RemoteSigned -Scope LocalMachine");
            message.AppendLine();
            message.AppendLine("4. Set execution policy (current user only):");
            message.AppendLine("   Set-ExecutionPolicy -ExecutionPolicy RemoteSigned -Scope CurrentUser");
            message.AppendLine();
            message.AppendLine("FOR FRESH INSTALLATIONS:");
            message.AppendLine("5. Install PowerCLI:");
            message.AppendLine("   Install-Module -Name VMware.PowerCLI -AllowClobber");
            message.AppendLine();
            message.AppendLine("6. Trust PowerShell Gallery (if needed):");
            message.AppendLine("   Set-PSRepository -Name PSGallery -InstallationPolicy Trusted");

            // Show results in a dialog
            Application.Current.Dispatcher.Invoke(() =>
            {
                var diagWindow = new Window
                {
                    Title = "PowerShell Diagnostics",
                    Width = 900,
                    Height = 700,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    Owner = Application.Current.MainWindow
                };

                var textBox = new TextBox
                {
                    Text = message.ToString(),
                    IsReadOnly = true,
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                    Margin = new Thickness(10)
                };

                var copyButton = new Button
                {
                    Content = "📋 Copy to Clipboard",
                    Margin = new Thickness(10, 0, 10, 10),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Bottom,
                    Padding = new Thickness(15, 5, 15, 5)
                };

                copyButton.Click += (s, e) =>
                {
                    try
                    {
                        Clipboard.SetText(message.ToString());
                        MessageBox.Show("✅ Diagnostic information copied to clipboard!", "Copied", 
                            MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"❌ Failed to copy to clipboard: {ex.Message}", "Error", 
                            MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                };

                var grid = new Grid();
                grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                Grid.SetRow(textBox, 0);
                Grid.SetRow(copyButton, 1);

                grid.Children.Add(textBox);
                grid.Children.Add(copyButton);

                diagWindow.Content = grid;
                diagWindow.ShowDialog();
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PowerShell diagnostics failed");
            Application.Current.Dispatcher.Invoke(() =>
            {
                MessageBox.Show($"❌ PowerShell diagnostics failed: {ex.Message}", "Error", 
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