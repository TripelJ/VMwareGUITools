using System.Collections.ObjectModel;
using System.Timers;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using VMwareGUITools.Core.Models;
using VMwareGUITools.Infrastructure.Services;
using System.Text.Json;

namespace VMwareGUITools.UI.ViewModels;

/// <summary>
/// View model for the vCenter overview tab showing summary information and resource usage
/// </summary>
public partial class VCenterOverviewViewModel : ObservableObject, IDisposable
{
    private readonly ILogger<VCenterOverviewViewModel> _logger;
    private readonly IServiceConfigurationManager _serviceConfigurationManager;
    private readonly System.Timers.Timer _overviewRefreshTimer;

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
        IServiceConfigurationManager serviceConfigurationManager)
    {
        _logger = logger;
        _serviceConfigurationManager = serviceConfigurationManager;

        // Setup auto-refresh timer for overview data (every 30 seconds)
        _overviewRefreshTimer = new System.Timers.Timer(30000);
        _overviewRefreshTimer.Elapsed += async (sender, e) => await AutoRefreshOverviewAsync();
        _overviewRefreshTimer.AutoReset = true;
    }

    /// <summary>
    /// Loads overview data for the specified vCenter via Windows Service
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
            
            // Stop any previous refresh timer
            _overviewRefreshTimer.Stop();
            
            // Clear previous data to ensure fresh display
            OverviewData = null;
            
            SelectedVCenter = vCenter;

            _logger.LogInformation("Loading overview data for vCenter: {VCenterName} via Windows Service", vCenter.Name);

            // Check if service is running
            var serviceStatus = await _serviceConfigurationManager.GetServiceStatusAsync();
            if (serviceStatus == null || serviceStatus.LastHeartbeat < DateTime.UtcNow.AddSeconds(-30))
            {
                StatusMessage = "Windows Service is not running. Cannot load overview data.";
                return;
            }

            // Send command to service
            var parameters = new { VCenterId = vCenter.Id };
            var commandId = await _serviceConfigurationManager.SendCommandAsync(
                ServiceCommandTypes.GetOverviewData, 
                parameters);

            StatusMessage = $"Overview request sent to service (ID: {commandId}). Loading...";

            // Monitor for completion
            var overviewData = await MonitorCommandCompletionAsync<VCenterOverview>(commandId, "Overview data");
            
            if (overviewData != null)
            {
                // Update UI on dispatcher thread
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    OverviewData = overviewData;
                    LastUpdated = DateTime.Now;
                    StatusMessage = $"Overview loaded successfully at {LastUpdated:HH:mm:ss}";
                });

                // Start auto-refresh if enabled
                if (AutoRefreshEnabled)
                {
                    _overviewRefreshTimer.Start();
                }

                _logger.LogInformation("Overview data loaded successfully for vCenter: {VCenterName} via Windows Service", 
                    vCenter.Name);
            }
        }
        catch (Exception ex)
        {
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
    /// Auto-refresh overview data via Windows Service
    /// </summary>
    private async Task AutoRefreshOverviewAsync()
    {
        if (SelectedVCenter == null || IsLoading || !AutoRefreshEnabled)
            return;

        try
        {
            _logger.LogDebug("Auto-refreshing overview data for vCenter: {VCenterName} via Windows Service", SelectedVCenter.Name);

            // Check if service is running
            var serviceStatus = await _serviceConfigurationManager.GetServiceStatusAsync();
            if (serviceStatus == null || serviceStatus.LastHeartbeat < DateTime.UtcNow.AddSeconds(-30))
            {
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    StatusMessage = "Windows Service is not running - auto-refresh disabled";
                });
                _overviewRefreshTimer.Stop();
                return;
            }

            // Send command to service
            var parameters = new { VCenterId = SelectedVCenter.Id };
            var commandId = await _serviceConfigurationManager.SendCommandAsync(
                ServiceCommandTypes.GetOverviewData, 
                parameters);

            // Monitor for completion
            var overviewData = await MonitorCommandCompletionAsync<VCenterOverview>(commandId, "Overview auto-refresh");
            
            if (overviewData != null)
            {
                // Update UI on dispatcher thread
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    OverviewData = overviewData;
                    LastUpdated = DateTime.Now;
                    // Don't update status message during auto-refresh to avoid UI noise
                });

                _logger.LogDebug("Auto-refresh completed for vCenter: {VCenterName} via Windows Service", 
                    SelectedVCenter.Name);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Auto-refresh failed for vCenter overview: {VCenterName}", SelectedVCenter?.Name);
            
            // Stop auto-refresh on repeated failures
            _overviewRefreshTimer.Stop();
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                StatusMessage = $"Auto-refresh failed - disabled";
            });
        }
    }

    /// <summary>
    /// Monitors a service command for completion and returns the deserialized result
    /// </summary>
    private async Task<T?> MonitorCommandCompletionAsync<T>(string commandId, string operationName) where T : class
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
                    try
                    {
                        if (!string.IsNullOrEmpty(command.Result))
                        {
                            return JsonSerializer.Deserialize<T>(command.Result);
                        }
                    }
                    catch (JsonException ex)
                    {
                        _logger.LogError(ex, "Failed to deserialize {OperationName} result", operationName);
                        throw new InvalidOperationException($"Failed to parse {operationName} response");
                    }
                    return null;
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
        
        if (AutoRefreshEnabled && SelectedVCenter != null)
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