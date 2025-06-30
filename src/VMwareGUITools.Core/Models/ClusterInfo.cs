namespace VMwareGUITools.Core.Models;

/// <summary>
/// Data transfer object for cluster information used in UI display
/// </summary>
public class ClusterInfo
{
    /// <summary>
    /// The name of the cluster
    /// </summary>
    public string Name { get; set; } = string.Empty;
    
    /// <summary>
    /// Whether DRS (Distributed Resource Scheduler) is enabled
    /// </summary>
    public bool DrsEnabled { get; set; }
    
    /// <summary>
    /// Whether HA (High Availability) is enabled  
    /// </summary>
    public bool HaEnabled { get; set; }
} 