using VMwareGUITools.Core.Models;

namespace VMwareGUITools.Infrastructure.VMware;

/// <summary>
/// Interface for VMware vCenter connection and discovery operations
/// </summary>
public interface IVMwareConnectionService
{
    /// <summary>
    /// Tests the connection to a vCenter server
    /// </summary>
    /// <param name="vcenterUrl">The vCenter server URL</param>
    /// <param name="username">Username for authentication</param>
    /// <param name="password">Password for authentication</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Connection test result</returns>
    Task<VCenterConnectionResult> TestConnectionAsync(string vcenterUrl, string username, string password, CancellationToken cancellationToken = default);

    /// <summary>
    /// Establishes a connection to a vCenter server and returns connection information
    /// </summary>
    /// <param name="vCenter">The vCenter configuration</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Connection session information</returns>
    Task<VMwareSession> ConnectAsync(VCenter vCenter, CancellationToken cancellationToken = default);

    /// <summary>
    /// Disconnects from a vCenter server
    /// </summary>
    /// <param name="session">The VMware session to disconnect</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task DisconnectAsync(VMwareSession session, CancellationToken cancellationToken = default);

    /// <summary>
    /// Discovers clusters in the connected vCenter
    /// </summary>
    /// <param name="session">The active VMware session</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of discovered clusters</returns>
    Task<List<ClusterInfo>> DiscoverClustersAsync(VMwareSession session, CancellationToken cancellationToken = default);

    /// <summary>
    /// Discovers hosts in a specific cluster
    /// </summary>
    /// <param name="session">The active VMware session</param>
    /// <param name="clusterMoId">The cluster managed object ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of discovered hosts</returns>
    Task<List<HostInfo>> DiscoverHostsAsync(VMwareSession session, string clusterMoId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves detailed information about a specific host
    /// </summary>
    /// <param name="session">The active VMware session</param>
    /// <param name="hostMoId">The host managed object ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Detailed host information</returns>
    Task<HostDetailInfo> GetHostDetailsAsync(VMwareSession session, string hostMoId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if PowerCLI is installed and available
    /// </summary>
    /// <returns>True if PowerCLI is available</returns>
    Task<bool> IsPowerCLIAvailableAsync();

    /// <summary>
    /// Gets the version information of the connected vCenter
    /// </summary>
    /// <param name="session">The active VMware session</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>vCenter version information</returns>
    Task<VCenterVersionInfo> GetVCenterVersionAsync(VMwareSession session, CancellationToken cancellationToken = default);

    /// <summary>
    /// Test connection to a vCenter server with lightweight health check
    /// </summary>
    Task<bool> TestConnectionHealthAsync(VCenter vCenter, CancellationToken cancellationToken = default);
}

/// <summary>
/// Represents the result of a vCenter connection test
/// </summary>
public class VCenterConnectionResult
{
    public bool IsSuccessful { get; set; }
    public string? ErrorMessage { get; set; }
    public TimeSpan ResponseTime { get; set; }
    public VCenterVersionInfo? VersionInfo { get; set; }
}

/// <summary>
/// Represents an active VMware session
/// </summary>
public class VMwareSession
{
    public string SessionId { get; set; } = string.Empty;
    public string VCenterUrl { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastActivity { get; set; } = DateTime.UtcNow;
    public bool IsActive { get; set; } = true;
    public VCenterVersionInfo? VersionInfo { get; set; }
}

/// <summary>
/// Contains information about a discovered cluster
/// </summary>
public class ClusterInfo
{
    public string Name { get; set; } = string.Empty;
    public string MoId { get; set; } = string.Empty;
    public int HostCount { get; set; }
    public string? ParentFolder { get; set; }
    public bool DrsEnabled { get; set; }
    public bool HaEnabled { get; set; }
    public bool VsanEnabled { get; set; }
}

/// <summary>
/// Contains information about a discovered host
/// </summary>
public class HostInfo
{
    public string Name { get; set; } = string.Empty;
    public string MoId { get; set; } = string.Empty;
    public string IpAddress { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string PowerState { get; set; } = string.Empty;
    public string ConnectionState { get; set; } = string.Empty;
    public bool InMaintenanceMode { get; set; }
    public HostType Type { get; set; } = HostType.Standard;
}

/// <summary>
/// Contains detailed information about a host
/// </summary>
public class HostDetailInfo : HostInfo
{
    public string Vendor { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string ProcessorType { get; set; } = string.Empty;
    public long MemorySize { get; set; }
    public int CpuCores { get; set; }
    public List<string> NetworkAdapters { get; set; } = new List<string>();
    public List<string> StorageAdapters { get; set; } = new List<string>();
    public bool SshEnabled { get; set; }
    public Dictionary<string, object> CustomAttributes { get; set; } = new Dictionary<string, object>();
}

/// <summary>
/// Contains information about a discovered datastore
/// </summary>
public class DatastoreInfo
{
    public string Name { get; set; } = string.Empty;
    public string MoId { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty; // VMFS, NFS, vSAN, etc.
    public long CapacityMB { get; set; }
    public long FreeMB { get; set; }
    public bool Accessible { get; set; } = true;
    public string Url { get; set; } = string.Empty;
    public bool MaintenanceMode { get; set; }
    public List<string> HostNames { get; set; } = new List<string>();
    
    /// <summary>
    /// Gets the used space in MB
    /// </summary>
    public long UsedMB => CapacityMB - FreeMB;

    /// <summary>
    /// Gets the usage percentage
    /// </summary>
    public double UsagePercentage => CapacityMB > 0 ? (double)UsedMB / CapacityMB * 100 : 0;

    /// <summary>
    /// Gets a formatted capacity string
    /// </summary>
    public string FormattedCapacity => FormatBytes(CapacityMB * 1024 * 1024);

    /// <summary>
    /// Gets a formatted free space string
    /// </summary>
    public string FormattedFreeSpace => FormatBytes(FreeMB * 1024 * 1024);

    /// <summary>
    /// Gets a formatted used space string
    /// </summary>
    public string FormattedUsedSpace => FormatBytes(UsedMB * 1024 * 1024);

    private static string FormatBytes(long bytes)
    {
        string[] suffixes = { "B", "KB", "MB", "GB", "TB" };
        int counter = 0;
        decimal number = bytes;
        while (Math.Round(number / 1024) >= 1)
        {
            number /= 1024;
            counter++;
        }
        return $"{number:n1} {suffixes[counter]}";
    }
}

/// <summary>
/// Contains vCenter version information
/// </summary>
public class VCenterVersionInfo
{
    public string Version { get; set; } = string.Empty;
    public string Build { get; set; } = string.Empty;
    public string ApiVersion { get; set; } = string.Empty;
    public string InstanceUuid { get; set; } = string.Empty;
    public string ProductName { get; set; } = string.Empty;
} 