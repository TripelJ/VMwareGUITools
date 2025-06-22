using System.ComponentModel.DataAnnotations;

namespace VMwareGUITools.Core.Models;

/// <summary>
/// Represents an ESXi host in a VMware vSphere environment
/// </summary>
public class Host
{
    public int Id { get; set; }

    [Required]
    public int ClusterId { get; set; }

    [Required]
    public int VCenterId { get; set; }

    [Required]
    [StringLength(100)]
    public string Name { get; set; } = string.Empty;

    [Required]
    [StringLength(45)] // IPv4/IPv6 address
    public string IpAddress { get; set; } = string.Empty;

    [Required]
    [StringLength(50)]
    public string MoId { get; set; } = string.Empty;

    [Required]
    [StringLength(100)]
    public string ClusterName { get; set; } = string.Empty;

    public HostType HostType { get; set; } = HostType.Standard;

    public int? ProfileId { get; set; }

    public bool SshEnabled { get; set; } = false;

    public bool Enabled { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? LastCheckRun { get; set; }

    // Navigation properties
    public virtual Cluster Cluster { get; set; } = null!;
    public virtual VCenter VCenter { get; set; } = null!;
    public virtual HostProfile? Profile { get; set; }
    public virtual ICollection<CheckResult> CheckResults { get; set; } = new List<CheckResult>();

    /// <summary>
    /// Gets the latest check result summary for this host
    /// </summary>
    public HealthStatus HealthStatus
    {
        get
        {
            if (CheckResults == null || !CheckResults.Any()) return HealthStatus.Unknown;
            
            var latestResults = CheckResults
                .Where(r => r.ExecutedAt > DateTime.UtcNow.AddHours(-24))
                .ToList();
            
            if (!latestResults.Any()) return HealthStatus.Stale;
            
            if (latestResults.Any(r => r.Status == CheckStatus.Critical)) return HealthStatus.Critical;
            if (latestResults.Any(r => r.Status == CheckStatus.Warning)) return HealthStatus.Warning;
            if (latestResults.Any(r => r.Status == CheckStatus.Failed)) return HealthStatus.Critical;
            if (latestResults.All(r => r.Status == CheckStatus.Passed)) return HealthStatus.Healthy;
            
            return HealthStatus.Unknown;
        }
    }

    /// <summary>
    /// Gets a display-friendly identifier for this host
    /// </summary>
    public string DisplayName => !string.IsNullOrEmpty(Name) ? Name : IpAddress;
}

public enum HostType
{
    Standard,
    VsanNode,
    ManagementCluster,
    EdgeCluster
}

public enum HealthStatus
{
    Unknown,
    Healthy,
    Warning,
    Critical,
    Stale
} 