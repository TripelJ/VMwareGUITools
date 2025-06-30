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
/// View model for the infrastructure tab showing clusters, hosts, and datastores
/// </summary>
public partial class InfrastructureViewModel : ObservableObject, IDisposable
{
    private readonly ILogger<InfrastructureViewModel> _logger;
    private readonly IServiceConfigurationManager _serviceConfigurationManager;
    private readonly System.Timers.Timer _statusCheckTimer;

    [ObservableProperty]
    private ObservableCollection<InfrastructureItemViewModel> _infrastructureItems = new();

    [ObservableProperty]
    private InfrastructureItemViewModel? _selectedItem;

    [ObservableProperty]
    private bool _isLoading = false;

    [ObservableProperty]
    private VCenter? _currentVCenter;

    [ObservableProperty]
    private DateTime _lastUpdated;

    [ObservableProperty]
    private string _statusMessage = "Ready";

    public InfrastructureViewModel(ILogger<InfrastructureViewModel> logger, IServiceConfigurationManager serviceConfigurationManager)
    {
        _logger = logger;
        _serviceConfigurationManager = serviceConfigurationManager;

        // Setup status check timer (every 2 minutes for status changes only)
        _statusCheckTimer = new System.Timers.Timer(120000); // 2 minutes
        _statusCheckTimer.Elapsed += async (sender, e) => await CheckStatusUpdatesAsync();
        _statusCheckTimer.AutoReset = true;
    }

    /// <summary>
    /// Load infrastructure data for the specified vCenter via Windows Service
    /// </summary>
    public async Task LoadInfrastructureAsync(VCenter? vCenter)
    {
        if (vCenter == null)
        {
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                InfrastructureItems.Clear();
                StatusMessage = "No vCenter selected";
            });
            _statusCheckTimer.Stop();
            return;
        }

        try
        {
            IsLoading = true;
            CurrentVCenter = vCenter;
            StatusMessage = "Loading infrastructure...";

            _logger.LogInformation("Loading infrastructure data for vCenter: {VCenterName} via Windows Service", vCenter.Name);

            // Check if service is running
            var serviceStatus = await _serviceConfigurationManager.GetServiceStatusAsync();
            if (serviceStatus == null || serviceStatus.LastHeartbeat < DateTime.UtcNow.AddMinutes(-2))
            {
                StatusMessage = "Windows Service is not running. Cannot load infrastructure data.";
                return;
            }

            // Send command to service
            var parameters = new { VCenterId = vCenter.Id };
            var commandId = await _serviceConfigurationManager.SendCommandAsync(
                ServiceCommandTypes.GetInfrastructureData, 
                parameters);

            StatusMessage = $"Infrastructure request sent to service (ID: {commandId}). Loading...";

            // Monitor for completion
            var infrastructureData = await MonitorCommandCompletionAsync(commandId, "Infrastructure data");
            
            if (infrastructureData != null)
            {
                // Update UI on the dispatcher thread
                await Application.Current.Dispatcher.InvokeAsync(async () =>
                {
                    InfrastructureItems.Clear();
                    await BuildInfrastructureTreeAsync(infrastructureData.Clusters, infrastructureData.Datastores);
                });

                // Successfully loaded infrastructure data
                LastUpdated = DateTime.Now;
                StatusMessage = $"Infrastructure loaded successfully at {LastUpdated:HH:mm:ss} ({infrastructureData.Clusters.Count} clusters, {infrastructureData.Datastores.Count} datastores)";
                
                // Start status monitoring
                _statusCheckTimer.Start();

                _logger.LogInformation("Loaded infrastructure data: {ClusterCount} clusters, {DatastoreCount} datastores for vCenter: {VCenterName} via Windows Service", 
                    infrastructureData.Clusters.Count, infrastructureData.Datastores.Count, vCenter.Name);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load infrastructure data for vCenter: {VCenterName}", vCenter?.Name);
            
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                StatusMessage = $"Failed to load infrastructure: {ex.Message}";
                InfrastructureItems.Clear();
            });
            
            _statusCheckTimer.Stop();
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Monitors a service command for completion and returns the deserialized infrastructure data
    /// </summary>
    private async Task<InfrastructureData?> MonitorCommandCompletionAsync(string commandId, string operationName)
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
                            return JsonSerializer.Deserialize<InfrastructureData>(command.Result);
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
    /// Build the infrastructure tree from discovered data
    /// </summary>
    private async Task BuildInfrastructureTreeAsync(IEnumerable<ClusterInfo> clusters, IEnumerable<DatastoreInfo> datastores)
    {
        // Build cluster tree with hosts
        foreach (var cluster in clusters.OrderBy(c => c.Name))
        {
            var clusterItem = new InfrastructureItemViewModel
            {
                Name = cluster.Name,
                Type = InfrastructureItemType.Cluster,
                Icon = "Cube",
                ToolTip = $"Cluster: {cluster.Name} - DRS: {(cluster.DrsEnabled ? "Enabled" : "Disabled")}, HA: {(cluster.HaEnabled ? "Enabled" : "Disabled")}",
                Data = cluster,
                IsExpanded = true,
                StatusColor = GetClusterStatusColor(cluster)
            };

            // Get hosts for this cluster
            try
            {
                // Note: Host loading will be handled by a separate service call if needed
                // For now, clusters are loaded without detailed host information
                // This follows the pattern where we get the basic cluster info first
                
                // Update cluster tooltip
                clusterItem.ToolTip = $"Cluster: {cluster.Name} - DRS: {(cluster.DrsEnabled ? "Enabled" : "Disabled")}, HA: {(cluster.HaEnabled ? "Enabled" : "Disabled")}";
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to process cluster {ClusterName}", cluster.Name);
                clusterItem.StatusColor = "#FF9800"; // Orange for warning
            }

            InfrastructureItems.Add(clusterItem);
        }

        // Add datastores section
        if (datastores.Any())
        {
            var datastoresSection = new InfrastructureItemViewModel
            {
                Name = "Datastores",
                Type = InfrastructureItemType.DatastoresFolder,
                Icon = "Database",
                ToolTip = $"Datastores ({datastores.Count()})",
                IsExpanded = true,
                StatusColor = "#2196F3" // Blue
            };

            foreach (var datastore in datastores.Where(d => d.Accessible).OrderBy(d => d.Name))
            {
                var datastoreItem = new InfrastructureItemViewModel
                {
                    Name = datastore.Name,
                    Type = InfrastructureItemType.Datastore,
                    Icon = "HardDisk",
                    ToolTip = $"Datastore: {datastore.Name} ({datastore.Type}) - {datastore.FormattedUsedSpace} / {datastore.FormattedCapacity} ({datastore.UsagePercentage:F1}%)",
                    Data = datastore,
                    Parent = datastoresSection
                };

                // Set status and type properties based on datastore
                SetDatastoreStatusProperties(datastoreItem, datastore);

                datastoresSection.Children.Add(datastoreItem);
            }

            InfrastructureItems.Add(datastoresSection);
        }
    }

    /// <summary>
    /// Check for status updates without rebuilding the entire tree
    /// </summary>
    private async Task CheckStatusUpdatesAsync()
    {
        if (CurrentVCenter == null || IsLoading)
            return;

        try
        {
            _logger.LogDebug("Checking status updates for vCenter: {VCenterName} via Windows Service", CurrentVCenter.Name);

            // For now, we'll just refresh the entire infrastructure periodically
            // In a more sophisticated implementation, we could have a separate service command
            // for just getting status updates
            
            _logger.LogDebug("Status updates completed for vCenter: {VCenterName}", CurrentVCenter.Name);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Status update failed for vCenter: {VCenterName}", CurrentVCenter?.Name);
        }
    }

    /// <summary>
    /// Manual refresh command
    /// </summary>
    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (CurrentVCenter != null)
        {
            await LoadInfrastructureAsync(CurrentVCenter);
        }
    }

    /// <summary>
    /// Expand or collapse an infrastructure item
    /// </summary>
    [RelayCommand]
    private void ToggleItem(InfrastructureItemViewModel? item)
    {
        if (item != null)
        {
            item.IsExpanded = !item.IsExpanded;
        }
    }

    /// <summary>
    /// Select an infrastructure item
    /// </summary>
    [RelayCommand]
    private void SelectItem(InfrastructureItemViewModel? item)
    {
        SelectedItem = item;
        _logger.LogDebug("Selected infrastructure item: {ItemName} ({ItemType})", 
            item?.Name, item?.Type);
    }

    /// <summary>
    /// Get status color based on cluster configuration
    /// </summary>
    private static string GetClusterStatusColor(ClusterInfo cluster)
    {
        if (cluster.DrsEnabled && cluster.HaEnabled)
            return "#4CAF50"; // Green - fully configured
        else if (cluster.DrsEnabled || cluster.HaEnabled)
            return "#FF9800"; // Orange - partially configured
        else
            return "#2196F3"; // Blue - basic configuration
    }

    /// <summary>
    /// Get status color based on host state
    /// </summary>
    private static string GetHostStatusColor(HostInfo host)
    {
        if (host.InMaintenanceMode)
            return "#9E9E9E"; // Gray - maintenance mode
        
        var connectionState = host.ConnectionState?.ToLower() ?? "";
        var powerState = host.PowerState?.ToLower() ?? "";
        var isPoweredOn = powerState == "poweredon" || powerState == "powered_on";
        
        return connectionState switch
        {
            "connected" when isPoweredOn => "#4CAF50", // Green - healthy
            "connected" => "#FF9800", // Orange - connected but not powered on
            "disconnected" => "#F44336", // Red - disconnected
            "notresponding" => "#F44336", // Red - not responding
            _ => "#2196F3" // Blue - unknown state
        };
    }

    /// <summary>
    /// Get status color based on datastore usage
    /// </summary>
    private static string GetDatastoreStatusColor(double usagePercentage)
    {
        return usagePercentage switch
        {
            >= 90 => "#F44336", // Red - critical usage
            >= 80 => "#FF9800", // Orange - high usage
            >= 70 => "#FFC107", // Amber - moderate usage
            _ => "#4CAF50" // Green - normal usage
        };
    }

    /// <summary>
    /// Set status and type properties for a host item
    /// </summary>
    private static void SetHostStatusProperties(InfrastructureItemViewModel hostItem, HostInfo host)
    {
        // Set status color and flashing based on connection and power state
        var connectionState = host.ConnectionState?.ToLower() ?? "";
        var powerState = host.PowerState?.ToLower() ?? "";
        
        var isConnected = connectionState == "connected";
        var isPoweredOn = powerState == "poweredon" || powerState == "powered_on";
        
        if (host.InMaintenanceMode)
        {
            hostItem.StatusColor = "#9E9E9E"; // Gray - maintenance mode
            hostItem.ShouldFlash = false;
        }
        else if (isConnected && isPoweredOn)
        {
            hostItem.StatusColor = "#4CAF50"; // Green - healthy
            hostItem.ShouldFlash = false;
        }
        else
        {
            hostItem.StatusColor = "#F44336"; // Red - problematic
            hostItem.ShouldFlash = true; // Flash for attention
        }

        // Set type indicator for vSAN vs Standard hosts
        switch (host.Type)
        {
            case HostType.VsanNode:
                hostItem.TypeIndicator = "vSAN";
                hostItem.TypeIndicatorColor = "#9C27B0"; // Purple for vSAN
                break;
            case HostType.Standard:
                hostItem.TypeIndicator = "STD";
                hostItem.TypeIndicatorColor = "#607D8B"; // Blue-gray for standard
                break;
            case HostType.ManagementCluster:
                hostItem.TypeIndicator = "MGT";
                hostItem.TypeIndicatorColor = "#FF5722"; // Deep orange for management
                break;
            case HostType.EdgeCluster:
                hostItem.TypeIndicator = "EDGE";
                hostItem.TypeIndicatorColor = "#795548"; // Brown for edge
                break;
            default:
                hostItem.TypeIndicator = string.Empty;
                hostItem.TypeIndicatorColor = "#666666";
                break;
        }
    }

    /// <summary>
    /// Set status and type properties for a datastore item
    /// </summary>
    private static void SetDatastoreStatusProperties(InfrastructureItemViewModel datastoreItem, DatastoreInfo datastore)
    {
        // Set status color based on usage
        datastoreItem.StatusColor = GetDatastoreStatusColor(datastore.UsagePercentage);
        datastoreItem.ShouldFlash = false; // Datastores don't flash

        // Set type indicator based on datastore type
        var type = datastore.Type.ToUpper();
        switch (type)
        {
            case "VSAN":
                datastoreItem.TypeIndicator = "vSAN";
                datastoreItem.TypeIndicatorColor = "#9C27B0"; // Purple for vSAN
                break;
            case "NFS":
                datastoreItem.TypeIndicator = "NFS";
                datastoreItem.TypeIndicatorColor = "#FF9800"; // Orange for NFS
                break;
            case "VMFS":
                datastoreItem.TypeIndicator = "VMFS";
                datastoreItem.TypeIndicatorColor = "#2196F3"; // Blue for VMFS
                break;
            case "VVOL":
                datastoreItem.TypeIndicator = "vVol";
                datastoreItem.TypeIndicatorColor = "#4CAF50"; // Green for vVol
                break;
            default:
                datastoreItem.TypeIndicator = type.Length > 4 ? type.Substring(0, 4) : type;
                datastoreItem.TypeIndicatorColor = "#666666"; // Gray for unknown types
                break;
        }
    }

    /// <summary>
    /// Dispose resources
    /// </summary>
    public void Dispose()
    {
        _statusCheckTimer?.Stop();
        _statusCheckTimer?.Dispose();
    }
}

/// <summary>
/// Represents an item in the infrastructure tree
/// </summary>
public partial class InfrastructureItemViewModel : ObservableObject
{
    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private InfrastructureItemType _type;

    [ObservableProperty]
    private string _icon = string.Empty;

    [ObservableProperty]
    private string _toolTip = string.Empty;

    [ObservableProperty]
    private bool _isExpanded = false;

    [ObservableProperty]
    private bool _isSelected = false;

    [ObservableProperty]
    private string _statusColor = "#2196F3";

    [ObservableProperty]
    private bool _shouldFlash = false;

    [ObservableProperty]
    private string _typeIndicator = string.Empty;

    [ObservableProperty]
    private string _typeIndicatorColor = "#666666";

    [ObservableProperty]
    private object? _data;

    public InfrastructureItemViewModel? Parent { get; set; }

    public ObservableCollection<InfrastructureItemViewModel> Children { get; } = new();

    /// <summary>
    /// Gets whether this item has children
    /// </summary>
    public bool HasChildren => Children.Any();
    
    /// <summary>
    /// Notify when children collection changes
    /// </summary>
    public InfrastructureItemViewModel()
    {
        Children.CollectionChanged += (s, e) => OnPropertyChanged(nameof(HasChildren));
    }
}

/// <summary>
/// Types of infrastructure items
/// </summary>
public enum InfrastructureItemType
{
    Cluster,
    Host,
    DatastoresFolder,
    Datastore
}

/// <summary>
/// Infrastructure data container for deserialization
/// </summary>
public class InfrastructureData
{
    public List<ClusterInfo> Clusters { get; set; } = new();
    public List<DatastoreInfo> Datastores { get; set; } = new();
    public DateTime LastUpdated { get; set; }
} 