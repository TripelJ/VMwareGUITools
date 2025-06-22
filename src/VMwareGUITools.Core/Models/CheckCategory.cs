using System.ComponentModel.DataAnnotations;

namespace VMwareGUITools.Core.Models;

/// <summary>
/// Represents a category that groups related checks together
/// </summary>
public class CheckCategory
{
    public int Id { get; set; }

    [Required]
    [StringLength(100)]
    public string Name { get; set; } = string.Empty;

    [StringLength(500)]
    public string Description { get; set; } = string.Empty;

    public CheckCategoryType Type { get; set; } = CheckCategoryType.Configuration;

    public bool Enabled { get; set; } = true;

    public int SortOrder { get; set; } = 0;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Navigation properties
    public virtual ICollection<CheckDefinition> CheckDefinitions { get; set; } = new List<CheckDefinition>();

    /// <summary>
    /// Gets the count of enabled check definitions in this category
    /// </summary>
    public int EnabledCheckCount => CheckDefinitions?.Count(c => c.IsEnabled) ?? 0;
}

/// <summary>
/// Represents the type of check category
/// </summary>
public enum CheckCategoryType
{
    Configuration,
    Health,
    Performance,
    Security,
    Compliance
} 