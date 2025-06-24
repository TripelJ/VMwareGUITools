using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using VMwareGUITools.Core.Models;
using VMwareGUITools.Data;

namespace VMwareGUITools.UI.ViewModels;

/// <summary>
/// View model for the infrastructure tree showing clusters, hosts, and datastores
/// </summary>
public partial class InfrastructureViewModel : ObservableObject
{
    private readonly ILogger<InfrastructureViewModel> _logger;
    private readonly VMwareDbContext _context;

    [ObservableProperty]
    private ObservableCollection<InfrastructureItemViewModel> _infrastructureItems = new();

    [ObservableProperty]
    private InfrastructureItemViewModel? _selectedItem;

    [ObservableProperty]
    private bool _isLoading = false;

    [ObservableProperty]
    private VCenter? _currentVCenter;

    public InfrastructureViewModel(ILogger<InfrastructureViewModel> logger, VMwareDbContext context)
    {
        _logger = logger;
        _context = context;
    }

    /// <summary>
    /// Load infrastructure data for the specified vCenter
    /// </summary>
    public async Task LoadInfrastructureAsync(VCenter? vCenter)
    {
        if (vCenter == null)
        {
            InfrastructureItems.Clear();
            return;
        }

        try
        {
            IsLoading = true;
            CurrentVCenter = vCenter;

            _logger.LogInformation("Loading infrastructure for vCenter: {VCenterName}", vCenter.Name);

            // Load clusters with their hosts
            var clusters = await _context.Clusters
                .Include(c => c.Hosts)
                .Where(c => c.VCenterId == vCenter.Id && c.Enabled)
                .OrderBy(c => c.Name)
                .ToListAsync();

            // Load datastores for this vCenter
            var datastores = await _context.Set<Datastore>()
                .Where(d => d.VCenterId == vCenter.Id && d.Accessible)
                .OrderBy(d => d.Name)
                .ToListAsync();

            // Clear existing items
            InfrastructureItems.Clear();

            // Create cluster items with their hosts
            foreach (var cluster in clusters)
            {
                var clusterItem = new InfrastructureItemViewModel
                {
                    Name = cluster.Name,
                    Type = InfrastructureItemType.Cluster,
                    Icon = "Cube",
                    ToolTip = $"Cluster: {cluster.Name} ({cluster.HostCount} hosts)",
                    Data = cluster,
                    IsExpanded = true
                };

                // Add hosts under each cluster
                foreach (var host in cluster.Hosts.Where(h => h.Enabled).OrderBy(h => h.Name))
                {
                    var hostItem = new InfrastructureItemViewModel
                    {
                        Name = host.Name,
                        Type = InfrastructureItemType.Host,
                        Icon = "Server",
                        ToolTip = $"Host: {host.Name} ({host.IpAddress}) - {host.HealthStatus}",
                        Data = host,
                        Parent = clusterItem,
                        StatusColor = GetStatusColor(host.HealthStatus)
                    };

                    clusterItem.Children.Add(hostItem);
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
                    IsExpanded = true
                };

                foreach (var datastore in datastores)
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

            // Successfully loaded infrastructure data - update connection status
            if (clusters.Any() || datastores.Any())
            {
                vCenter.UpdateConnectionStatus(true);
            }

            _logger.LogInformation("Loaded {ClusterCount} clusters and {DatastoreCount} datastores for vCenter: {VCenterName}", 
                clusters.Count, datastores.Count, vCenter.Name);
        }
        catch (Exception ex)
        {
            vCenter?.UpdateConnectionStatus(false);
            _logger.LogError(ex, "Failed to load infrastructure for vCenter: {VCenterName}", vCenter?.Name);
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Refresh the infrastructure data
    /// </summary>
    [RelayCommand]
    private async Task RefreshAsync()
    {
        await LoadInfrastructureAsync(CurrentVCenter);
    }

    /// <summary>
    /// Get status color based on health status
    /// </summary>
    private static string GetStatusColor(HealthStatus status)
    {
        return status switch
        {
            HealthStatus.Healthy => "#4CAF50", // Green
            HealthStatus.Warning => "#FF9800", // Orange
            HealthStatus.Critical => "#F44336", // Red
            HealthStatus.Stale => "#9E9E9E", // Gray
            _ => "#2196F3" // Blue (Unknown)
        };
    }

    /// <summary>
    /// Get status color based on datastore usage
    /// </summary>
    private static string GetDatastoreStatusColor(double usagePercentage)
    {
        return usagePercentage switch
        {
            >= 90 => "#F44336", // Red
            >= 80 => "#FF9800", // Orange
            >= 70 => "#FFC107", // Amber
            _ => "#4CAF50" // Green
        };
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