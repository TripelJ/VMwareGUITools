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
    private readonly System.Timers.Timer _clockTimer;
    private readonly System.Timers.Timer _connectionMonitorTimer;

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
        IServiceProvider serviceProvider)
    {
        _logger = logger;
        _context = context;
        _vmwareService = vmwareService;
        _restApiService = restApiService;
        _serviceProvider = serviceProvider;

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
            _serviceProvider.GetRequiredService<ICheckExecutionService>());

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
    /// Command to run iSCSI dead path checks on all hosts
    /// </summary>
    [RelayCommand]
    private async Task RuniSCSICheckAsync()
    {
        if (SelectedVCenter == null)
        {
            StatusMessage = "Please select a vCenter server first";
            return;
        }

        try
        {
            IsLoading = true;
            StatusMessage = "Running iSCSI dead path checks...";

            _logger.LogInformation("Running iSCSI checks for vCenter: {VCenterName}", SelectedVCenter.Name);

            // Get the iSCSI check definition
            var iSCSICheck = await _context.CheckDefinitions
                .Include(cd => cd.Category)
                .FirstOrDefaultAsync(cd => cd.Name.Contains("iSCSI") && cd.Name.Contains("Dead Path"));

            if (iSCSICheck == null)
            {
                // Create the iSCSI check if it doesn't exist
                var storageCategory = await _context.CheckCategories
                    .FirstOrDefaultAsync(cc => cc.Name == "Storage")
                    ?? new CheckCategory
                    {
                        Name = "Storage",
                        Description = "Storage health and configuration checks",
                        Type = CheckCategoryType.Health,
                        Enabled = true,
                        SortOrder = 5
                    };

                if (storageCategory.Id == 0)
                {
                    _context.CheckCategories.Add(storageCategory);
                    await _context.SaveChangesAsync();
                }

                iSCSICheck = new CheckDefinition
                {
                    CategoryId = storageCategory.Id,
                    Name = "iSCSI Dead Path Check",
                    Description = "Check for dead or inactive iSCSI storage paths",
                    ExecutionType = CheckExecutionType.vSphereRestAPI,
                    DefaultSeverity = CheckSeverity.Critical,
                    IsEnabled = true,
                    TimeoutSeconds = 120,
                    ScriptPath = "Scripts/Storage/Check-iSCSIDeadPaths.ps1",
                    Script = "# PowerShell script to check iSCSI path status",
                    Parameters = """{"checkAllAdapters": true}""",
                    Thresholds = """{"maxDeadPaths": 0}"""
                };

                _context.CheckDefinitions.Add(iSCSICheck);
                await _context.SaveChangesAsync();
            }

            // Use the same REST API discovery method that the Infrastructure tab uses
            VSphereSession? session = null;
            var allHosts = new List<(HostInfo Host, ClusterInfo Cluster)>();
            
            try
            {
                // Establish REST API session (same as Infrastructure tab)
                session = await _restApiService.ConnectAsync(SelectedVCenter);
                
                // Discover clusters first
                var clusters = await _restApiService.DiscoverClustersAsync(session);
                
                // Get hosts from each cluster (same approach as Infrastructure tab)
                foreach (var cluster in clusters)
                {
                    try
                    {
                        var hostsInCluster = await _restApiService.DiscoverHostsAsync(session, cluster.MoId);
                        // Associate each host with its cluster
                        foreach (var host in hostsInCluster)
                        {
                            allHosts.Add((host, cluster));
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to discover hosts in cluster {ClusterName}", cluster.Name);
                    }
                }
                
                if (!allHosts.Any())
                {
                    StatusMessage = "No hosts found for the selected vCenter";
                    return;
                }

                _logger.LogInformation("Discovered {HostCount} hosts for iSCSI checks", allHosts.Count);

                var checkExecutionService = _serviceProvider.GetRequiredService<ICheckExecutionService>();
                var results = new List<CheckResult>();

                // Execute the check on each discovered host
                // Convert HostInfo to Host entities for check execution
                foreach (var (hostInfo, clusterInfo) in allHosts)
                {
                    try
                    {
                        // Check if host already exists in database
                        var existingHost = await _context.Hosts
                            .FirstOrDefaultAsync(h => h.MoId == hostInfo.MoId && h.VCenterId == SelectedVCenter.Id);
                        
                        Host hostEntity;
                        if (existingHost != null)
                        {
                            // Use existing host from database
                            hostEntity = existingHost;
                        }
                        else
                        {
                            // Create a new host entity with proper relationships
                            // First, try to find or create the cluster
                            var cluster = await _context.Clusters
                                .FirstOrDefaultAsync(c => c.VCenterId == SelectedVCenter.Id && c.Name == clusterInfo.Name);
                            
                            if (cluster == null)
                            {
                                // Create a temporary cluster if it doesn't exist
                                cluster = new Cluster
                                {
                                    Name = clusterInfo.Name,
                                    VCenterId = SelectedVCenter.Id,
                                    MoId = clusterInfo.MoId,
                                    Enabled = true,
                                    CreatedAt = DateTime.UtcNow,
                                    UpdatedAt = DateTime.UtcNow
                                };
                                _context.Clusters.Add(cluster);
                                await _context.SaveChangesAsync(); // Save to get the cluster ID
                            }
                            
                            // Create the host entity with proper foreign key relationships
                            hostEntity = new Host
                            {
                                Name = hostInfo.Name,
                                MoId = hostInfo.MoId,
                                IpAddress = hostInfo.IpAddress ?? "Unknown",
                                VCenterId = SelectedVCenter.Id,
                                ClusterId = cluster.Id,
                                HostType = hostInfo.Type,
                                ClusterName = clusterInfo.Name,
                                Enabled = true,
                                CreatedAt = DateTime.UtcNow,
                                UpdatedAt = DateTime.UtcNow
                            };
                            
                            // Add to database and save
                            _context.Hosts.Add(hostEntity);
                            await _context.SaveChangesAsync(); // Save to get the host ID
                        }

                        var result = await checkExecutionService.ExecuteCheckAsync(hostEntity, iSCSICheck, SelectedVCenter);
                        results.Add(result);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to execute iSCSI check on host {HostName}", hostInfo.Name);
                        
                        // Create a failed result for reporting purposes
                        var failedResult = new CheckResult
                        {
                            CheckDefinitionId = iSCSICheck.Id,
                            HostId = 0, // No host ID available
                            Status = CheckStatus.Error,
                            ErrorMessage = $"Failed to execute check: {ex.Message}",
                            ExecutedAt = DateTime.UtcNow,
                            ExecutionTime = TimeSpan.Zero,
                            Output = "Check execution failed",
                            Details = $"Host: {hostInfo.Name}, Error: {ex.Message}"
                        };
                        results.Add(failedResult);
                    }
                }

                // Load the results into the check results view model
                await CheckResultsViewModel.LoadCheckResultsAsync(SelectedVCenter);

                var passedCount = results.Count(r => r.Status == CheckStatus.Passed);
                var failedCount = results.Count(r => r.Status == CheckStatus.Failed);

                StatusMessage = $"iSCSI checks completed: {passedCount} passed, {failedCount} failed on {allHosts.Count} hosts";

                _logger.LogInformation("iSCSI checks completed for {HostCount} hosts: {PassedCount} passed, {FailedCount} failed",
                    allHosts.Count, passedCount, failedCount);
            }
            finally
            {
                // Clean up the session
                if (session != null)
                {
                    try
                    {
                        await _restApiService.DisconnectAsync(session);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to disconnect REST API session");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to run iSCSI checks");
            StatusMessage = $"Failed to run iSCSI checks: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
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
            // Check each vCenter's connection status
            var monitoringTasks = VCenters.Where(v => v.Enabled).Select(async vCenter =>
            {
                try
                {
                    // Reset stale connection flags first
                    if (vCenter.IsCurrentlyConnected && 
                        vCenter.LastSuccessfulConnection.HasValue &&
                        DateTime.UtcNow - vCenter.LastSuccessfulConnection.Value > TimeSpan.FromMinutes(5))
                    {
                        vCenter.UpdateConnectionStatus(false);
                    }

                    // If marked as disconnected but hasn't been tested recently, perform health check
                    if (!vCenter.IsCurrentlyConnected && 
                        (!vCenter.LastSuccessfulConnection.HasValue || 
                         DateTime.UtcNow - vCenter.LastSuccessfulConnection.Value > TimeSpan.FromMinutes(2)))
                    {
                        var isHealthy = await _vmwareService.TestConnectionHealthAsync(vCenter);
                        vCenter.UpdateConnectionStatus(isHealthy);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error monitoring connection for vCenter: {VCenterName}", vCenter.Name);
                    vCenter.UpdateConnectionStatus(false);
                }
            });

            await Task.WhenAll(monitoringTasks);
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
            _ = CheckResultsViewModel.LoadCheckResultsAsync(value);
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
        InfrastructureViewModel?.Dispose();
        VCenterOverviewViewModel?.Dispose();
    }
} 