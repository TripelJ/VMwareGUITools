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
    private VCenter? _selectedVCenter;

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
            context);

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
    /// Command to load overview data for the selected vCenter
    /// </summary>
    [RelayCommand]
    private async Task LoadOverviewDataAsync()
    {
        if (SelectedVCenter == null)
        {
            OverviewData = null;
            return;
        }

        try
        {
            IsLoading = true;
            StatusMessage = $"Loading overview data for {SelectedVCenter.DisplayName}...";

            _logger.LogInformation("Loading overview data for vCenter: {VCenterName}", SelectedVCenter.Name);

            // Connect to vCenter using REST API service directly
            var restSession = await _restApiService.ConnectAsync(SelectedVCenter);

            // Update connection status since we successfully connected
            SelectedVCenter.UpdateConnectionStatus(true);

            // Get overview data
            OverviewData = await _restApiService.GetOverviewDataAsync(restSession);
            StatusMessage = $"Overview data loaded successfully for {SelectedVCenter.DisplayName}";

            _logger.LogInformation("Overview data loaded successfully for vCenter: {VCenterName}", SelectedVCenter.Name);
        }
        catch (Exception ex)
        {
            if (SelectedVCenter != null)
            {
                SelectedVCenter.UpdateConnectionStatus(false);
            }
            _logger.LogError(ex, "Failed to load overview data for vCenter: {VCenterName}", SelectedVCenter?.Name);
            StatusMessage = $"Failed to load overview data: {ex.Message}";
            OverviewData = null;
        }
        finally
        {
            IsLoading = false;
        }
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

            var vcenters = await _context.VCenters
                .Include(v => v.Clusters)
                .ThenInclude(c => c.Hosts)
                .OrderBy(v => v.Name)
                .ToListAsync();

            Application.Current.Dispatcher.Invoke(() =>
            {
                VCenters.Clear();
                foreach (var vcenter in vcenters)
                {
                    VCenters.Add(vcenter);
                }
            });

            _logger.LogDebug("Loaded {Count} vCenter servers", vcenters.Count);
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
            // Automatically load overview data when a vCenter is selected
            _ = LoadOverviewDataAsync();
            
            // Load infrastructure data
            _ = InfrastructureViewModel.LoadInfrastructureAsync(value);
        }
        else
        {
            OverviewData = null;
            InfrastructureViewModel.InfrastructureItems.Clear();
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
        _clockTimer?.Dispose();
        _connectionMonitorTimer?.Dispose();
    }
} 