using System.ComponentModel.DataAnnotations;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace VMwareGUITools.Core.Models;

/// <summary>
/// Represents an availability zone that groups vCenter servers
/// </summary>
public class AvailabilityZone : INotifyPropertyChanged
{
    public int Id { get; set; }

    [Required]
    [StringLength(100)]
    public string Name { get; set; } = string.Empty;

    [StringLength(500)]
    public string? Description { get; set; }

    [StringLength(50)]
    public string? Color { get; set; } = "#1976D2"; // Default VMware blue

    public int SortOrder { get; set; } = 0;

    public bool IsExpanded { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Navigation properties
    public virtual ICollection<VCenter> VCenters { get; set; } = new List<VCenter>();

    /// <summary>
    /// Gets the display name for this availability zone
    /// </summary>
    public string DisplayName => Name;

    /// <summary>
    /// Gets the count of vCenters in this zone
    /// </summary>
    public int VCenterCount => VCenters?.Count ?? 0;

    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
} 