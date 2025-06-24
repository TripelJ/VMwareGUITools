using System.Collections.ObjectModel;
using System.Timers;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using VMwareGUITools.Core.Models;
using VMwareGUITools.Infrastructure.VMware;

namespace VMwareGUITools.UI.ViewModels;

/// <summary>
/// View model for the infrastructure tree showing clusters, hosts, and datastores from live REST API data
/// </summary>
public partial class InfrastructureViewModel : ObservableObject, IDisposable
{
    private readonly ILogger<InfrastructureViewModel> _logger;
    private readonly IVSphereRestAPIService _restApiService;
    private readonly System.Timers.Timer _refreshTimer;

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

    public InfrastructureViewModel(ILogger<InfrastructureViewModel> logger, IVSphereRestAPIService restApiService)
    {
        _logger = logger;
        _restApiService = restApiService;

        // Setup auto-refresh timer (30 seconds)
        _refreshTimer = new System.Timers.Timer(30000);
        _refreshTimer.Elapsed += async (sender, e) => await AutoRefreshAsync();
        _refreshTimer.AutoReset = true;
    }

    /// <summary>
    /// Load live infrastructure data for the specified vCenter using REST API
    /// </summary>
    public async Task LoadInfrastructureAsync(VCenter? vCenter)
    {
        if (vCenter == null)
        {
            InfrastructureItems.Clear();
            _refreshTimer.Stop();
            StatusMessage = "No vCenter selected";
            return;
        }

        try
        {
            IsLoading = true;
            CurrentVCenter = vCenter;
            StatusMessage = "Loading infrastructure...";

            _logger.LogInformation("Loading live infrastructure data for vCenter: {VCenterName}", vCenter.Name);

            // Establish REST API session
            var session = await _restApiService.ConnectAsync(vCenter);

            // Clear existing items
            InfrastructureItems.Clear();

            // Get live data from REST API
            var clustersTask = _restApiService.DiscoverClustersAsync(session);
            var datastoresTask = _restApiService.DiscoverDatastoresAsync(session);

            await Task.WhenAll(clustersTask, datastoresTask);

            var clusters = await clustersTask;
            var datastores = await datastoresTask;

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
                    var hosts = await _restApiService.DiscoverHostsAsync(session, cluster.MoId);
                    
                    foreach (var host in hosts.OrderBy(h => h.Name))
                    {
                        var hostItem = new InfrastructureItemViewModel
                        {
                            Name = host.Name,
                            Type = InfrastructureItemType.Host,
                            Icon = "Server",
                            ToolTip = $"Host: {host.Name} - State: {host.ConnectionState}, Power: {host.PowerState}{(host.InMaintenanceMode ? " (Maintenance)" : "")}",
                            Data = host,
                            Parent = clusterItem,
                            StatusColor = GetHostStatusColor(host)
                        };

                        clusterItem.Children.Add(hostItem);
                    }

                    // Update cluster tooltip with actual host count
                    clusterItem.ToolTip = $"Cluster: {cluster.Name} ({hosts.Count} hosts) - DRS: {(cluster.DrsEnabled ? "Enabled" : "Disabled")}, HA: {(cluster.HaEnabled ? "Enabled" : "Disabled")}";
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to load hosts for cluster {ClusterName}", cluster.Name);
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
                    ToolTip = $"Datastores ({datastores.Count})",
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
                        Parent = datastoresSection,
                        StatusColor = GetDatastoreStatusColor(datastore.UsagePercentage)
                    };

                    datastoresSection.Children.Add(datastoreItem);
                }

                InfrastructureItems.Add(datastoresSection);
            }

            // Successfully loaded infrastructure data
            LastUpdated = DateTime.Now;
            StatusMessage = $"Infrastructure loaded successfully at {LastUpdated:HH:mm:ss} ({clusters.Count} clusters, {datastores.Count} datastores)";
            
            // Start auto-refresh timer
            _refreshTimer.Start();

            _logger.LogInformation("Loaded live infrastructure data: {ClusterCount} clusters, {DatastoreCount} datastores for vCenter: {VCenterName}", 
                clusters.Count, datastores.Count, vCenter.Name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load live infrastructure data for vCenter: {VCenterName}", vCenter?.Name);
            StatusMessage = $"Failed to load infrastructure: {ex.Message}";
            _refreshTimer.Stop();
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Auto-refresh handler that runs every 30 seconds
    /// </summary>
    private async Task AutoRefreshAsync()
    {
        if (CurrentVCenter != null && !IsLoading)
        {
            try
            {
                _logger.LogDebug("Auto-refreshing infrastructure data for vCenter: {VCenterName}", CurrentVCenter.Name);
                await LoadInfrastructureAsync(CurrentVCenter);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Auto-refresh failed for vCenter: {VCenterName}", CurrentVCenter?.Name);
                StatusMessage = $"Auto-refresh failed at {DateTime.Now:HH:mm:ss}: {ex.Message}";
            }
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
            _refreshTimer.Stop(); // Stop auto-refresh during manual refresh
            await LoadInfrastructureAsync(CurrentVCenter);
            // Timer will be restarted in LoadInfrastructureAsync if successful
        }
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
        
        return host.ConnectionState.ToLower() switch
        {
            "connected" when host.PowerState.ToLower() == "poweredon" => "#4CAF50", // Green - healthy
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
    /// Dispose resources
    /// </summary>
    public void Dispose()
    {
        _refreshTimer?.Stop();
        _refreshTimer?.Dispose();
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
    private object? _data;

    public InfrastructureItemViewModel? Parent { get; set; }

    public ObservableCollection<InfrastructureItemViewModel> Children { get; } = new();

    /// <summary>
    /// Gets whether this item has children
    /// </summary>
    public bool HasChildren => Children.Any();
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