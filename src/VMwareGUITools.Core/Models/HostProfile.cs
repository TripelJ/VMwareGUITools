using System.ComponentModel.DataAnnotations;
using System.Text.Json;

namespace VMwareGUITools.Core.Models;

/// <summary>
/// Represents a host profile that defines the checks and configuration for specific types of hosts
/// </summary>
public class HostProfile
{
    public int Id { get; set; }

    [Required]
    [StringLength(100)]
    public string Name { get; set; } = string.Empty;

    [StringLength(500)]
    public string Description { get; set; } = string.Empty;

    public HostType Type { get; set; } = HostType.Standard;

    [Required]
    public string CheckConfigs { get; set; } = "[]"; // JSON array of check configurations

    public bool IsDefault { get; set; } = false;

    public bool Enabled { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Navigation properties
    public virtual ICollection<Cluster> Clusters { get; set; } = new List<Cluster>();
    public virtual ICollection<Host> Hosts { get; set; } = new List<Host>();

    /// <summary>
    /// Gets the parsed check configurations from the JSON string
    /// </summary>
    public List<HostProfileCheckConfig> GetCheckConfigs()
    {
        try
        {
            return JsonSerializer.Deserialize<List<HostProfileCheckConfig>>(CheckConfigs) ?? new List<HostProfileCheckConfig>();
        }
        catch
        {
            return new List<HostProfileCheckConfig>();
        }
    }

    /// <summary>
    /// Sets the check configurations as JSON string
    /// </summary>
    public void SetCheckConfigs(List<HostProfileCheckConfig> configs)
    {
        CheckConfigs = JsonSerializer.Serialize(configs, new JsonSerializerOptions 
        { 
            WriteIndented = true 
        });
        UpdatedAt = DateTime.UtcNow;
    }
}

/// <summary>
/// Represents a check configuration within a host profile
/// </summary>
public class HostProfileCheckConfig
{
    public string Category { get; set; } = string.Empty;
    public int CheckId { get; set; }
    public string Schedule { get; set; } = "daily";
    public bool AlertOnFailure { get; set; } = true;
    public bool AlertOnSuccess { get; set; } = false;
    public Dictionary<string, string> Parameters { get; set; } = new Dictionary<string, string>();
} 