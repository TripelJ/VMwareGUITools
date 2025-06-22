using System.ComponentModel.DataAnnotations;

namespace VMwareGUITools.Core.Models;

/// <summary>
/// Represents a VMware vSphere cluster
/// </summary>
public class Cluster
{
    public int Id { get; set; }

    [Required]
    public int VCenterId { get; set; }

    [Required]
    [StringLength(100)]
    public string Name { get; set; } = string.Empty;

    [Required]
    [StringLength(50)]
    public string MoId { get; set; } = string.Empty;

    public int? ProfileId { get; set; }

    public bool Enabled { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Navigation properties
    public virtual VCenter VCenter { get; set; } = null!;
    public virtual HostProfile? Profile { get; set; }
    public virtual ICollection<Host> Hosts { get; set; } = new List<Host>();

    /// <summary>
    /// Gets the number of hosts in this cluster
    /// </summary>
    public int HostCount => Hosts?.Count ?? 0;

    /// <summary>
    /// Gets the number of enabled hosts in this cluster
    /// </summary>
    public int EnabledHostCount => Hosts?.Count(h => h.Enabled) ?? 0;
} 