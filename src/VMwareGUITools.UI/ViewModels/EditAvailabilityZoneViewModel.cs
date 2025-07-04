using System.ComponentModel.DataAnnotations;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using VMwareGUITools.Core.Models;
using VMwareGUITools.Data;

namespace VMwareGUITools.UI.ViewModels;

/// <summary>
/// View model for editing an existing availability zone
/// </summary>
public partial class EditAvailabilityZoneViewModel : ObservableValidator
{
    private readonly ILogger<EditAvailabilityZoneViewModel> _logger;
    private readonly VMwareDbContext _context;
    private AvailabilityZone? _originalZone;

    [ObservableProperty]
    [Required(ErrorMessage = "Zone name is required")]
    [StringLength(100, ErrorMessage = "Zone name must be less than 100 characters")]
    private string _zoneName = string.Empty;

    [ObservableProperty]
    [StringLength(500, ErrorMessage = "Description must be less than 500 characters")]
    private string _zoneDescription = string.Empty;

    [ObservableProperty]
    private string _zoneColor = "#1976D2";

    [ObservableProperty]
    private bool _isLoading = false;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _canDelete = false;

    [ObservableProperty]
    private int _vCenterCount = 0;

    public EditAvailabilityZoneViewModel(
        ILogger<EditAvailabilityZoneViewModel> logger,
        VMwareDbContext context)
    {
        _logger = logger;
        _context = context;
    }

    /// <summary>
    /// Event raised when dialog result should be set
    /// </summary>
    public event Action<bool>? DialogResultRequested;

    /// <summary>
    /// Initialize the view model with existing availability zone data
    /// </summary>
    public async Task InitializeAsync(AvailabilityZone zone)
    {
        _originalZone = zone;
        
        ZoneName = zone.Name;
        ZoneDescription = zone.Description ?? string.Empty;
        ZoneColor = zone.Color ?? "#1976D2";
        
        // Check if any vCenters are assigned to this zone
        VCenterCount = await _context.VCenters
            .CountAsync(vc => vc.AvailabilityZoneId == zone.Id);
            
        // Can only delete if no vCenters are assigned
        CanDelete = VCenterCount == 0;
    }

    /// <summary>
    /// Command to save changes to the availability zone
    /// </summary>
    [RelayCommand]
    private async Task SaveChangesAsync()
    {
        if (string.IsNullOrWhiteSpace(ZoneName) || _originalZone == null)
        {
            StatusMessage = "Zone name is required";
            return;
        }

        try
        {
            IsLoading = true;
            StatusMessage = "Saving changes...";

            // Check if name already exists (excluding current zone)
            var existingZone = await _context.AvailabilityZones
                .FirstOrDefaultAsync(az => az.Name == ZoneName && az.Id != _originalZone.Id);

            if (existingZone != null)
            {
                StatusMessage = "A zone with that name already exists";
                return;
            }

            // Update the zone
            _originalZone.Name = ZoneName.Trim();
            _originalZone.Description = string.IsNullOrWhiteSpace(ZoneDescription) ? null : ZoneDescription.Trim();
            _originalZone.Color = ZoneColor;
            _originalZone.UpdatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync();

            _logger.LogInformation("Successfully updated availability zone: {ZoneName}", ZoneName);

            // Close dialog with success
            DialogResultRequested?.Invoke(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update availability zone");
            StatusMessage = $"Failed to save changes: {ex.Message}";
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
        ZoneColor = color;
    }

    /// <summary>
    /// Command to delete the availability zone
    /// </summary>
    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (_originalZone == null) return;

        try
        {
            // Double-check that no vCenters are assigned
            var currentVCenterCount = await _context.VCenters
                .CountAsync(vc => vc.AvailabilityZoneId == _originalZone.Id);

            if (currentVCenterCount > 0)
            {
                StatusMessage = $"Cannot delete zone '{_originalZone.Name}' - it contains {currentVCenterCount} vCenter(s). Move them to another zone first.";
                return;
            }

            _logger.LogInformation("Requesting to delete availability zone: {ZoneName}", _originalZone.Name);

            // Show confirmation dialog
            var result = System.Windows.MessageBox.Show(
                $"Are you sure you want to delete the availability zone '{_originalZone.Name}'?\n\n" +
                "This action cannot be undone.",
                "Confirm Delete Availability Zone",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Warning,
                System.Windows.MessageBoxResult.No);

            if (result == System.Windows.MessageBoxResult.Yes)
            {
                IsLoading = true;
                StatusMessage = $"Deleting availability zone '{_originalZone.Name}'...";

                _logger.LogInformation("User confirmed deletion of availability zone: {ZoneName}", _originalZone.Name);

                _context.AvailabilityZones.Remove(_originalZone);
                await _context.SaveChangesAsync();

                _logger.LogInformation("Successfully deleted availability zone: {ZoneName}", _originalZone.Name);

                // Close dialog with success (indicating the zone was deleted)
                DialogResultRequested?.Invoke(true);
            }
            else
            {
                _logger.LogInformation("User cancelled deletion of availability zone: {ZoneName}", _originalZone.Name);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete availability zone: {ZoneName}", _originalZone?.Name ?? "Unknown");
            StatusMessage = $"Failed to delete availability zone: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Command to cancel editing
    /// </summary>
    [RelayCommand]
    private void Cancel()
    {
        _logger.LogDebug("Edit availability zone dialog cancelled");
        DialogResultRequested?.Invoke(false);
    }
} 