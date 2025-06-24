using VMwareGUITools.Core.Models;

namespace VMwareGUITools.Infrastructure.VMware;

/// <summary>
/// Interface for vSphere REST API operations
/// </summary>
public interface IVSphereRestAPIService : IDisposable
{
    /// <summary>
    /// Tests connection to a vCenter server using REST API
    /// </summary>
    Task<VCenterConnectionResult> TestConnectionAsync(string vcenterUrl, string username, string password, CancellationToken cancellationToken = default);

    /// <summary>
    /// Establishes a REST API session to vCenter
    /// </summary>
    Task<VSphereSession> ConnectAsync(VCenter vCenter, CancellationToken cancellationToken = default);

    /// <summary>
    /// Disconnects from vCenter
    /// </summary>
    Task DisconnectAsync(VSphereSession session, CancellationToken cancellationToken = default);

    /// <summary>
    /// Discovers clusters using REST API
    /// </summary>
    Task<List<ClusterInfo>> DiscoverClustersAsync(VSphereSession session, CancellationToken cancellationToken = default);

    /// <summary>
    /// Discovers hosts in a cluster using REST API
    /// </summary>
    Task<List<HostInfo>> DiscoverHostsAsync(VSphereSession session, string clusterMoId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets detailed host information using REST API
    /// </summary>
    Task<HostDetailInfo> GetHostDetailsAsync(VSphereSession session, string hostMoId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes a check on a host using REST API
    /// </summary>
    Task<VSphereApiResult> ExecuteCheckAsync(VSphereSession session, string hostMoId, string checkType, Dictionary<string, object>? parameters = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets comprehensive overview data for the vCenter including clusters, hosts, VMs, and resource usage
    /// </summary>
    Task<VCenterOverview> GetOverviewDataAsync(VSphereSession session, CancellationToken cancellationToken = default);
} 