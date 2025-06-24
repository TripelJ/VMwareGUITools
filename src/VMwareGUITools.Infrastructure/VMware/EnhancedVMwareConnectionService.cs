using Microsoft.Extensions.Logging;
using System.Management.Automation;
using VMwareGUITools.Core.Models;
using VMwareGUITools.Infrastructure.PowerShell;
using VMwareGUITools.Infrastructure.Security;

namespace VMwareGUITools.Infrastructure.VMware;

/// <summary>
/// Enhanced VMware connection service using the improved PowerCLI service
/// </summary>
public class EnhancedVMwareConnectionService : IVMwareConnectionService
{
    private readonly ILogger<EnhancedVMwareConnectionService> _logger;
    private readonly ICredentialService _credentialService;
    private readonly IPowerCLIService _powerCLIService;
    private readonly Dictionary<string, EnhancedVMwareSession> _activeSessions = new();

    public EnhancedVMwareConnectionService(
        ILogger<EnhancedVMwareConnectionService> logger,
        ICredentialService credentialService,
        IPowerCLIService powerCLIService)
    {
        _logger = logger;
        _credentialService = credentialService;
        _powerCLIService = powerCLIService;
    }

    public async Task<VCenterConnectionResult> TestConnectionAsync(string vcenterUrl, string username, string password, CancellationToken cancellationToken = default)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        
        try
        {
            _logger.LogInformation("Testing enhanced connection to vCenter: {VCenterUrl}", vcenterUrl);

            // Basic URL validation
            if (!Uri.TryCreate(vcenterUrl, UriKind.Absolute, out var uri) || 
                (uri.Scheme != "https" && uri.Scheme != "http"))
            {
                return new VCenterConnectionResult
                {
                    IsSuccessful = false,
                    ErrorMessage = "Invalid vCenter URL format. URL must start with http:// or https://",
                    ResponseTime = stopwatch.Elapsed
                };
            }

            // First validate PowerCLI environment
            var validation = await _powerCLIService.ValidatePowerCLIAsync();
            if (!validation.IsValid)
            {
                var errorMessage = "PowerCLI environment validation failed:\n" +
                                 string.Join("\n", validation.Issues);
                
                if (validation.Suggestions.Count > 0)
                {
                    errorMessage += "\n\nSuggested fixes:\n" + string.Join("\n", validation.Suggestions);
                }

                return new VCenterConnectionResult
                {
                    IsSuccessful = false,
                    ErrorMessage = errorMessage,
                    ResponseTime = stopwatch.Elapsed
                };
            }

            // Test PowerCLI connection
            var connectionResult = await _powerCLIService.TestConnectionAsync(vcenterUrl, username, password, 60);
            
            if (connectionResult.IsSuccessful)
            {
                var versionInfo = new VCenterVersionInfo
                {
                    Version = connectionResult.ServerVersion ?? "",
                    Build = connectionResult.ServerBuild ?? "",
                    ApiVersion = connectionResult.ApiVersion ?? "",
                    ProductName = "VMware vCenter Server"
                };

                _logger.LogInformation("Successfully tested connection to vCenter: {VCenterUrl} (Version: {Version}, Build: {Build})", 
                    vcenterUrl, versionInfo.Version, versionInfo.Build);

                return new VCenterConnectionResult
                {
                    IsSuccessful = true,
                    ResponseTime = connectionResult.ResponseTime,
                    VersionInfo = versionInfo
                };
            }
            else
            {
                var enhancedErrorMessage = EnhanceErrorMessage(connectionResult.ErrorMessage, connectionResult.ErrorCode);
                
                _logger.LogWarning("Failed to connect to vCenter: {VCenterUrl} - {Error} (Code: {ErrorCode})", 
                    vcenterUrl, connectionResult.ErrorMessage, connectionResult.ErrorCode);

                return new VCenterConnectionResult
                {
                    IsSuccessful = false,
                    ErrorMessage = enhancedErrorMessage,
                    ResponseTime = connectionResult.ResponseTime
                };
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Enhanced connection test failed for vCenter: {VCenterUrl}", vcenterUrl);
            return new VCenterConnectionResult
            {
                IsSuccessful = false,
                ErrorMessage = $"Connection test failed: {ex.Message}",
                ResponseTime = stopwatch.Elapsed
            };
        }
    }

    public async Task<VMwareSession> ConnectAsync(VCenter vCenter, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("Establishing enhanced connection to vCenter: {VCenterUrl}", vCenter.Url);

            // Decrypt credentials
            var (username, password) = _credentialService.DecryptCredentials(vCenter.EncryptedCredentials);

            // Establish PowerCLI session
            var powerCLISession = await _powerCLIService.ConnectAsync(vCenter.Url, username, password, 60);

            // Create enhanced session wrapper
            var enhancedSession = new EnhancedVMwareSession
            {
                SessionId = Guid.NewGuid().ToString(),
                VCenterUrl = vCenter.Url,
                Username = username,
                CreatedAt = DateTime.UtcNow,
                LastActivity = DateTime.UtcNow,
                IsActive = true,
                PowerCLISession = powerCLISession,
                VersionInfo = new VCenterVersionInfo
                {
                    Version = powerCLISession.ServerVersion ?? "",
                    ApiVersion = powerCLISession.ApiVersion ?? "",
                    ProductName = "VMware vCenter Server"
                }
            };

            _activeSessions[enhancedSession.SessionId] = enhancedSession;

            _logger.LogInformation("Enhanced VMware session established. SessionId: {SessionId}", enhancedSession.SessionId);

            // Return standard VMware session for compatibility
            return new VMwareSession
            {
                SessionId = enhancedSession.SessionId,
                VCenterUrl = enhancedSession.VCenterUrl,
                Username = enhancedSession.Username,
                CreatedAt = enhancedSession.CreatedAt,
                LastActivity = enhancedSession.LastActivity,
                IsActive = enhancedSession.IsActive,
                VersionInfo = enhancedSession.VersionInfo
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to establish enhanced VMware session to vCenter: {VCenterUrl}", vCenter.Url);
            throw;
        }
    }

    public async Task DisconnectAsync(VMwareSession session, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("Disconnecting enhanced VMware session: {SessionId}", session.SessionId);

            if (_activeSessions.TryGetValue(session.SessionId, out var enhancedSession))
            {
                await _powerCLIService.DisconnectAsync(enhancedSession.PowerCLISession);
                _activeSessions.Remove(session.SessionId);
                enhancedSession.IsActive = false;
            }

            _logger.LogInformation("Enhanced VMware session disconnected: {SessionId}", session.SessionId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error disconnecting enhanced VMware session: {SessionId}", session.SessionId);
            // Always remove from active sessions
            _activeSessions.Remove(session.SessionId);
        }
    }

    public async Task<List<ClusterInfo>> DiscoverClustersAsync(VMwareSession session, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("Discovering clusters for session: {SessionId}", session.SessionId);

            if (!_activeSessions.TryGetValue(session.SessionId, out var enhancedSession))
            {
                throw new InvalidOperationException("Session not found or inactive");
            }

            var command = @"
                Get-Cluster | Select-Object Name, 
                    @{N='MoId'; E={$_.Id}},
                    @{N='HostCount'; E={($_ | Get-VMHost).Count}},
                    @{N='ParentFolder'; E={$_.Parent.Name}},
                    DrsEnabled,
                    HAEnabled,
                    VsanEnabled
            ";

            var result = await _powerCLIService.ExecuteCommandAsync(enhancedSession.PowerCLISession, command, timeoutSeconds: 120);
            
            if (!result.IsSuccess)
            {
                throw new InvalidOperationException($"Failed to discover clusters: {result.ErrorMessage}");
            }

            enhancedSession.LastActivity = DateTime.UtcNow;

            var clusters = new List<ClusterInfo>();
            foreach (var obj in result.Objects.OfType<PSObject>())
            {
                clusters.Add(new ClusterInfo
                {
                    Name = GetPropertyValue<string>(obj, "Name") ?? "",
                    MoId = GetPropertyValue<string>(obj, "MoId") ?? "",
                    HostCount = GetPropertyValue<int>(obj, "HostCount"),
                    ParentFolder = GetPropertyValue<string>(obj, "ParentFolder"),
                    DrsEnabled = GetPropertyValue<bool>(obj, "DrsEnabled"),
                    HaEnabled = GetPropertyValue<bool>(obj, "HAEnabled"),
                    VsanEnabled = GetPropertyValue<bool>(obj, "VsanEnabled")
                });
            }

            _logger.LogInformation("Discovered {ClusterCount} clusters for session: {SessionId}", clusters.Count, session.SessionId);
            return clusters;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to discover clusters for session: {SessionId}", session.SessionId);
            throw;
        }
    }

    public async Task<List<HostInfo>> DiscoverHostsAsync(VMwareSession session, string clusterMoId, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("Discovering hosts in cluster {ClusterMoId} for session: {SessionId}", clusterMoId, session.SessionId);

            if (!_activeSessions.TryGetValue(session.SessionId, out var enhancedSession))
            {
                throw new InvalidOperationException("Session not found or inactive");
            }

            var command = $@"
                $cluster = Get-Cluster | Where-Object {{ $_.Id -eq '{clusterMoId}' }}
                if ($cluster) {{
                    $cluster | Get-VMHost | Select-Object Name,
                        @{{N='MoId'; E={{$_.Id}}}},
                        @{{N='IpAddress'; E={{$_.NetworkInfo.VirtualNic[0].Spec.Ip.IpAddress}}}},
                        Version,
                        PowerState,
                        ConnectionState,
                        @{{N='InMaintenanceMode'; E={{$_.State -eq 'Maintenance'}}}},
                        @{{N='Type'; E={{'Standard'}}}}
                }}
            ";

            var result = await _powerCLIService.ExecuteCommandAsync(enhancedSession.PowerCLISession, command, timeoutSeconds: 120);
            
            if (!result.IsSuccess)
            {
                throw new InvalidOperationException($"Failed to discover hosts: {result.ErrorMessage}");
            }

            enhancedSession.LastActivity = DateTime.UtcNow;

            var hosts = new List<HostInfo>();
            foreach (var obj in result.Objects.OfType<PSObject>())
            {
                hosts.Add(new HostInfo
                {
                    Name = GetPropertyValue<string>(obj, "Name") ?? "",
                    MoId = GetPropertyValue<string>(obj, "MoId") ?? "",
                    IpAddress = GetPropertyValue<string>(obj, "IpAddress") ?? "",
                    Version = GetPropertyValue<string>(obj, "Version") ?? "",
                    PowerState = GetPropertyValue<string>(obj, "PowerState") ?? "",
                    ConnectionState = GetPropertyValue<string>(obj, "ConnectionState") ?? "",
                    InMaintenanceMode = GetPropertyValue<bool>(obj, "InMaintenanceMode"),
                    Type = HostType.Standard
                });
            }

            _logger.LogInformation("Discovered {HostCount} hosts in cluster {ClusterMoId} for session: {SessionId}", 
                hosts.Count, clusterMoId, session.SessionId);
            return hosts;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to discover hosts in cluster {ClusterMoId} for session: {SessionId}", clusterMoId, session.SessionId);
            throw;
        }
    }

    public async Task<HostDetailInfo> GetHostDetailsAsync(VMwareSession session, string hostMoId, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("Getting host details for {HostMoId} in session: {SessionId}", hostMoId, session.SessionId);

            if (!_activeSessions.TryGetValue(session.SessionId, out var enhancedSession))
            {
                throw new InvalidOperationException("Session not found or inactive");
            }

            var command = $@"
                $vmhost = Get-VMHost | Where-Object {{ $_.Id -eq '{hostMoId}' }}
                if ($vmhost) {{
                    $vmhost | Select-Object Name,
                        @{{N='MoId'; E={{$_.Id}}}},
                        @{{N='IpAddress'; E={{$_.NetworkInfo.VirtualNic[0].Spec.Ip.IpAddress}}}},
                        Version,
                        PowerState,
                        ConnectionState,
                        @{{N='InMaintenanceMode'; E={{$_.State -eq 'Maintenance'}}}},
                        @{{N='Vendor'; E={{$_.Hardware.SystemInfo.Vendor}}}},
                        @{{N='Model'; E={{$_.Hardware.SystemInfo.Model}}}},
                        @{{N='ProcessorType'; E={{$_.Hardware.CpuInfo.Name}}}},
                        @{{N='MemorySize'; E={{$_.Hardware.MemorySize}}}},
                        @{{N='CpuCores'; E={{$_.Hardware.CpuInfo.NumCpuCores}}}},
                        @{{N='SshEnabled'; E={{ ($_ | Get-VMHostService | Where-Object {{$_.Key -eq 'TSM-SSH'}}).Running}}}},
                        @{{N='Type'; E={{'Standard'}}}}
                }}
            ";

            var result = await _powerCLIService.ExecuteCommandAsync(enhancedSession.PowerCLISession, command, timeoutSeconds: 60);
            
            if (!result.IsSuccess)
            {
                throw new InvalidOperationException($"Failed to get host details: {result.ErrorMessage}");
            }

            enhancedSession.LastActivity = DateTime.UtcNow;

            if (result.Objects.Count == 0)
            {
                throw new InvalidOperationException($"Host with MoId {hostMoId} not found");
            }

            var obj = result.Objects.OfType<PSObject>().First();
            var hostDetail = new HostDetailInfo
            {
                Name = GetPropertyValue<string>(obj, "Name") ?? "",
                MoId = GetPropertyValue<string>(obj, "MoId") ?? "",
                IpAddress = GetPropertyValue<string>(obj, "IpAddress") ?? "",
                Version = GetPropertyValue<string>(obj, "Version") ?? "",
                PowerState = GetPropertyValue<string>(obj, "PowerState") ?? "",
                ConnectionState = GetPropertyValue<string>(obj, "ConnectionState") ?? "",
                InMaintenanceMode = GetPropertyValue<bool>(obj, "InMaintenanceMode"),
                Vendor = GetPropertyValue<string>(obj, "Vendor") ?? "",
                Model = GetPropertyValue<string>(obj, "Model") ?? "",
                ProcessorType = GetPropertyValue<string>(obj, "ProcessorType") ?? "",
                MemorySize = GetPropertyValue<long>(obj, "MemorySize"),
                CpuCores = GetPropertyValue<int>(obj, "CpuCores"),
                SshEnabled = GetPropertyValue<bool>(obj, "SshEnabled"),
                Type = HostType.Standard
            };

            _logger.LogInformation("Retrieved host details for {HostMoId} in session: {SessionId}", hostMoId, session.SessionId);
            return hostDetail;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get host details for {HostMoId} in session: {SessionId}", hostMoId, session.SessionId);
            throw;
        }
    }

    public async Task<bool> IsPowerCLIAvailableAsync()
    {
        var validation = await _powerCLIService.ValidatePowerCLIAsync();
        return validation.IsValid;
    }

    public Task<VCenterVersionInfo> GetVCenterVersionAsync(VMwareSession session, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("Getting vCenter version for session: {SessionId}", session.SessionId);

            if (!_activeSessions.TryGetValue(session.SessionId, out var enhancedSession))
            {
                throw new InvalidOperationException("Session not found or inactive");
            }

            // Use cached version info if available
            if (enhancedSession.VersionInfo != null)
            {
                enhancedSession.LastActivity = DateTime.UtcNow;
                return Task.FromResult(enhancedSession.VersionInfo);
            }

            // Otherwise get from PowerCLI session
            var versionInfo = new VCenterVersionInfo
            {
                Version = enhancedSession.PowerCLISession.ServerVersion ?? "Unknown",
                ApiVersion = enhancedSession.PowerCLISession.ApiVersion ?? "Unknown",
                ProductName = "VMware vCenter Server"
            };

            enhancedSession.VersionInfo = versionInfo;
            enhancedSession.LastActivity = DateTime.UtcNow;

            return Task.FromResult(versionInfo);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get vCenter version for session: {SessionId}", session.SessionId);
            throw;
        }
    }

    public async Task<bool> TestConnectionHealthAsync(VCenter vCenter, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("Testing connection health for vCenter: {VCenterUrl}", vCenter.Url);

            // Decrypt credentials
            var (username, password) = _credentialService.DecryptCredentials(vCenter.EncryptedCredentials);

            // Use a quick PowerCLI health check
            var connectionResult = await _powerCLIService.TestConnectionAsync(vCenter.Url, username, password, 30);
            
            var isHealthy = connectionResult.IsSuccessful;
            
            _logger.LogInformation("Connection health check for vCenter {VCenterUrl}: {IsHealthy}", 
                vCenter.Url, isHealthy ? "Healthy" : "Unhealthy");

            return isHealthy;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Connection health check failed for vCenter: {VCenterUrl}", vCenter.Url);
            return false;
        }
    }

    private string EnhanceErrorMessage(string? originalMessage, string? errorCode)
    {
        if (string.IsNullOrEmpty(originalMessage))
            return "Unknown connection error occurred";

        var enhancedMessage = originalMessage;

        switch (errorCode)
        {
            case "AUTHENTICATION_FAILED":
                enhancedMessage += "\n\nThis typically means:\n" +
                                 "• Invalid username or password\n" +
                                 "• Account is locked or disabled\n" +
                                 "• Domain authentication issues (if using domain\\user format)";
                break;
            
            case "CERTIFICATE_ERROR":
                enhancedMessage += "\n\nThis typically means:\n" +
                                 "• Self-signed SSL certificate\n" +
                                 "• Certificate trust issues\n" +
                                 "• SSL/TLS version mismatch";
                break;
            
            case "NETWORK_ERROR":
                enhancedMessage += "\n\nThis typically means:\n" +
                                 "• vCenter server is unreachable\n" +
                                 "• Network connectivity issues\n" +
                                 "• Firewall blocking connection\n" +
                                 "• Incorrect server URL or port";
                break;
            
            case "TIMEOUT":
                enhancedMessage += "\n\nThis typically means:\n" +
                                 "• vCenter server is slow to respond\n" +
                                 "• Network latency issues\n" +
                                 "• vCenter server is under heavy load";
                break;
            
            case "POWERCLI_INVALID":
                enhancedMessage += "\n\nTo fix PowerCLI issues:\n" +
                                 "• Use the 'Repair PowerCLI' function in Settings\n" +
                                 "• Ensure PowerShell execution policy allows PowerCLI\n" +
                                 "• Install/update VMware PowerCLI modules";
                break;
        }

        return enhancedMessage;
    }

    private T? GetPropertyValue<T>(PSObject psObject, string propertyName)
    {
        try
        {
            var property = psObject.Properties[propertyName];
            if (property?.Value != null)
            {
                if (typeof(T) == typeof(bool) && property.Value is string strValue)
                {
                    return (T)(object)bool.Parse(strValue);
                }
                return (T)property.Value;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to get property {PropertyName} from PSObject", propertyName);
        }
        return default(T);
    }
}

/// <summary>
/// Enhanced VMware session with PowerCLI session management
/// </summary>
internal class EnhancedVMwareSession
{
    public string SessionId { get; set; } = string.Empty;
    public string VCenterUrl { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime LastActivity { get; set; }
    public bool IsActive { get; set; }
    public VCenterVersionInfo? VersionInfo { get; set; }
    public PowerCLISession PowerCLISession { get; set; } = null!;
} 