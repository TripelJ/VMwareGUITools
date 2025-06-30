namespace VMwareGUITools.Core.Models;

/// <summary>
/// Data transfer object for host information used in UI display
/// </summary>
public class HostInfo
{
    /// <summary>
    /// The name of the host
    /// </summary>
    public string Name { get; set; } = string.Empty;
    
    /// <summary>
    /// Whether the host is in maintenance mode
    /// </summary>
    public bool InMaintenanceMode { get; set; }
    
    /// <summary>
    /// The connection state of the host (connected, disconnected, notresponding)
    /// </summary>
    public string? ConnectionState { get; set; }
    
    /// <summary>
    /// The power state of the host (poweredon, poweredoff, etc.)
    /// </summary>
    public string? PowerState { get; set; }
    
    /// <summary>
    /// The type of host (Standard, VsanNode, ManagementCluster, EdgeCluster)
    /// </summary>
    public HostType Type { get; set; } = HostType.Standard;
} 