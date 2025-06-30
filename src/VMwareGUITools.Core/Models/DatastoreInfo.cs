namespace VMwareGUITools.Core.Models;

/// <summary>
/// Data transfer object for datastore information used in UI display
/// </summary>
public class DatastoreInfo
{
    /// <summary>
    /// The name of the datastore
    /// </summary>
    public string Name { get; set; } = string.Empty;
    
    /// <summary>
    /// The type of datastore (VMFS, NFS, vSAN, vVol, etc.)
    /// </summary>
    public string Type { get; set; } = string.Empty;
    
    /// <summary>
    /// Whether the datastore is accessible
    /// </summary>
    public bool Accessible { get; set; } = true;
    
    /// <summary>
    /// The usage percentage of the datastore
    /// </summary>
    public double UsagePercentage { get; set; }
    
    /// <summary>
    /// Formatted string showing used space (e.g., "1.2 TB")
    /// </summary>
    public string FormattedUsedSpace { get; set; } = string.Empty;
    
    /// <summary>
    /// Formatted string showing total capacity (e.g., "2.0 TB")
    /// </summary>
    public string FormattedCapacity { get; set; } = string.Empty;
} 