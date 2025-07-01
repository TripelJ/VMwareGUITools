using System.ComponentModel.DataAnnotations;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace VMwareGUITools.Core.Models;

/// <summary>
/// Represents a VMware vCenter Server instance
/// </summary>
public class VCenter : INotifyPropertyChanged
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

    private DateTime? _lastSuccessfulConnection;
    /// <summary>
    /// Tracks the last time a successful connection was made (not persisted to database)
    /// </summary>
    public DateTime? LastSuccessfulConnection
    {
        get => _lastSuccessfulConnection;
        set
        {
            if (_lastSuccessfulConnection != value)
            {
                _lastSuccessfulConnection = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(Status));
            }
        }
    }

    private bool _isCurrentlyConnected;
    /// <summary>
    /// Indicates if the vCenter is currently connected (not persisted to database)
    /// </summary>
    public bool IsCurrentlyConnected
    {
        get => _isCurrentlyConnected;
        set
        {
            if (_isCurrentlyConnected != value)
            {
                _isCurrentlyConnected = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(Status));
            }
        }
    }

    public bool Enabled { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Foreign key for AvailabilityZone
    public int? AvailabilityZoneId { get; set; }

    // Navigation properties
    public virtual AvailabilityZone? AvailabilityZone { get; set; }
    public virtual ICollection<Cluster> Clusters { get; set; } = new List<Cluster>();
    public virtual ICollection<Host> Hosts { get; set; } = new List<Host>();

    /// <summary>
    /// Gets a display-friendly identifier for this vCenter
    /// </summary>
    public string DisplayName => !string.IsNullOrEmpty(Name) ? Name : Url;

    /// <summary>
    /// Gets the connection status based on service connection state and data flow.
    /// Only shows Connected when actively connected through the service and receiving data.
    /// Test connections do not affect this status.
    /// </summary>
    public ConnectionStatus Status
    {
        get
        {
            if (!Enabled) return ConnectionStatus.Disabled;
            
            // Only show Connected if currently connected through service AND there's been recent data activity
            if (IsCurrentlyConnected && LastSuccessfulConnection.HasValue)
            {
                // Must be connected through service with recent activity (within 10 minutes)
                if (DateTime.UtcNow - LastSuccessfulConnection.Value <= TimeSpan.FromMinutes(10))
                    return ConnectionStatus.Connected;
            }
            
            // Check for stale connections (service was connected but not recently)
            var lastConnection = LastSuccessfulConnection ?? LastScan;
            if (lastConnection.HasValue)
            {
                // If last service connection was within 30 minutes but not recent, show as stale
                if (DateTime.UtcNow - lastConnection.Value <= TimeSpan.FromMinutes(30))
                    return ConnectionStatus.Stale;
                    
                // Older connections are considered disconnected
                return ConnectionStatus.Disconnected;
            }
            
            return ConnectionStatus.NotTested;
        }
    }

    /// <summary>
    /// Updates the connection status when a successful connection is made
    /// </summary>
    public void UpdateConnectionStatus(bool isConnected)
    {
        IsCurrentlyConnected = isConnected;
        if (isConnected)
        {
            LastSuccessfulConnection = DateTime.UtcNow;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
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