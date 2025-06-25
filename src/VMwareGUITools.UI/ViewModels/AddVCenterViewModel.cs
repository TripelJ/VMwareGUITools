using System.Collections.ObjectModel;
using System.ComponentModel.DataAnnotations;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using VMwareGUITools.Core.Models;
using VMwareGUITools.Data;
using VMwareGUITools.Infrastructure.Security;
using VMwareGUITools.Infrastructure.VMware;

namespace VMwareGUITools.UI.ViewModels;

/// <summary>
/// View model for adding a new vCenter server
/// </summary>
public partial class AddVCenterViewModel : ObservableValidator
{
    private readonly ILogger<AddVCenterViewModel> _logger;
    private readonly VMwareDbContext _context;
    private readonly ICredentialService _credentialService;
    private readonly IVMwareConnectionService _vmwareService;

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
    private bool _testOnSave = true;

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

    public AddVCenterViewModel(
        ILogger<AddVCenterViewModel> logger,
        VMwareDbContext context,
        ICredentialService credentialService,
        IVMwareConnectionService vmwareService)
    {
        _logger = logger;
        _context = context;
        _credentialService = credentialService;
        _vmwareService = vmwareService;
        
        _ = LoadAvailabilityZonesAsync();
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
    /// Command to test the connection
    /// </summary>
    [RelayCommand]
    private async Task TestConnectionAsync()
    {
        try
        {
            IsTesting = true;
            ShowTestResult = false;

            _logger.LogInformation("Testing connection to vCenter: {Url}", Url);

            TestResult = await _vmwareService.TestConnectionAsync(Url, Username, Password);
            ShowTestResult = true;

            _logger.LogInformation("Connection test completed. Success: {Success}", TestResult.IsSuccessful);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to test connection");
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
    /// Command to save the vCenter
    /// </summary>
    [RelayCommand]
    private async Task SaveAsync()
    {
        try
        {
            if (!ValidateForm()) return;

            IsSaving = true;

            _logger.LogInformation("Saving new vCenter: {Name} ({Url})", Name, Url);

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

            // Check for duplicate URLs
            var existingVCenter = await _context.VCenters
                .FirstOrDefaultAsync(v => v.Url.ToLower() == Url.ToLower());

            if (existingVCenter != null)
            {
                _logger.LogWarning("vCenter with URL {Url} already exists", Url);
                // TODO: Show error message to user
                return;
            }

            // Encrypt credentials
            var encryptedCredentials = _credentialService.EncryptCredentials(Username, Password);

            // Create new vCenter entity
            var vCenter = new VCenter
            {
                Name = Name.Trim(),
                Url = Url.Trim(),
                EncryptedCredentials = encryptedCredentials,
                Enabled = true,
                AvailabilityZoneId = SelectedAvailabilityZone?.Id,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                LastScan = TestResult?.IsSuccessful == true ? DateTime.UtcNow : null
            };

            _context.VCenters.Add(vCenter);
            await _context.SaveChangesAsync();

            _logger.LogInformation("Successfully saved vCenter: {Name} with ID: {Id}", Name, vCenter.Id);

            // Close dialog with success
            DialogResultRequested?.Invoke(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save vCenter: {Name}", Name);
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
        _logger.LogDebug("Add vCenter dialog cancelled");
        DialogResultRequested?.Invoke(false);
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

            _logger.LogDebug("Loaded {Count} availability zones for vCenter selection", zones.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load availability zones");
        }
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
} 