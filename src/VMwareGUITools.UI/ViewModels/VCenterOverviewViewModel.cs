using System.Collections.ObjectModel;
using System.Timers;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using VMwareGUITools.Core.Models;
using VMwareGUITools.Infrastructure.VMware;

namespace VMwareGUITools.UI.ViewModels;

/// <summary>
/// View model for the vCenter overview tab showing summary information and resource usage
/// </summary>
public partial class VCenterOverviewViewModel : ObservableObject, IDisposable
{
    private readonly ILogger<VCenterOverviewViewModel> _logger;
    private readonly IVMwareConnectionService _vmwareService;
    private readonly IVSphereRestAPIService _restApiService;
    private readonly System.Timers.Timer _overviewRefreshTimer;
    private VSphereSession? _currentSession;

    [ObservableProperty]
    private VCenterOverview? _overviewData;

    [ObservableProperty]
    private bool _isLoading = false;

    [ObservableProperty]
    private string _statusMessage = "Ready";

    [ObservableProperty]
    private DateTime _lastUpdated;

    [ObservableProperty]
    private VCenter? _selectedVCenter;

    [ObservableProperty]
    private bool _autoRefreshEnabled = true;

    public VCenterOverviewViewModel(
        ILogger<VCenterOverviewViewModel> logger,
        IVMwareConnectionService vmwareService,
        IVSphereRestAPIService restApiService)
    {
        _logger = logger;
        _vmwareService = vmwareService;
        _restApiService = restApiService;

        // Setup auto-refresh timer for overview data (every 30 seconds)
        _overviewRefreshTimer = new System.Timers.Timer(30000);
        _overviewRefreshTimer.Elapsed += async (sender, e) => await AutoRefreshOverviewAsync();
        _overviewRefreshTimer.AutoReset = true;
    }

    /// <summary>
    /// Loads overview data for the specified vCenter
    /// </summary>
    public async Task LoadOverviewDataAsync(VCenter vCenter)
    {
        if (vCenter == null)
        {
            OverviewData = null;
            _overviewRefreshTimer.Stop();
            return;
        }

        try
        {
            IsLoading = true;
            StatusMessage = "Loading vCenter overview...";
            SelectedVCenter = vCenter;

            _logger.LogInformation("Loading overview data for vCenter: {VCenterName}", vCenter.Name);

            // First ensure we have an active connection
            var session = await _vmwareService.ConnectAsync(vCenter);
            
            // Convert VMwareSession to VSphereSession for the REST API
            _currentSession = new VSphereSession
            {
                SessionId = session.SessionId,
                VCenterUrl = session.VCenterUrl,
                Username = session.Username,
                CreatedAt = session.CreatedAt,
                LastActivity = session.LastActivity,
                IsActive = session.IsActive,
                SessionToken = "", // Will be handled internally by the service
                VersionInfo = session.VersionInfo
            };

            // Get overview data (always live, never from database)
            var freshOverviewData = await _restApiService.GetOverviewDataAsync(_currentSession);
            
            // Update UI on dispatcher thread
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                OverviewData = freshOverviewData;
                LastUpdated = DateTime.Now;
                StatusMessage = $"Overview loaded successfully at {LastUpdated:HH:mm:ss}";
            });

            // Update connection status since we successfully loaded overview data
            vCenter.UpdateConnectionStatus(true);

            // Start auto-refresh if enabled
            if (AutoRefreshEnabled)
            {
                _overviewRefreshTimer.Start();
            }

            _logger.LogInformation("Overview data loaded successfully for vCenter: {VCenterName}", vCenter.Name);
        }
        catch (Exception ex)
        {
            vCenter?.UpdateConnectionStatus(false);
            _logger.LogError(ex, "Failed to load overview data for vCenter: {VCenterName}", vCenter?.Name);
            
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                StatusMessage = $"Failed to load overview data: {ex.Message}";
                OverviewData = null;
            });
            
            _overviewRefreshTimer.Stop();
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Auto-refresh overview data (CPU, memory, disk usage) without using database
    /// </summary>
    private async Task AutoRefreshOverviewAsync()
    {
        if (SelectedVCenter == null || IsLoading || _currentSession == null || !AutoRefreshEnabled)
            return;

        try
        {
            _logger.LogDebug("Auto-refreshing overview data for vCenter: {VCenterName}", SelectedVCenter.Name);

            // Get fresh overview data directly from vCenter API (no database)
            var freshOverviewData = await _restApiService.GetOverviewDataAsync(_currentSession);
            
            // Update UI on dispatcher thread
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                OverviewData = freshOverviewData;
                LastUpdated = DateTime.Now;
                // Don't update status message during auto-refresh to avoid UI noise
            });

            _logger.LogDebug("Auto-refresh completed for vCenter: {VCenterName}", SelectedVCenter.Name);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Auto-refresh failed for vCenter overview: {VCenterName}", SelectedVCenter?.Name);
            
            // Stop auto-refresh on connection errors
            if (ex.Message.Contains("connection") || ex.Message.Contains("session"))
            {
                _overviewRefreshTimer.Stop();
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    StatusMessage = $"Connection lost - auto-refresh disabled";
                });
            }
        }
    }

    /// <summary>
    /// Command to refresh the overview data
    /// </summary>
    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (SelectedVCenter != null)
        {
            await LoadOverviewDataAsync(SelectedVCenter);
        }
    }

    /// <summary>
    /// Command to toggle auto-refresh
    /// </summary>
    [RelayCommand]
    private void ToggleAutoRefresh()
    {
        AutoRefreshEnabled = !AutoRefreshEnabled;
        
        if (AutoRefreshEnabled && SelectedVCenter != null && _currentSession != null)
        {
            _overviewRefreshTimer.Start();
            StatusMessage = "Auto-refresh enabled";
        }
        else
        {
            _overviewRefreshTimer.Stop();
            StatusMessage = "Auto-refresh disabled";
        }
        
        _logger.LogInformation("Overview auto-refresh {Status} for vCenter: {VCenterName}", 
            AutoRefreshEnabled ? "enabled" : "disabled", SelectedVCenter?.Name);
    }

    /// <summary>
    /// Stop overview refresh when switching vCenters
    /// </summary>
    public void StopRefresh()
    {
        _overviewRefreshTimer.Stop();
        _currentSession = null;
    }

    /// <summary>
    /// Dispose resources
    /// </summary>
    public void Dispose()
    {
        _overviewRefreshTimer?.Stop();
        _overviewRefreshTimer?.Dispose();
    }
} 