using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using VMwareGUITools.Core.Models;
using VMwareGUITools.Data;
using VMwareGUITools.Infrastructure.Security;
using VMwareGUITools.Infrastructure.Checks;
using VMwareGUITools.UI.Views;
using VMwareGUITools.Infrastructure.Services;
using VMwareGUITools.Infrastructure.VMware;
using System.Text.Json;

namespace VMwareGUITools.UI.ViewModels;

/// <summary>
/// Main window view model handling the primary application interface
/// </summary>
public partial class MainWindowViewModel : ObservableObject
{
    private readonly ILogger<MainWindowViewModel> _logger;
    private readonly VMwareDbContext _context;
    private readonly IServiceProvider _serviceProvider;
    private readonly IServiceConfigurationManager _serviceConfigurationManager;
    private readonly IVMwareConnectionService _vmwareService;
    private readonly System.Timers.Timer _clockTimer;
    private readonly System.Timers.Timer _connectionMonitorTimer;
    private readonly System.Timers.Timer _serviceMonitorTimer;

    [ObservableProperty]
    private ObservableCollection<VCenter> _vCenters = new();

    [ObservableProperty]
    private ObservableCollection<AvailabilityZone> _availabilityZones = new();

    [ObservableProperty]
    private VCenter? _selectedVCenter;

    [ObservableProperty]
    private AvailabilityZoneViewModel _availabilityZoneViewModel;

    [ObservableProperty]
    private string _statusMessage = "Ready";

    [ObservableProperty]
    private DateTime _currentTime = DateTime.Now;

    [ObservableProperty]
    private bool _isPowerCLIAvailable = false;

    [ObservableProperty]
    private bool _isDatabaseConnected = false;

    [ObservableProperty]
    private bool _isServiceRunning = false;

    [ObservableProperty]
    private string _serviceStatus = "Unknown";

    [ObservableProperty]
    private DateTime _serviceLastHeartbeat = DateTime.MinValue;

    [ObservableProperty]
    private int _serviceActiveExecutions = 0;

    [ObservableProperty]
    private bool _isLoading = false;

    [ObservableProperty]
    private VCenterOverview? _overviewData;

    [ObservableProperty]
    private InfrastructureViewModel _infrastructureViewModel;

    [ObservableProperty]
    private VCenterOverviewViewModel _vCenterOverviewViewModel;

    [ObservableProperty]
    private CheckResultsViewModel _checkResultsViewModel;

    public MainWindowViewModel(
        ILogger<MainWindowViewModel> logger,
        VMwareDbContext context,
        IServiceProvider serviceProvider,
        IServiceConfigurationManager serviceConfigurationManager,
        IVMwareConnectionService vmwareService)
    {
        _logger = logger;
        _context = context;
        _serviceProvider = serviceProvider;
        _serviceConfigurationManager = serviceConfigurationManager;
        _vmwareService = vmwareService;

        // Initialize view models
        _infrastructureViewModel = new InfrastructureViewModel(
            _serviceProvider.GetRequiredService<ILogger<InfrastructureViewModel>>(),
            serviceConfigurationManager);
        
        _vCenterOverviewViewModel = new VCenterOverviewViewModel(
            _serviceProvider.GetRequiredService<ILogger<VCenterOverviewViewModel>>(),
            serviceConfigurationManager);

        _checkResultsViewModel = new CheckResultsViewModel(
            _serviceProvider.GetRequiredService<ILogger<CheckResultsViewModel>>(),
            context,
            serviceConfigurationManager);

        _availabilityZoneViewModel = new AvailabilityZoneViewModel(
            _serviceProvider.GetRequiredService<ILogger<AvailabilityZoneViewModel>>(),
            context);

        // Subscribe to availability zone events
        AvailabilityZoneViewModel.EditAvailabilityZoneRequested += OnEditAvailabilityZoneRequested;
        AvailabilityZoneViewModel.AvailabilityZonesChanged += OnAvailabilityZonesChanged;

        // Setup clock timer
        _clockTimer = new System.Timers.Timer(1000);
        _clockTimer.Elapsed += (s, e) => CurrentTime = DateTime.Now;
        _clockTimer.Start();

        // Setup connection monitoring timer - check every 30 seconds
        _connectionMonitorTimer = new System.Timers.Timer(30000);
        _connectionMonitorTimer.Elapsed += async (s, e) => await MonitorConnectionsAsync();
        _connectionMonitorTimer.Start();

        // Setup service monitoring timer - check every 10 seconds
        _serviceMonitorTimer = new System.Timers.Timer(10000);
        _serviceMonitorTimer.Elapsed += async (s, e) => await MonitorServiceStatusAsync();
        _serviceMonitorTimer.Start();

        // Initialize data
        _ = InitializeAsync();
    }

    /// <summary>
    /// Gets whether to show the welcome screen
    /// </summary>
    public bool ShowWelcomeScreen => !VCenters.Any() || SelectedVCenter == null;

    /// <summary>
    /// Gets whether to show the content area
    /// </summary>
    public bool ShowContentArea => !ShowWelcomeScreen;

    /// <summary>
    /// Command to add a new vCenter server
    /// </summary>
    [RelayCommand]
    private async Task AddVCenterAsync()
    {
        try
        {
            _logger.LogInformation("Opening Add vCenter dialog");

            var addVCenterWindow = _serviceProvider.GetRequiredService<AddVCenterWindow>();
            
            // Show dialog and await result
            var result = addVCenterWindow.ShowDialog();

            if (result == true)
            {
                _logger.LogInformation("Add vCenter dialog completed successfully");
                await LoadVCentersAsync();
                StatusMessage = "vCenter server added successfully";
            }
            else
            {
                _logger.LogInformation("Add vCenter dialog was cancelled or closed");
                StatusMessage = "Add vCenter operation cancelled";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open Add vCenter dialog");
            StatusMessage = $"Failed to add vCenter server: {ex.Message}";
        }
    }

    /// <summary>
    /// Command to connect to a vCenter server via Windows Service
    /// </summary>
    [RelayCommand]
    private async Task ConnectVCenterAsync(VCenter? vCenter)
    {
        if (vCenter == null) return;

        try
        {
            IsLoading = true;
            StatusMessage = $"Connecting to {vCenter.DisplayName} via service...";

            _logger.LogInformation("Attempting to connect to vCenter via service: {VCenterName}", vCenter.Name);

            // Check if service is running
            var serviceStatus = await _serviceConfigurationManager.GetServiceStatusAsync();
            if (serviceStatus == null || serviceStatus.LastHeartbeat < DateTime.UtcNow.AddSeconds(-30))
            {
                StatusMessage = "Windows Service is not running. Cannot connect to vCenter.";
                return;
            }

            // Send connection command to service
            var parameters = new { VCenterId = vCenter.Id, Action = "Connect" };
            var commandId = await _serviceConfigurationManager.SendCommandAsync("ConnectVCenter", parameters);

            StatusMessage = $"Connection request sent to service (ID: {commandId}). Processing...";

            // Monitor for completion
            var result = await MonitorCommandCompletionAsync(commandId, "vCenter connection");
            
            if (result != null)
            {
                // Update connection status in UI
                vCenter.UpdateConnectionStatus(true);
                vCenter.LastScan = DateTime.UtcNow;
                await _context.SaveChangesAsync();

                StatusMessage = $"Connected to {vCenter.DisplayName} via service";
                
                // Set as selected vCenter
                SelectedVCenter = VCenters.FirstOrDefault(v => v.Id == vCenter.Id);
                
                _logger.LogInformation("Successfully connected to vCenter via service: {VCenterName}", vCenter.Name);
            }
        }
        catch (Exception ex)
        {
            vCenter.UpdateConnectionStatus(false);
            _logger.LogError(ex, "Failed to connect to vCenter via service: {VCenterName}", vCenter.Name);
            
            // Handle credential decryption errors specifically
            if (await HandleCredentialDecryptionErrorAsync(vCenter, ex, "connect to"))
            {
                return; // Error was handled, don't show generic message
            }
            
            StatusMessage = $"Failed to connect to {vCenter.DisplayName}: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Command to refresh all data
    /// </summary>
    [RelayCommand]
    private async Task RefreshAsync()
    {
        try
        {
            IsLoading = true;
            StatusMessage = "Refreshing data...";

            await LoadVCentersAsync();
            await CheckPowerCLIAsync();
            await CheckDatabaseConnectivityAsync();

            StatusMessage = "Data refreshed successfully";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to refresh data");
            StatusMessage = $"Failed to refresh data: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Command to edit a vCenter server
    /// </summary>
    [RelayCommand]
    private async Task EditVCenterAsync(VCenter? vCenter)
    {
        if (vCenter == null) 
        {
            _logger.LogWarning("EditVCenterAsync called with null vCenter parameter");
            return;
        }

        try
        {
            _logger.LogInformation("Opening Edit vCenter dialog for: {VCenterName}", vCenter.Name);

            var editVCenterWindow = _serviceProvider.GetRequiredService<EditVCenterWindow>();
            var viewModel = editVCenterWindow.DataContext as EditVCenterViewModel;
            
            if (viewModel != null)
            {
                await viewModel.InitializeAsync(vCenter);
                var result = editVCenterWindow.ShowDialog();

                if (result == true)
                {
                    _logger.LogInformation("Edit vCenter dialog completed successfully for: {VCenterName}", vCenter.Name);
                    await LoadVCentersAsync();
                    StatusMessage = $"vCenter '{vCenter.DisplayName}' updated successfully";
                }
                else
                {
                    _logger.LogInformation("Edit vCenter dialog was cancelled for: {VCenterName}", vCenter.Name);
                    StatusMessage = "Edit vCenter operation cancelled";
                }
            }
            else
            {
                _logger.LogError("Failed to get EditVCenterViewModel from EditVCenterWindow");
                StatusMessage = "Failed to initialize edit dialog";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to edit vCenter: {VCenterName}", vCenter.Name);
            StatusMessage = $"Failed to edit vCenter: {ex.Message}";
        }
    }

    /// <summary>
    /// Command to delete a vCenter server
    /// </summary>
    [RelayCommand]
    private async Task DeleteVCenterAsync(VCenter? vCenter)
    {
        if (vCenter == null) return;

        try
        {
            _logger.LogInformation("Requesting to delete vCenter: {VCenterName}", vCenter.Name);

            // Show confirmation dialog
            var result = MessageBox.Show(
                $"Are you sure you want to delete vCenter '{vCenter.DisplayName}'?\n\n" +
                "This action will permanently remove:\n" +
                "• The vCenter connection\n" +
                "• All collected cluster data\n" +
                "• All collected host data\n" +
                "• All collected datastore data\n" +
                "• All historical check results\n\n" +
                "This action cannot be undone.",
                "Confirm Delete vCenter",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);

            if (result == MessageBoxResult.Yes)
            {
                IsLoading = true;
                StatusMessage = $"Deleting vCenter '{vCenter.DisplayName}' and all associated data...";

                _logger.LogInformation("User confirmed deletion of vCenter: {VCenterName}", vCenter.Name);

                // Remove the vCenter - EF Core will handle cascade deletes for related data
                _context.VCenters.Remove(vCenter);
                await _context.SaveChangesAsync();

                _logger.LogInformation("Successfully deleted vCenter: {VCenterName}", vCenter.Name);

                // Reload data to refresh the UI
                await LoadVCentersAsync();
                StatusMessage = $"vCenter '{vCenter.DisplayName}' and all associated data deleted successfully";
                
                // Clear selection if deleted vCenter was selected
                if (SelectedVCenter?.Id == vCenter.Id)
                {
                    SelectedVCenter = null;
                }
            }
            else
            {
                _logger.LogInformation("User cancelled deletion of vCenter: {VCenterName}", vCenter.Name);
                StatusMessage = "Delete operation cancelled";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete vCenter: {VCenterName}", vCenter?.Name ?? "Unknown");
            StatusMessage = $"Failed to delete vCenter: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Command to test connection to a vCenter server via Windows Service
    /// </summary>
    [RelayCommand]
    private async Task TestVCenterConnectionAsync(VCenter? vCenter)
    {
        if (vCenter == null) return;

        try
        {
            IsLoading = true;
            StatusMessage = $"Testing connection to {vCenter.DisplayName} via service...";

            _logger.LogInformation("Testing connection to vCenter via service: {VCenterName}", vCenter.Name);

            // Check if service is running
            var serviceStatus = await _serviceConfigurationManager.GetServiceStatusAsync();
            if (serviceStatus == null || serviceStatus.LastHeartbeat < DateTime.UtcNow.AddSeconds(-30))
            {
                StatusMessage = "Windows Service is not running. Cannot test vCenter connection.";
                return;
            }

            // Send test connection command to service
            var parameters = new { VCenterId = vCenter.Id, Action = "TestConnection" };
            var commandId = await _serviceConfigurationManager.SendCommandAsync("TestVCenterConnection", parameters);

            StatusMessage = $"Connection test request sent to service (ID: {commandId}). Processing...";

            // Monitor for completion
            var result = await MonitorCommandCompletionAsync(commandId, "vCenter connection test");
            
            if (result != null)
            {
                var testResult = JsonSerializer.Deserialize<Dictionary<string, object>>(result);
                if (testResult != null && testResult.TryGetValue("IsSuccessful", out var successObj))
                {
                    var isSuccessful = successObj.ToString() == "True";
                    vCenter.UpdateConnectionStatus(isSuccessful);
                    
                    if (isSuccessful && testResult.TryGetValue("ResponseTime", out var responseTimeObj))
                    {
                        StatusMessage = $"Connection to {vCenter.DisplayName} successful - {responseTimeObj}ms";
                    }
                    else if (testResult.TryGetValue("ErrorMessage", out var errorObj))
                    {
                        StatusMessage = $"Connection to {vCenter.DisplayName} failed: {errorObj}";
                    }
                }
            }
        }
        catch (Exception ex)
        {
            vCenter.UpdateConnectionStatus(false);
            _logger.LogError(ex, "Failed to test connection to vCenter via service: {VCenterName}", vCenter.Name);
            
            // Handle credential decryption errors specifically
            if (await HandleCredentialDecryptionErrorAsync(vCenter, ex, "test connection to"))
            {
                return; // Error was handled, don't show generic message
            }
            
            StatusMessage = $"Failed to test connection to {vCenter.DisplayName}: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Handles credential decryption errors by offering to update credentials
    /// </summary>
    /// <param name="vCenter">The vCenter with credential issues</param>
    /// <param name="ex">The exception that occurred</param>
    /// <param name="operation">Description of the operation that failed (e.g., "connect to", "test connection to")</param>
    /// <returns>True if the error was handled, false if it should be processed as a regular error</returns>
    private async Task<bool> HandleCredentialDecryptionErrorAsync(VCenter vCenter, Exception ex, string operation)
    {
        if (ex.Message.Contains("Failed to decrypt credentials") || 
            ex.Message.Contains("invalid encryption scope") ||
            ex.Message.Contains("corrupted data"))
        {
            var result = MessageBox.Show(
                $"Failed to decrypt stored credentials for '{vCenter.DisplayName}'.\n\n" +
                "This can happen when encryption settings have changed or credentials are corrupted.\n\n" +
                "Would you like to update the credentials for this vCenter?",
                "Credential Decryption Error",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result == MessageBoxResult.Yes)
            {
                // Open edit dialog to allow user to re-enter credentials
                await EditVCenterAsync(vCenter);
            }
            else
            {
                StatusMessage = $"Credential decryption failed for {vCenter.DisplayName}. Please update credentials to resolve this issue.";
            }
            
            return true; // Error was handled
        }
        
        return false; // Error was not a credential decryption issue
    }

    /// <summary>
    /// Command to open settings
    /// </summary>
    [RelayCommand]
    private void Settings()
    {
        try
        {
            _logger.LogInformation("Opening settings dialog");

            var settingsWindow = _serviceProvider.GetRequiredService<SettingsWindow>();
            settingsWindow.ShowDialog();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open settings");
            StatusMessage = $"Failed to open settings: {ex.Message}";
        }
    }

    /// <summary>
    /// Command to run iSCSI checks via Windows Service
    /// </summary>
    [RelayCommand]
    private async Task RuniSCSICheckAsync()
    {
        if (SelectedVCenter == null)
        {
            StatusMessage = "Please select a vCenter server first";
            return;
        }

        if (!IsServiceRunning)
        {
            StatusMessage = "Windows Service is not running. Cannot execute checks.";
            return;
        }

        try
        {
            IsLoading = true;
            StatusMessage = "Sending iSCSI check command to Windows Service...";

            // Send command to service instead of executing directly
            var parameters = new
            {
                VCenterId = SelectedVCenter.Id,
                CheckType = "iSCSI",
                IsManualRun = true
            };

            var commandId = await _serviceConfigurationManager.SendCommandAsync(
                ServiceCommandTypes.ExecuteCheck, 
                parameters);

            StatusMessage = $"iSCSI check command sent to service (ID: {commandId}). Monitoring for results...";

            // Monitor for completion
            await MonitorCommandCompletionAsync(commandId, "iSCSI check");

            // Refresh check results
            await CheckResultsViewModel.LoadCheckResultsAsync(SelectedVCenter);

            _logger.LogInformation("iSCSI check command sent to service with ID: {CommandId}", commandId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send iSCSI check command to service");
            StatusMessage = $"Failed to send command to service: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Monitors a service command for completion and returns the result
    /// </summary>
    private async Task<string?> MonitorCommandCompletionAsync(string commandId, string operationName)
    {
        var timeout = DateTime.UtcNow.AddMinutes(5); // 5 minute timeout
        
        while (DateTime.UtcNow < timeout)
        {
            await Task.Delay(2000); // Check every 2 seconds
            
            var command = await _serviceConfigurationManager.GetCommandResultAsync(commandId);
            if (command != null)
            {
                if (command.Status == "Completed")
                {
                    return command.Result;
                }
                else if (command.Status == "Failed")
                {
                    throw new InvalidOperationException($"{operationName} failed: {command.ErrorMessage}");
                }
            }
        }
        
        throw new TimeoutException($"{operationName} timed out");
    }

    /// <summary>
    /// Monitors Windows Service status
    /// </summary>
    private async Task MonitorServiceStatusAsync()
    {
        try
        {
            var serviceStatus = await _serviceConfigurationManager.GetServiceStatusAsync();
            if (serviceStatus != null)
            {
                // Update service status string exactly as received from the service
                ServiceStatus = serviceStatus.Status;
                ServiceLastHeartbeat = serviceStatus.LastHeartbeat;
                ServiceActiveExecutions = serviceStatus.ActiveExecutions;
                
                // Consider service running if:
                // 1. Status is explicitly "Running" or "Starting"
                // 2. AND heartbeat is within last 30 seconds (heartbeat updates every 10 seconds)
                var wasServiceRunning = IsServiceRunning;
                var heartbeatCutoff = DateTime.UtcNow.AddSeconds(-30);
                var statusIsRunning = serviceStatus.Status?.Equals("Running", StringComparison.OrdinalIgnoreCase) == true ||
                                    serviceStatus.Status?.Equals("Starting", StringComparison.OrdinalIgnoreCase) == true;
                var heartbeatFresh = serviceStatus.LastHeartbeat > heartbeatCutoff;
                
                IsServiceRunning = statusIsRunning && heartbeatFresh;
                
                _logger.LogDebug("Service Status Check - Status: '{Status}', Heartbeat: {Heartbeat}, IsRunning: {IsRunning}, " +
                               "StatusIsRunning: {StatusIsRunning}, HeartbeatFresh: {HeartbeatFresh}", 
                               serviceStatus.Status, serviceStatus.LastHeartbeat, IsServiceRunning, statusIsRunning, heartbeatFresh);
                
                // If service status changed, update PowerCLI status
                if (wasServiceRunning != IsServiceRunning)
                {
                    _logger.LogInformation("Service status changed: {WasRunning} -> {IsRunning}", wasServiceRunning, IsServiceRunning);
                    await CheckPowerCLIAsync();
                }
            }
            else
            {
                _logger.LogDebug("No service status received - service appears to be stopped");
                ServiceStatus = "Stopped";
                IsServiceRunning = false;
                ServiceLastHeartbeat = DateTime.MinValue;
                ServiceActiveExecutions = 0;
                
                // Service not running means PowerCLI is not available
                IsPowerCLIAvailable = false;
            }
            
            // Update vCenter connection status based on service status
            UpdateVCenterConnectionStates();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to monitor service status");
            ServiceStatus = "Error";
            IsServiceRunning = false;
            IsPowerCLIAvailable = false;
            ServiceLastHeartbeat = DateTime.MinValue;
            ServiceActiveExecutions = 0;
            
            // When service has error, mark all vCenters as disconnected
            UpdateVCenterConnectionStates();
        }
    }

    /// <summary>
    /// Updates the connection status of all vCenters based on current service status
    /// </summary>
    private void UpdateVCenterConnectionStates()
    {
        try
        {
            foreach (var vCenter in VCenters)
            {
                // If service is not running, all vCenters are considered disconnected
                if (!IsServiceRunning)
                {
                    vCenter.UpdateConnectionStatus(false);
                }
                else
                {
                    // TODO: In a real implementation, you would check the actual connection status
                    // through the service for each vCenter. For now, we'll assume they're connected
                    // if the service is running and they were previously connected.
                    
                    // Keep current status if service is running - don't override actual connection checks
                    // The service should update the connection status through other means
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update vCenter connection states");
        }
    }

    /// <summary>
    /// Command to assign a vCenter to a specific availability zone
    /// </summary>
    [RelayCommand]
    private async Task AssignVCenterToZoneAsync(object? parameter)
    {
        try
        {
            if (parameter is VCenter vCenter)
            {
                // Handle "No Zone" assignment (remove from current zone)
                _logger.LogInformation("Removing vCenter {VCenterName} from any availability zone", vCenter.Name);
                await AvailabilityZoneViewModel.MoveVCenterToZoneAsync(vCenter, null);
                await LoadVCentersAsync();
                StatusMessage = $"vCenter '{vCenter.DisplayName}' removed from availability zone";
            }
            else if (parameter is object[] parameters && parameters.Length == 2 && 
                     parameters[0] is AvailabilityZone targetZone && parameters[1] is VCenter targetVCenter)
            {
                // Handle assignment to specific zone
                _logger.LogInformation("Moving vCenter {VCenterName} to availability zone {ZoneName}", 
                    targetVCenter.Name, targetZone.Name);
                
                await AvailabilityZoneViewModel.MoveVCenterToZoneAsync(targetVCenter, targetZone);
                await LoadVCentersAsync();
                StatusMessage = $"vCenter '{targetVCenter.DisplayName}' moved to zone '{targetZone.Name}'";
            }
            else
            {
                _logger.LogWarning("AssignVCenterToZone called with invalid parameter: {Parameter}", parameter);
                StatusMessage = "Invalid zone assignment parameters";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to assign vCenter to zone");
            StatusMessage = $"Failed to assign vCenter to zone: {ex.Message}";
        }
    }

    /// <summary>
    /// Command to add a new availability zone
    /// </summary>
    [RelayCommand]
    private async Task AddAvailabilityZoneAsync()
    {
        try
        {
            _logger.LogInformation("Opening Add Availability Zone dialog");

            var addZoneWindow = new Views.AddAvailabilityZoneWindow(AvailabilityZoneViewModel);
            addZoneWindow.Owner = Application.Current.MainWindow;
            
            var result = addZoneWindow.ShowDialog();

            if (result == true)
            {
                await LoadVCentersAsync();
                StatusMessage = "Availability zone added successfully";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to add availability zone");
            StatusMessage = $"Failed to add availability zone: {ex.Message}";
        }
    }

    /// <summary>
    /// Command to edit an availability zone (proxy to AvailabilityZoneViewModel)
    /// </summary>
    [RelayCommand]
    private void EditAvailabilityZone(AvailabilityZone? zone)
    {
        if (zone == null) return;
        AvailabilityZoneViewModel.EditAvailabilityZoneCommand.Execute(zone);
    }

    /// <summary>
    /// Command to delete an availability zone (proxy to AvailabilityZoneViewModel)
    /// </summary>
    [RelayCommand]
    private async Task DeleteAvailabilityZoneAsync(AvailabilityZone? zone)
    {
        if (zone == null) return;
        await AvailabilityZoneViewModel.DeleteAvailabilityZoneCommand.ExecuteAsync(zone);
    }

    /// <summary>
    /// Handles the request to edit an availability zone
    /// </summary>
    private async void OnEditAvailabilityZoneRequested(AvailabilityZone zone)
    {
        try
        {
            _logger.LogInformation("Opening Edit Availability Zone dialog for: {ZoneName}", zone.Name);

            var editZoneViewModel = new EditAvailabilityZoneViewModel(
                _serviceProvider.GetRequiredService<ILogger<EditAvailabilityZoneViewModel>>(),
                _context);

            await editZoneViewModel.InitializeAsync(zone);

            var editZoneWindow = new Views.EditAvailabilityZoneWindow(editZoneViewModel);
            editZoneWindow.Owner = Application.Current.MainWindow;
            
            var result = editZoneWindow.ShowDialog();

            if (result == true)
            {
                await LoadVCentersAsync();
                await AvailabilityZoneViewModel.LoadAvailabilityZonesAsync();
                StatusMessage = $"Availability zone '{zone.Name}' updated successfully";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to edit availability zone: {ZoneName}", zone.Name);
            StatusMessage = $"Failed to edit availability zone: {ex.Message}";
        }
    }

    /// <summary>
    /// Handles when availability zones are changed and need to refresh
    /// </summary>
    private async Task OnAvailabilityZonesChanged()
    {
        await LoadVCentersAsync();
    }

    /// <summary>
    /// Monitor connections periodically to update status
    /// </summary>
    private async Task MonitorConnectionsAsync()
    {
        try
        {
            // Check database connectivity
            await CheckDatabaseConnectivityAsync();
            
            // Monitor service status
            await MonitorServiceStatusAsync();

            // Note: vCenter connection health is now monitored by the Windows Service
            // The GUI only monitors the service status and database connectivity
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during connection monitoring");
        }
    }

    /// <summary>
    /// Initialize the view model and load data
    /// </summary>
    public async Task InitializeAsync()
    {
        try
        {
            StatusMessage = "Initializing application...";

            await LoadVCentersAsync();
            await CheckPowerCLIAsync();
            await CheckDatabaseConnectivityAsync();

            StatusMessage = "Application initialized successfully";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize application");
            StatusMessage = $"Initialization failed: {ex.Message}";
        }
    }

    /// <summary>
    /// Load vCenter servers from database
    /// </summary>
    private async Task LoadVCentersAsync()
    {
        try
        {
            _logger.LogDebug("Loading vCenter servers from database");

            // Load availability zones with their vCenters
            var zones = await _context.AvailabilityZones
                .Include(az => az.VCenters)
                    .ThenInclude(v => v.Clusters)
                    .ThenInclude(c => c.Hosts)
                .OrderBy(az => az.SortOrder)
                .ThenBy(az => az.Name)
                .ToListAsync();

            // Load all vCenters (including ungrouped ones)
            var vcenters = await _context.VCenters
                .Include(v => v.AvailabilityZone)
                .Include(v => v.Clusters)
                .ThenInclude(c => c.Hosts)
                .OrderBy(v => v.AvailabilityZone!.SortOrder)
                .ThenBy(v => v.AvailabilityZone!.Name)
                .ThenBy(v => v.Name)
                .ToListAsync();

            Application.Current.Dispatcher.Invoke(() =>
            {
                AvailabilityZones.Clear();
                foreach (var zone in zones)
                {
                    AvailabilityZones.Add(zone);
                }

                VCenters.Clear();
                foreach (var vcenter in vcenters)
                {
                    VCenters.Add(vcenter);
                }
            });

            // Update availability zone view model
            await AvailabilityZoneViewModel.LoadAvailabilityZonesAsync();

            _logger.LogDebug("Loaded {VCenterCount} vCenter servers in {ZoneCount} availability zones", 
                vcenters.Count, zones.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load vCenter servers");
            throw;
        }
    }

    /// <summary>
    /// Check if VMware connectivity is available (considering both PowerCLI and service status)
    /// </summary>
    private async Task CheckPowerCLIAsync()
    {
        try
        {
            _logger.LogDebug("Checking VMware REST API connectivity availability");
            
            // Check basic PowerCLI availability
            var powerCLIAvailable = await _vmwareService.IsPowerCLIAvailableAsync();
            
            // PowerCLI is only truly available if the service is also running
            // Since all VMware operations go through the Windows Service
            IsPowerCLIAvailable = powerCLIAvailable && IsServiceRunning;
            
            _logger.LogDebug("VMware REST API availability: {Available} (PowerCLI: {PowerCLI}, Service: {Service})", 
                IsPowerCLIAvailable, powerCLIAvailable, IsServiceRunning);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to check VMware REST API availability");
            IsPowerCLIAvailable = false;
        }
    }

    /// <summary>
    /// Check database connectivity
    /// </summary>
    private async Task CheckDatabaseConnectivityAsync()
    {
        try
        {
            _logger.LogDebug("Checking database connectivity");
            
            // Test database connection by trying to access the database
            await _context.Database.CanConnectAsync();
            
            // Verify we can actually query the database
            var connectionTest = await _context.VCenters.Take(1).CountAsync();
            
            IsDatabaseConnected = true;
            _logger.LogDebug("Database connectivity: Connected");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to check database connectivity");
            IsDatabaseConnected = false;
        }
    }

    /// <summary>
    /// Handle property changes to update computed properties
    /// </summary>
    partial void OnVCentersChanged(ObservableCollection<VCenter> value)
    {
        OnPropertyChanged(nameof(ShowWelcomeScreen));
        OnPropertyChanged(nameof(ShowContentArea));
    }

    /// <summary>
    /// Handle selected vCenter changes
    /// </summary>
    partial void OnSelectedVCenterChanged(VCenter? value)
    {
        OnPropertyChanged(nameof(ShowWelcomeScreen));
        OnPropertyChanged(nameof(ShowContentArea));
        
        if (value != null)
        {
            _logger.LogDebug("Selected vCenter changed to: {VCenterName}", value.Name);
            
            // Stop previous refreshes
            VCenterOverviewViewModel.StopRefresh();
            
            // Load overview, infrastructure, and check results data
            _ = VCenterOverviewViewModel.LoadOverviewDataAsync(value);
            _ = InfrastructureViewModel.LoadInfrastructureAsync(value);
            _ = Task.Run(async () =>
            {
                try
                {
                    await CheckResultsViewModel.LoadCheckResultsAsync(value);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to load check results for vCenter: {VCenterName}", value.Name);
                }
            });
        }
        else
        {
            // Stop all refreshes when no vCenter is selected
            VCenterOverviewViewModel.StopRefresh();
            
            // Clear data
            OverviewData = null;
            Application.Current.Dispatcher.Invoke(() =>
            {
                InfrastructureViewModel.InfrastructureItems.Clear();
                CheckResultsViewModel.CheckResults.Clear();
            });
        }
    }

    /// <summary>
    /// Cleanup resources
    /// </summary>
    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        
        // Update status message when loading state changes
        if (e.PropertyName == nameof(IsLoading) && IsLoading)
        {
            // Status message is typically set by the calling method
        }
    }

    /// <summary>
    /// Dispose resources
    /// </summary>
    public void Dispose()
    {
        // Unsubscribe from events
        if (AvailabilityZoneViewModel != null)
        {
            AvailabilityZoneViewModel.EditAvailabilityZoneRequested -= OnEditAvailabilityZoneRequested;
        }
        
        _clockTimer?.Dispose();
        _connectionMonitorTimer?.Dispose();
        _serviceMonitorTimer?.Dispose();
        _serviceConfigurationManager?.Dispose();
        InfrastructureViewModel?.Dispose();
        VCenterOverviewViewModel?.Dispose();
    }
} 