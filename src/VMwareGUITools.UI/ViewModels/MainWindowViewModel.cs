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
using VMwareGUITools.Infrastructure.VMware;
using VMwareGUITools.Infrastructure.Checks;
using VMwareGUITools.UI.Views;
using VMwareGUITools.Infrastructure.Services;
using System.Text.Json;

namespace VMwareGUITools.UI.ViewModels;

/// <summary>
/// Main window view model handling the primary application interface
/// </summary>
public partial class MainWindowViewModel : ObservableObject
{
    private readonly ILogger<MainWindowViewModel> _logger;
    private readonly VMwareDbContext _context;
    private readonly IVMwareConnectionService _vmwareService;
    private readonly IVSphereRestAPIService _restApiService;
    private readonly IServiceProvider _serviceProvider;
    private readonly IServiceConfigurationManager _serviceConfigurationManager;
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
        IVMwareConnectionService vmwareService,
        IVSphereRestAPIService restApiService,
        IServiceProvider serviceProvider,
        IServiceConfigurationManager serviceConfigurationManager)
    {
        _logger = logger;
        _context = context;
        _vmwareService = vmwareService;
        _restApiService = restApiService;
        _serviceProvider = serviceProvider;
        _serviceConfigurationManager = serviceConfigurationManager;

        // Initialize view models
        _infrastructureViewModel = new InfrastructureViewModel(
            _serviceProvider.GetRequiredService<ILogger<InfrastructureViewModel>>(),
            restApiService);
        
        _vCenterOverviewViewModel = new VCenterOverviewViewModel(
            _serviceProvider.GetRequiredService<ILogger<VCenterOverviewViewModel>>(),
            vmwareService,
            restApiService);

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
            var result = addVCenterWindow.ShowDialog();

            if (result == true)
            {
                await LoadVCentersAsync();
                StatusMessage = "vCenter server added successfully";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to add vCenter server");
            StatusMessage = $"Failed to add vCenter server: {ex.Message}";
        }
    }

    /// <summary>
    /// Command to connect to a vCenter server
    /// </summary>
    [RelayCommand]
    private async Task ConnectVCenterAsync(VCenter? vCenter)
    {
        if (vCenter == null) return;

        try
        {
            IsLoading = true;
            StatusMessage = $"Connecting to {vCenter.DisplayName}...";

            _logger.LogInformation("Connecting to vCenter: {VCenterName}", vCenter.Name);

            var session = await _vmwareService.ConnectAsync(vCenter);
            
            // Update connection status
            vCenter.UpdateConnectionStatus(true);
            vCenter.LastScan = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            StatusMessage = $"Connected to {vCenter.DisplayName}";
            
            // Set as selected vCenter
            SelectedVCenter = VCenters.FirstOrDefault(v => v.Id == vCenter.Id);
            
            _logger.LogInformation("Successfully connected to vCenter: {VCenterName}", vCenter.Name);
        }
        catch (Exception ex)
        {
            vCenter.UpdateConnectionStatus(false);
            _logger.LogError(ex, "Failed to connect to vCenter: {VCenterName}", vCenter.Name);
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
        if (vCenter == null) return;

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
                    await LoadVCentersAsync();
                    StatusMessage = $"vCenter '{vCenter.DisplayName}' updated successfully";
                }
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
            _logger.LogInformation("Deleting vCenter: {VCenterName}", vCenter.Name);

            var result = MessageBox.Show(
                $"Are you sure you want to delete vCenter '{vCenter.DisplayName}'?\n\nThis will also remove all associated clusters and hosts data.",
                "Confirm Delete",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result == MessageBoxResult.Yes)
            {
                _context.VCenters.Remove(vCenter);
                await _context.SaveChangesAsync();

                await LoadVCentersAsync();
                StatusMessage = $"vCenter '{vCenter.DisplayName}' deleted successfully";
                
                // Clear selection if deleted vCenter was selected
                if (SelectedVCenter?.Id == vCenter.Id)
                {
                    SelectedVCenter = null;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete vCenter: {VCenterName}", vCenter.Name);
            StatusMessage = $"Failed to delete vCenter: {ex.Message}";
        }
    }

    /// <summary>
    /// Command to test connection to a vCenter server
    /// </summary>
    [RelayCommand]
    private async Task TestVCenterConnectionAsync(VCenter? vCenter)
    {
        if (vCenter == null) return;

        try
        {
            IsLoading = true;
            StatusMessage = $"Testing connection to {vCenter.DisplayName}...";

            _logger.LogInformation("Testing connection to vCenter: {VCenterName}", vCenter.Name);

            var credentials = _serviceProvider.GetRequiredService<ICredentialService>()
                .DecryptCredentials(vCenter.EncryptedCredentials);

            var testResult = await _vmwareService.TestConnectionAsync(vCenter.Url, credentials.Username, credentials.Password);

            if (testResult.IsSuccessful)
            {
                vCenter.UpdateConnectionStatus(true);
                StatusMessage = $"Connection to {vCenter.DisplayName} successful - {testResult.ResponseTime.TotalMilliseconds:F0}ms";
            }
            else
            {
                vCenter.UpdateConnectionStatus(false);
                StatusMessage = $"Connection to {vCenter.DisplayName} failed: {testResult.ErrorMessage}";
            }
        }
        catch (Exception ex)
        {
            vCenter.UpdateConnectionStatus(false);
            _logger.LogError(ex, "Failed to test connection to vCenter: {VCenterName}", vCenter.Name);
            StatusMessage = $"Failed to test connection to {vCenter.DisplayName}: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
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
    /// Monitors a service command for completion
    /// </summary>
    private async Task MonitorCommandCompletionAsync(string commandId, string operationName)
    {
        var timeout = DateTime.UtcNow.AddMinutes(10); // 10 minute timeout
        
        while (DateTime.UtcNow < timeout)
        {
            await Task.Delay(2000); // Check every 2 seconds
            
            var command = await _serviceConfigurationManager.GetCommandResultAsync(commandId);
            if (command != null)
            {
                if (command.Status == "Completed")
                {
                    StatusMessage = $"{operationName} completed successfully";
                    return;
                }
                else if (command.Status == "Failed")
                {
                    StatusMessage = $"{operationName} failed: {command.ErrorMessage}";
                    return;
                }
            }
        }
        
        StatusMessage = $"{operationName} timed out";
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
                ServiceStatus = serviceStatus.Status;
                ServiceLastHeartbeat = serviceStatus.LastHeartbeat;
                ServiceActiveExecutions = serviceStatus.ActiveExecutions;
                
                // Consider service running if heartbeat is within last 2 minutes
                IsServiceRunning = serviceStatus.LastHeartbeat > DateTime.UtcNow.AddMinutes(-2);
            }
            else
            {
                ServiceStatus = "Unknown";
                IsServiceRunning = false;
                ServiceLastHeartbeat = DateTime.MinValue;
                ServiceActiveExecutions = 0;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to monitor service status");
            ServiceStatus = "Error";
            IsServiceRunning = false;
        }
    }

    /// <summary>
    /// Command to assign a vCenter to an availability zone
    /// </summary>
    [RelayCommand]
    private void AssignVCenterToZone(object? parameter)
    {
        if (parameter == null)
            return;

        // For now, we'll implement this when we add the context menu functionality
        // This method is ready for future drag-and-drop or context menu operations
        
        try
        {
            // Example: await _availabilityZoneViewModel.MoveVCenterToZoneAsync(vCenter, targetZone);
            // await LoadVCentersAsync();
            StatusMessage = "vCenter assignment functionality ready for implementation";
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

            // Check vCenter connections health for connected vCenters
            foreach (var vCenter in VCenters.Where(v => v.IsCurrentlyConnected))
            {
                try
                {
                    var isHealthy = await _vmwareService.TestConnectionHealthAsync(vCenter);
                    if (!isHealthy)
                    {
                        vCenter.UpdateConnectionStatus(false);
                        await _context.SaveChangesAsync();
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Health check failed for vCenter: {VCenterName}", vCenter.Name);
                    vCenter.UpdateConnectionStatus(false);
                    await _context.SaveChangesAsync();
                }
            }
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
    /// Check if VMware connectivity is available (using REST API)
    /// </summary>
    private async Task CheckPowerCLIAsync()
    {
        try
        {
            _logger.LogDebug("Checking VMware REST API connectivity availability");
            IsPowerCLIAvailable = await _vmwareService.IsPowerCLIAvailableAsync();
            _logger.LogDebug("VMware REST API availability: {Available}", IsPowerCLIAvailable);
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