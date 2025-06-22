using System.ComponentModel.DataAnnotations;

namespace VMwareGUITools.Core.Models;

/// <summary>
/// Represents a VMware vCenter Server instance
/// </summary>
public class VCenter
{
    public int Id { get; set; }

    [Required]
    [StringLength(100)]
    public string Name { get; set; } = string.Empty;

    [Required]
    [Url]
    [StringLength(255)]
    public string Url { get; set; } = string.Empty;

    [Required]
    [StringLength(500)]
    public string EncryptedCredentials { get; set; } = string.Empty;

    public DateTime? LastScan { get; set; }

    public bool Enabled { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Navigation properties
    public virtual ICollection<Cluster> Clusters { get; set; } = new List<Cluster>();

    /// <summary>
    /// Gets a display-friendly identifier for this vCenter
    /// </summary>
    public string DisplayName => !string.IsNullOrEmpty(Name) ? Name : Url;

    /// <summary>
    /// Gets the connection status based on last scan time
    /// </summary>
    public ConnectionStatus Status
    {
        get
        {
            if (!Enabled) return ConnectionStatus.Disabled;
            if (!LastScan.HasValue) return ConnectionStatus.NotTested;
            if (DateTime.UtcNow - LastScan.Value > TimeSpan.FromMinutes(30)) return ConnectionStatus.Stale;
            return ConnectionStatus.Connected;
        }
    }
}

public enum ConnectionStatus
{
    NotTested,
    Connected,
    Disconnected,
    Stale,
    Disabled
} 