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

    // Navigation properties
    public virtual ICollection<Cluster> Clusters { get; set; } = new List<Cluster>();
    public virtual ICollection<Host> Hosts { get; set; } = new List<Host>();

    /// <summary>
    /// Gets a display-friendly identifier for this vCenter
    /// </summary>
    public string DisplayName => !string.IsNullOrEmpty(Name) ? Name : Url;

    /// <summary>
    /// Gets the connection status based on real-time connection state and last successful connection
    /// </summary>
    public ConnectionStatus Status
    {
        get
        {
            if (!Enabled) return ConnectionStatus.Disabled;
            
            // If currently connected, return Connected
            if (IsCurrentlyConnected) return ConnectionStatus.Connected;
            
            // Check last successful connection first (more recent and accurate than LastScan)
            var lastConnection = LastSuccessfulConnection ?? LastScan;
            
            if (!lastConnection.HasValue) return ConnectionStatus.NotTested;
            
            // If last connection was within 5 minutes, consider it connected
            if (DateTime.UtcNow - lastConnection.Value <= TimeSpan.FromMinutes(5))
                return ConnectionStatus.Connected;
                
            // If last connection was within 30 minutes, consider it stale
            if (DateTime.UtcNow - lastConnection.Value <= TimeSpan.FromMinutes(30))
                return ConnectionStatus.Stale;
                
            return ConnectionStatus.Disconnected;
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