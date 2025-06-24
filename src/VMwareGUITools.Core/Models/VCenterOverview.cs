namespace VMwareGUITools.Core.Models;

/// <summary>
/// Represents overview statistics for a vCenter Server
/// </summary>
public class VCenterOverview
{
    /// <summary>
    /// Total number of clusters
    /// </summary>
    public int ClusterCount { get; set; }

    /// <summary>
    /// Total number of hosts across all clusters
    /// </summary>
    public int HostCount { get; set; }

    /// <summary>
    /// Total number of VMs across all hosts
    /// </summary>
    public int VmCount { get; set; }

    /// <summary>
    /// Overall CPU resource usage
    /// </summary>
    public ResourceUsage CpuUsage { get; set; } = new();

    /// <summary>
    /// Overall memory resource usage
    /// </summary>
    public ResourceUsage MemoryUsage { get; set; } = new();

    /// <summary>
    /// Overall storage resource usage
    /// </summary>
    public ResourceUsage StorageUsage { get; set; } = new();

    /// <summary>
    /// List of cluster summaries
    /// </summary>
    public List<ClusterSummary> Clusters { get; set; } = new();

    /// <summary>
    /// Timestamp when this overview was last updated
    /// </summary>
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Represents resource usage statistics
/// </summary>
public class ResourceUsage
{
    /// <summary>
    /// Total capacity in appropriate units (MHz for CPU, MB for memory, GB for storage)
    /// </summary>
    public long TotalCapacity { get; set; }

    /// <summary>
    /// Currently used amount in same units as TotalCapacity
    /// </summary>
    public long UsedCapacity { get; set; }

    /// <summary>
    /// Usage percentage (0-100)
    /// </summary>
    public double UsagePercentage => TotalCapacity > 0 ? (double)UsedCapacity / TotalCapacity * 100 : 0;

    /// <summary>
    /// Available capacity
    /// </summary>
    public long AvailableCapacity => TotalCapacity - UsedCapacity;

    /// <summary>
    /// Unit of measurement (MHz, MB, GB, etc.)
    /// </summary>
    public string Unit { get; set; } = string.Empty;
}

/// <summary>
/// Summary information about a cluster
/// </summary>
public class ClusterSummary
{
    /// <summary>
    /// Cluster name
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Cluster managed object ID
    /// </summary>
    public string MoId { get; set; } = string.Empty;

    /// <summary>
    /// Number of hosts in the cluster
    /// </summary>
    public int HostCount { get; set; }

    /// <summary>
    /// Number of VMs in the cluster
    /// </summary>
    public int VmCount { get; set; }

    /// <summary>
    /// CPU usage for the cluster
    /// </summary>
    public ResourceUsage CpuUsage { get; set; } = new();

    /// <summary>
    /// Memory usage for the cluster
    /// </summary>
    public ResourceUsage MemoryUsage { get; set; } = new();

    /// <summary>
    /// Overall health status
    /// </summary>
    public string HealthStatus { get; set; } = "Unknown";

    /// <summary>
    /// Whether DRS is enabled
    /// </summary>
    public bool DrsEnabled { get; set; }

    /// <summary>
    /// Whether HA is enabled
    /// </summary>
    public bool HaEnabled { get; set; }

    /// <summary>
    /// Whether vSAN is enabled
    /// </summary>
    public bool VsanEnabled { get; set; }
} 