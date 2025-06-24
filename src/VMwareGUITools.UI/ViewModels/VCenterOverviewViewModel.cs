using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using VMwareGUITools.Core.Models;
using VMwareGUITools.Infrastructure.VMware;

namespace VMwareGUITools.UI.ViewModels;

/// <summary>
/// View model for the vCenter overview tab showing summary information and resource usage
/// </summary>
public partial class VCenterOverviewViewModel : ObservableObject
{
    private readonly ILogger<VCenterOverviewViewModel> _logger;
    private readonly IVMwareConnectionService _vmwareService;
    private readonly IVSphereRestAPIService _restApiService;

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

    public VCenterOverviewViewModel(
        ILogger<VCenterOverviewViewModel> logger,
        IVMwareConnectionService vmwareService,
        IVSphereRestAPIService restApiService)
    {
        _logger = logger;
        _vmwareService = vmwareService;
        _restApiService = restApiService;
    }

    /// <summary>
    /// Loads overview data for the specified vCenter
    /// </summary>
    public async Task LoadOverviewDataAsync(VCenter vCenter)
    {
        if (vCenter == null)
        {
            OverviewData = null;
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
            var restSession = new VSphereSession
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

            // Get overview data
            OverviewData = await _restApiService.GetOverviewDataAsync(restSession);
            LastUpdated = DateTime.Now;
            StatusMessage = $"Overview loaded successfully at {LastUpdated:HH:mm:ss}";

            _logger.LogInformation("Overview data loaded successfully for vCenter: {VCenterName}", vCenter.Name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load overview data for vCenter: {VCenterName}", vCenter?.Name);
            StatusMessage = $"Failed to load overview data: {ex.Message}";
            OverviewData = null;
        }
        finally
        {
            IsLoading = false;
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


} 