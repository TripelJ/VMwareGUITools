using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using VMwareGUITools.Core.Models;
using VMwareGUITools.Data;

namespace VMwareGUITools.UI.ViewModels;

/// <summary>
/// View model for managing availability zones and grouping vCenter servers
/// </summary>
public partial class AvailabilityZoneViewModel : ObservableObject
{
    private readonly ILogger<AvailabilityZoneViewModel> _logger;
    private readonly VMwareDbContext _context;

    [ObservableProperty]
    private ObservableCollection<AvailabilityZone> _availabilityZones = new();

    [ObservableProperty]
    private AvailabilityZone? _selectedAvailabilityZone;

    [ObservableProperty]
    private string _newZoneName = string.Empty;

    [ObservableProperty]
    private string _newZoneDescription = string.Empty;

    [ObservableProperty]
    private string _newZoneColor = "#1976D2";

    [ObservableProperty]
    private bool _isLoading = false;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    public AvailabilityZoneViewModel(
        ILogger<AvailabilityZoneViewModel> logger,
        VMwareDbContext context)
    {
        _logger = logger;
        _context = context;

        _ = LoadAvailabilityZonesAsync();
    }

    /// <summary>
    /// Loads all availability zones from the database
    /// </summary>
    public async Task LoadAvailabilityZonesAsync()
    {
        try
        {
            IsLoading = true;
            StatusMessage = "Loading availability zones...";

            var zones = await _context.AvailabilityZones
                .Include(az => az.VCenters)
                .OrderBy(az => az.SortOrder)
                .ThenBy(az => az.Name)
                .ToListAsync();

            AvailabilityZones.Clear();
            foreach (var zone in zones)
            {
                AvailabilityZones.Add(zone);
            }

            StatusMessage = $"Loaded {zones.Count} availability zones";
            _logger.LogInformation("Loaded {Count} availability zones", zones.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load availability zones");
            StatusMessage = $"Failed to load availability zones: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Creates a new availability zone
    /// </summary>
    [RelayCommand]
    private async Task CreateAvailabilityZoneAsync()
    {
        if (string.IsNullOrWhiteSpace(NewZoneName))
        {
            StatusMessage = "Zone name is required";
            return;
        }

        try
        {
            IsLoading = true;
            StatusMessage = "Creating availability zone...";

            // Check if name already exists
            var existingZone = await _context.AvailabilityZones
                .FirstOrDefaultAsync(az => az.Name == NewZoneName);

            if (existingZone != null)
            {
                StatusMessage = "A zone with that name already exists";
                return;
            }

            // Get next sort order
            var maxSortOrder = await _context.AvailabilityZones
                .MaxAsync(az => (int?)az.SortOrder) ?? 0;

            var newZone = new AvailabilityZone
            {
                Name = NewZoneName.Trim(),
                Description = string.IsNullOrWhiteSpace(NewZoneDescription) ? null : NewZoneDescription.Trim(),
                Color = NewZoneColor,
                SortOrder = maxSortOrder + 1,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            _context.AvailabilityZones.Add(newZone);
            await _context.SaveChangesAsync();

            await LoadAvailabilityZonesAsync();

            // Notify main window to refresh
            if (AvailabilityZonesChanged != null)
                await AvailabilityZonesChanged.Invoke();

            // Clear form
            NewZoneName = string.Empty;
            NewZoneDescription = string.Empty;
            NewZoneColor = "#1976D2";

            StatusMessage = $"Created availability zone '{newZone.Name}'";
            _logger.LogInformation("Created availability zone: {ZoneName}", newZone.Name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create availability zone");
            StatusMessage = $"Failed to create availability zone: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Event raised when edit availability zone dialog should be opened
    /// </summary>
    public event Action<AvailabilityZone>? EditAvailabilityZoneRequested;

    /// <summary>
    /// Event raised when availability zones are changed and main window needs to refresh
    /// </summary>
    public static event Func<Task>? AvailabilityZonesChanged;

    /// <summary>
    /// Command to edit an availability zone
    /// </summary>
    [RelayCommand]
    private void EditAvailabilityZone(AvailabilityZone? zone)
    {
        if (zone == null) return;
        EditAvailabilityZoneRequested?.Invoke(zone);
    }

    /// <summary>
    /// Deletes an availability zone
    /// </summary>
    [RelayCommand]
    private async Task DeleteAvailabilityZoneAsync(AvailabilityZone? zone)
    {
        if (zone == null) return;

        try
        {
            // Check if any vCenters are assigned to this zone first
            var vCenterCount = await _context.VCenters
                .CountAsync(vc => vc.AvailabilityZoneId == zone.Id);

            if (vCenterCount > 0)
            {
                StatusMessage = $"Cannot delete zone '{zone.Name}' - it contains {vCenterCount} vCenter(s). Move them to another zone first.";
                return;
            }

            // Show confirmation dialog
            var result = System.Windows.MessageBox.Show(
                $"Are you sure you want to delete the availability zone '{zone.Name}'?",
                "Confirm Delete",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Warning);

            if (result != System.Windows.MessageBoxResult.Yes)
                return;

            IsLoading = true;
            StatusMessage = $"Deleting availability zone '{zone.Name}'...";

            _context.AvailabilityZones.Remove(zone);
            await _context.SaveChangesAsync();

            await LoadAvailabilityZonesAsync();

            // Notify main window to refresh
            if (AvailabilityZonesChanged != null)
                await AvailabilityZonesChanged.Invoke();

            StatusMessage = $"Deleted availability zone '{zone.Name}'";
            _logger.LogInformation("Deleted availability zone: {ZoneName}", zone.Name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete availability zone");
            StatusMessage = $"Failed to delete availability zone: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Moves a vCenter to a different availability zone
    /// </summary>
    public async Task MoveVCenterToZoneAsync(VCenter vCenter, AvailabilityZone? targetZone)
    {
        try
        {
            IsLoading = true;
            var zoneName = targetZone?.Name ?? "No Zone";
            StatusMessage = $"Moving vCenter '{vCenter.Name}' to '{zoneName}'...";

            vCenter.AvailabilityZoneId = targetZone?.Id;
            vCenter.UpdatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync();

            StatusMessage = $"Moved vCenter '{vCenter.Name}' to '{zoneName}'";
            _logger.LogInformation("Moved vCenter {VCenterName} to zone {ZoneName}", 
                vCenter.Name, zoneName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to move vCenter to zone");
            StatusMessage = $"Failed to move vCenter: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Updates the sort order of availability zones
    /// </summary>
    public async Task UpdateSortOrderAsync(List<AvailabilityZone> orderedZones)
    {
        try
        {
            IsLoading = true;
            StatusMessage = "Updating zone order...";

            for (int i = 0; i < orderedZones.Count; i++)
            {
                orderedZones[i].SortOrder = i + 1;
                orderedZones[i].UpdatedAt = DateTime.UtcNow;
            }

            await _context.SaveChangesAsync();
            await LoadAvailabilityZonesAsync();

            StatusMessage = "Zone order updated";
            _logger.LogInformation("Updated sort order for {Count} availability zones", orderedZones.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update zone sort order");
            StatusMessage = $"Failed to update zone order: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Command to set the zone color
    /// </summary>
    [RelayCommand]
    private void SetZoneColor(string color)
    {
        NewZoneColor = color;
    }

    /// <summary>
    /// Command to cancel the operation
    /// </summary>
    [RelayCommand]
    private void Cancel()
    {
        // Reset form
        NewZoneName = string.Empty;
        NewZoneDescription = string.Empty;
        NewZoneColor = "#1976D2";
        StatusMessage = string.Empty;
    }
} 