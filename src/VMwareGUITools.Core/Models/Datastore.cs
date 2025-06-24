using System.ComponentModel.DataAnnotations;

namespace VMwareGUITools.Core.Models;

/// <summary>
/// Represents a VMware datastore
/// </summary>
public class Datastore
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

    [Required]
    [StringLength(20)]
    public string Type { get; set; } = string.Empty; // VMFS, NFS, vSAN, etc.

    public long CapacityMB { get; set; }

    public long FreeMB { get; set; }

    public bool Accessible { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Navigation properties
    public virtual VCenter VCenter { get; set; } = null!;

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