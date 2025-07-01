using System.Collections.ObjectModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using VMwareGUITools.Core.Models;
using VMwareGUITools.Data;
using VMwareGUITools.Infrastructure.Services;
using VMwareGUITools.Infrastructure.VMware;

namespace VMwareGUITools.UI.ViewModels;

/// <summary>
/// View model for editing an existing vCenter server
/// </summary>
public partial class EditVCenterViewModel : ObservableValidator
{
    private readonly ILogger<EditVCenterViewModel> _logger;
    private readonly VMwareDbContext _context;
    private readonly IServiceConfigurationManager _serviceConfigurationManager;
    private VCenter? _originalVCenter;

    [ObservableProperty]
    [Required(ErrorMessage = "Display name is required")]
    [StringLength(100, ErrorMessage = "Display name must be less than 100 characters")]
    private string _name = string.Empty;

    [ObservableProperty]
    [Required(ErrorMessage = "vCenter URL is required")]
    [Url(ErrorMessage = "Please enter a valid URL")]
    private string _url = string.Empty;

    [ObservableProperty]
    [Required(ErrorMessage = "Username is required")]
    private string _username = string.Empty;

    [ObservableProperty]
    [Required(ErrorMessage = "Password is required")]
    private string _password = string.Empty;

    [ObservableProperty]
    private bool _enableAutoDiscovery = true;

    [ObservableProperty]
    private bool _testOnSave = false;

    [ObservableProperty]
    private bool _isTesting = false;

    [ObservableProperty]
    private bool _isSaving = false;

    [ObservableProperty]
    private VCenterConnectionResult? _testResult;

    [ObservableProperty]
    private bool _showTestResult = false;

    [ObservableProperty]
    private ObservableCollection<AvailabilityZone> _availabilityZones = new();

    [ObservableProperty]
    private AvailabilityZone? _selectedAvailabilityZone;

    public EditVCenterViewModel(
        ILogger<EditVCenterViewModel> logger,
        VMwareDbContext context,
        IServiceConfigurationManager serviceConfigurationManager)
    {
        _logger = logger;
        _context = context;
        _serviceConfigurationManager = serviceConfigurationManager;
    }

    /// <summary>
    /// Event raised when dialog result should be set
    /// </summary>
    public event Action<bool>? DialogResultRequested;

    /// <summary>
    /// Gets whether connection can be tested
    /// </summary>
    public bool CanTestConnection => !string.IsNullOrWhiteSpace(Url) && 
                                    !string.IsNullOrWhiteSpace(Username) && 
                                    !string.IsNullOrWhiteSpace(Password) && 
                                    !IsTesting;

    /// <summary>
    /// Gets whether the form can be saved
    /// </summary>
    public bool CanSave => !string.IsNullOrWhiteSpace(Name) &&
                          !string.IsNullOrWhiteSpace(Url) &&
                          !string.IsNullOrWhiteSpace(Username) &&
                          !string.IsNullOrWhiteSpace(Password) &&
                          !IsSaving &&
                          !IsTesting &&
                          (!TestOnSave || (TestResult?.IsSuccessful == true));

    /// <summary>
    /// Gets the test result message to display
    /// </summary>
    public string TestResultMessage
    {
        get
        {
            if (TestResult == null) return string.Empty;

            if (TestResult.IsSuccessful)
            {
                var versionInfo = TestResult.VersionInfo;
                if (versionInfo != null)
                {
                    return $"Connected to {versionInfo.ProductName} version {versionInfo.Version} (Build {versionInfo.Build})";
                }
                return $"Connection successful (Response time: {TestResult.ResponseTime.TotalMilliseconds:F0}ms)";
            }

            return TestResult.ErrorMessage ?? "Connection failed";
        }
    }

    /// <summary>
    /// Initialize the view model with existing vCenter data
    /// </summary>
    public async Task InitializeAsync(VCenter vCenter)
    {
        _originalVCenter = vCenter;
        
        Name = vCenter.Name;
        Url = vCenter.Url;
        EnableAutoDiscovery = true; // Default value
        
        // Note: We can't decrypt credentials in the GUI anymore (service-only)
        // User will need to re-enter credentials when editing
        Username = string.Empty;
        Password = string.Empty;
        
        // Load availability zones and set current selection
        await LoadAvailabilityZonesAsync();
        SelectedAvailabilityZone = AvailabilityZones.FirstOrDefault(az => az.Id == vCenter.AvailabilityZoneId);
    }

    /// <summary>
    /// Command to test the connection
    /// </summary>
    [RelayCommand]
    private async Task TestConnectionAsync()
    {
        try
        {
            IsTesting = true;
            ShowTestResult = false;

            _logger.LogInformation("Testing connection to vCenter: {Url} via service", Url);

            // Check if service is running
            var serviceStatus = await _serviceConfigurationManager.GetServiceStatusAsync();
            if (serviceStatus == null || serviceStatus.LastHeartbeat < DateTime.UtcNow.AddSeconds(-30))
            {
                TestResult = new VCenterConnectionResult
                {
                    IsSuccessful = false,
                    ErrorMessage = "Windows Service is not running. Cannot test connection.",
                    ResponseTime = TimeSpan.Zero
                };
                ShowTestResult = true;
                return;
            }

            // Send test command to service
            var parameters = new 
            { 
                Url = Url, 
                Username = Username, 
                Password = Password 
            };

            var commandId = await _serviceConfigurationManager.SendCommandAsync(
                ServiceCommandTypes.TestVCenterConnectionWithCredentials, 
                parameters);

            // Monitor for completion
            var result = await MonitorCommandCompletionAsync(commandId, "Connection test");
            
            if (result != null)
            {
                var testResultData = JsonSerializer.Deserialize<Dictionary<string, object>>(result);
                if (testResultData != null)
                {
                    // Handle JsonElement objects properly
                    var isSuccessful = false;
                    if (testResultData.TryGetValue("IsSuccessful", out var successObj))
                    {
                        isSuccessful = successObj switch
                        {
                            JsonElement jsonElement => jsonElement.GetBoolean(),
                            bool boolValue => boolValue,
                            _ => Convert.ToBoolean(successObj)
                        };
                    }

                    var responseTime = TimeSpan.Zero;
                    if (testResultData.TryGetValue("ResponseTime", out var timeObj))
                    {
                        var responseTimeMs = timeObj switch
                        {
                            JsonElement jsonElement => jsonElement.GetDouble(),
                            double doubleValue => doubleValue,
                            _ => Convert.ToDouble(timeObj)
                        };
                        responseTime = TimeSpan.FromMilliseconds(responseTimeMs);
                    }

                    var errorMessage = testResultData.TryGetValue("ErrorMessage", out var errorObj) 
                        ? errorObj?.ToString() : null;

                    TestResult = new VCenterConnectionResult
                    {
                        IsSuccessful = isSuccessful,
                        ErrorMessage = errorMessage,
                        ResponseTime = responseTime
                    };

                    // Parse version info if available
                    if (testResultData.TryGetValue("VersionInfo", out var versionObj) && versionObj != null)
                    {
                        try
                        {
                            string? versionJson = null;
                            if (versionObj is JsonElement versionElement)
                            {
                                versionJson = versionElement.GetRawText();
                            }
                            else
                            {
                                versionJson = versionObj.ToString();
                            }

                            if (!string.IsNullOrEmpty(versionJson))
                            {
                                TestResult.VersionInfo = JsonSerializer.Deserialize<VCenterVersionInfo>(versionJson);
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to parse version info from test result");
                        }
                    }
                }
                else
                {
                    TestResult = new VCenterConnectionResult
                    {
                        IsSuccessful = false,
                        ErrorMessage = "Invalid response from service",
                        ResponseTime = TimeSpan.Zero
                    };
                }
            }
            else
            {
                TestResult = new VCenterConnectionResult
                {
                    IsSuccessful = false,
                    ErrorMessage = "Connection test timed out or failed",
                    ResponseTime = TimeSpan.Zero
                };
            }

            ShowTestResult = true;
            _logger.LogInformation("Connection test completed via service. Success: {Success}", TestResult.IsSuccessful);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to test connection via service");
            TestResult = new VCenterConnectionResult
            {
                IsSuccessful = false,
                ErrorMessage = $"Test failed: {ex.Message}",
                ResponseTime = TimeSpan.Zero
            };
            ShowTestResult = true;
        }
        finally
        {
            IsTesting = false;
            OnPropertyChanged(nameof(CanSave));
        }
    }

    /// <summary>
    /// Command to save the vCenter changes
    /// </summary>
    [RelayCommand]
    private async Task SaveAsync()
    {
        try
        {
            if (!ValidateForm() || _originalVCenter == null) return;

            IsSaving = true;

            _logger.LogInformation("Updating vCenter: {Name} ({Url}) via service", Name, Url);

            // Test connection if required
            if (TestOnSave && (TestResult == null || !TestResult.IsSuccessful))
            {
                await TestConnectionAsync();
                if (TestResult?.IsSuccessful != true)
                {
                    _logger.LogWarning("Connection test failed, cannot save vCenter");
                    return;
                }
            }

            // Check if service is running
            var serviceStatus = await _serviceConfigurationManager.GetServiceStatusAsync();
            if (serviceStatus == null || serviceStatus.LastHeartbeat < DateTime.UtcNow.AddSeconds(-30))
            {
                _logger.LogWarning("Windows Service is not running. Cannot save vCenter.");
                // TODO: Show error message to user
                return;
            }

            // Send edit vCenter command to service
            var parameters = new Dictionary<string, object>
            {
                { "VCenterId", _originalVCenter.Id },
                { "Name", Name.Trim() },
                { "Url", Url.Trim() },
                { "AvailabilityZoneId", SelectedAvailabilityZone?.Id ?? 0 },
                { "EnableAutoDiscovery", EnableAutoDiscovery }
            };

            // Only include credentials if they were entered
            if (!string.IsNullOrEmpty(Username) && !string.IsNullOrEmpty(Password))
            {
                parameters.Add("Username", Username);
                parameters.Add("Password", Password);
            }

            var commandId = await _serviceConfigurationManager.SendCommandAsync(
                ServiceCommandTypes.EditVCenter, 
                parameters);

            // Monitor for completion
            var result = await MonitorCommandCompletionAsync(commandId, "Edit vCenter");
            
            if (result != null)
            {
                var resultData = JsonSerializer.Deserialize<Dictionary<string, object>>(result);
                if (resultData != null && resultData.TryGetValue("Success", out var successObj))
                {
                    var isSuccess = successObj switch
                    {
                        JsonElement jsonElement => jsonElement.GetBoolean(),
                        bool boolValue => boolValue,
                        _ => Convert.ToBoolean(successObj)
                    };

                    if (isSuccess)
                    {
                        _logger.LogInformation("Successfully updated vCenter via service: {Name}", Name);
                        // Close dialog with success
                        DialogResultRequested?.Invoke(true);
                    }
                    else
                    {
                        var message = resultData.TryGetValue("Message", out var msgObj) ? msgObj.ToString() : "Unknown error";
                        _logger.LogError("Failed to update vCenter via service: {Message}", message);
                        // TODO: Show error message to user
                    }
                }
                else
                {
                    _logger.LogError("Invalid response format from service");
                    // TODO: Show error message to user
                }
            }
            else
            {
                _logger.LogError("Edit vCenter command timed out or failed");
                // TODO: Show error message to user
            }

        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update vCenter: {Name}", Name);
            // TODO: Show error message to user
        }
        finally
        {
            IsSaving = false;
        }
    }

    /// <summary>
    /// Command to delete the vCenter
    /// </summary>
    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (_originalVCenter == null) return;

        try
        {
            _logger.LogInformation("Requesting to delete vCenter: {VCenterName}", _originalVCenter.Name);

            // Show confirmation dialog
            var result = System.Windows.MessageBox.Show(
                $"Are you sure you want to delete vCenter '{_originalVCenter.DisplayName}'?\n\n" +
                "This action will permanently remove:\n" +
                "• The vCenter connection\n" +
                "• All collected cluster data\n" +
                "• All collected host data\n" +
                "• All collected datastore data\n" +
                "• All historical check results\n\n" +
                "This action cannot be undone.",
                "Confirm Delete vCenter",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Warning,
                System.Windows.MessageBoxResult.No);

            if (result == System.Windows.MessageBoxResult.Yes)
            {
                IsSaving = true;

                _logger.LogInformation("User confirmed deletion of vCenter: {VCenterName}", _originalVCenter.Name);

                // Remove the vCenter - EF Core will handle cascade deletes for related data
                _context.VCenters.Remove(_originalVCenter);
                await _context.SaveChangesAsync();

                _logger.LogInformation("Successfully deleted vCenter: {VCenterName}", _originalVCenter.Name);

                // Close dialog with success (indicating the vCenter was deleted)
                DialogResultRequested?.Invoke(true);
            }
            else
            {
                _logger.LogInformation("User cancelled deletion of vCenter: {VCenterName}", _originalVCenter.Name);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete vCenter: {VCenterName}", _originalVCenter?.Name ?? "Unknown");
            // TODO: Show error message to user
        }
        finally
        {
            IsSaving = false;
        }
    }

    /// <summary>
    /// Command to cancel the dialog
    /// </summary>
    [RelayCommand]
    private void Cancel()
    {
        _logger.LogDebug("Edit vCenter dialog cancelled");
        DialogResultRequested?.Invoke(false);
    }

    /// <summary>
    /// Validates the form data
    /// </summary>
    private bool ValidateForm()
    {
        var validationContext = new ValidationContext(this);
        var validationResults = new List<ValidationResult>();
        var isValid = Validator.TryValidateObject(this, validationContext, validationResults, true);

        if (!isValid)
        {
            var errors = string.Join(", ", validationResults.Select(r => r.ErrorMessage));
            _logger.LogWarning("Form validation failed: {Errors}", errors);
            // TODO: Display validation errors to user
        }

        return isValid;
    }

    /// <summary>
    /// Update computed properties when form fields change
    /// </summary>
    partial void OnNameChanged(string value) => OnPropertyChanged(nameof(CanSave));
    partial void OnUrlChanged(string value)
    {
        OnPropertyChanged(nameof(CanTestConnection));
        OnPropertyChanged(nameof(CanSave));
        ShowTestResult = false; // Hide previous test results when URL changes
    }
    partial void OnUsernameChanged(string value)
    {
        OnPropertyChanged(nameof(CanTestConnection));
        OnPropertyChanged(nameof(CanSave));
        ShowTestResult = false;
    }
    partial void OnPasswordChanged(string value)
    {
        OnPropertyChanged(nameof(CanTestConnection));
        OnPropertyChanged(nameof(CanSave));
        ShowTestResult = false;
    }
    partial void OnTestOnSaveChanged(bool value) => OnPropertyChanged(nameof(CanSave));
    partial void OnTestResultChanged(VCenterConnectionResult? value)
    {
        OnPropertyChanged(nameof(TestResultMessage));
        OnPropertyChanged(nameof(CanSave));
    }

    /// <summary>
    /// Loads all availability zones for selection
    /// </summary>
    private async Task LoadAvailabilityZonesAsync()
    {
        try
        {
            var zones = await _context.AvailabilityZones
                .OrderBy(az => az.SortOrder)
                .ThenBy(az => az.Name)
                .ToListAsync();

            AvailabilityZones.Clear();
            foreach (var zone in zones)
            {
                AvailabilityZones.Add(zone);
            }

            _logger.LogDebug("Loaded {Count} availability zones for vCenter editing", zones.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load availability zones");
        }
    }

    /// <summary>
    /// Monitors a service command for completion and returns the result
    /// </summary>
    private async Task<string?> MonitorCommandCompletionAsync(string commandId, string operationName)
    {
        const int maxAttempts = 30; // 30 seconds timeout
        const int delayMs = 1000; // 1 second delay between checks

        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            try
            {
                var command = await _serviceConfigurationManager.GetCommandResultAsync(commandId);
                if (command != null)
                {
                    switch (command.Status)
                    {
                        case "Completed":
                            _logger.LogInformation("{OperationName} completed successfully", operationName);
                            return command.Result;
                        
                        case "Failed":
                            _logger.LogError("{OperationName} failed: {Error}", operationName, command.ErrorMessage);
                            return null;
                        
                        case "Processing":
                        case "Pending":
                            // Continue monitoring
                            break;
                    }
                }

                await Task.Delay(delayMs);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error monitoring {OperationName} command", operationName);
                return null;
            }
        }

        _logger.LogWarning("{OperationName} command timed out after {Timeout} seconds", operationName, maxAttempts);
        return null;
    }
} 