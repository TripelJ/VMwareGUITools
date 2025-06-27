using VMware.Vim;
using VMwareGUITools.Core.Models;

namespace VMwareGUITools.Infrastructure.VMware;

/// <summary>
/// Interface for vSphere SDK operations using managed objects
/// </summary>
public interface IVSphereSDKService
{
    /// <summary>
    /// Establishes a connection to vCenter using the vSphere SDK
    /// </summary>
    Task<VimClientConnection> ConnectAsync(VCenter vCenter, CancellationToken cancellationToken = default);

    /// <summary>
    /// Disconnects from vCenter and cleans up resources
    /// </summary>
    Task DisconnectAsync(VimClientConnection connection, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets detailed iSCSI path information for a host using HostStorageSystem managed objects
    /// </summary>
    Task<ISCSIPathResult> GetISCSIPathStatusAsync(VimClientConnection connection, string hostMoId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets storage adapter information for a host
    /// </summary>
    Task<List<StorageAdapterInfo>> GetStorageAdaptersAsync(VimClientConnection connection, string hostMoId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets multipath information for all LUNs on a host
    /// </summary>
    Task<List<MultipathInfo>> GetMultipathInfoAsync(VimClientConnection connection, string hostMoId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Represents a connection to vCenter using the vSphere SDK
/// </summary>
public class VimClientConnection
{
    public VimClientImpl VimClient { get; set; } = null!;
    public ServiceContent ServiceContent { get; set; } = null!;
    public string SessionId { get; set; } = string.Empty;
    public DateTime ConnectedAt { get; set; }
    public VCenter VCenter { get; set; } = null!;
}

/// <summary>
/// Result of iSCSI path status check
/// </summary>
public class ISCSIPathResult
{
    public bool IsSuccess { get; set; }
    public string HostName { get; set; } = string.Empty;
    public string HostMoId { get; set; } = string.Empty;
    public List<ISCSIAdapterInfo> ISCSIAdapters { get; set; } = new();
    public int TotalPaths { get; set; }
    public int ActivePaths { get; set; }
    public int DeadPaths { get; set; }
    public int StandbyPaths { get; set; }
    public List<PathInfo> PathDetails { get; set; } = new();
    public string? ErrorMessage { get; set; }
    public DateTime CheckedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Information about an iSCSI adapter
/// </summary>
public class ISCSIAdapterInfo
{
    public string AdapterName { get; set; } = string.Empty;
    public string HbaType { get; set; } = string.Empty;
    public bool IsOnline { get; set; }
    public List<string> PortalAddresses { get; set; } = new();
    public List<PathInfo> Paths { get; set; } = new();
}

/// <summary>
/// Information about a storage path
/// </summary>
public class PathInfo
{
    public string PathName { get; set; } = string.Empty;
    public string Adapter { get; set; } = string.Empty;
    public string LunUuid { get; set; } = string.Empty;
    public string PathState { get; set; } = string.Empty;
    public string PathStatus { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public string Target { get; set; } = string.Empty;
    public int Lun { get; set; }
    public string Transport { get; set; } = string.Empty;
}

/// <summary>
/// Information about a storage adapter
/// </summary>
public class StorageAdapterInfo
{
    public string Key { get; set; } = string.Empty;
    public string Device { get; set; } = string.Empty;
    public string Bus { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string Driver { get; set; } = string.Empty;
    public string[] PciId { get; set; } = Array.Empty<string>();
}

/// <summary>
/// Multipath information for a LUN
/// </summary>
public class MultipathInfo
{
    public string LunKey { get; set; } = string.Empty;
    public string LunUuid { get; set; } = string.Empty;
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string PolicyName { get; set; } = string.Empty;
    public List<PathInfo> Paths { get; set; } = new();
} 