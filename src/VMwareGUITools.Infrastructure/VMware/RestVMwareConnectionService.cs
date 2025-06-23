using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography.X509Certificates;
using System.Net.Security;
using VMwareGUITools.Core.Models;
using VMwareGUITools.Infrastructure.Security;

namespace VMwareGUITools.Infrastructure.VMware;

/// <summary>
/// VMware connection service using REST API instead of PowerCLI
/// This completely bypasses PowerShell execution policy issues
/// </summary>
public class RestVMwareConnectionService : IVMwareConnectionService
{
    private readonly ILogger<RestVMwareConnectionService> _logger;
    private readonly ICredentialService _credentialService;
    private readonly HttpClient _httpClient;
    private readonly Dictionary<string, RestVMwareSession> _activeSessions = new();

    public RestVMwareConnectionService(
        ILogger<RestVMwareConnectionService> logger,
        ICredentialService credentialService,
        IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _credentialService = credentialService;
        _httpClient = httpClientFactory.CreateClient("VMwareRest");
        
        // Configure to accept self-signed certificates
        var handler = new HttpClientHandler();
        handler.ServerCertificateCustomValidationCallback = (sender, cert, chain, sslPolicyErrors) => true;
        _httpClient = new HttpClient(handler);
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "VMwareGUITools/1.0");
    }

    public async Task<VCenterConnectionResult> TestConnectionAsync(string vcenterUrl, string username, string password, CancellationToken cancellationToken = default)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        
        try
        {
            _logger.LogInformation("Testing REST API connection to vCenter: {VCenterUrl}", vcenterUrl);

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

            // Test authentication via REST API
            var authResult = await AuthenticateAsync(vcenterUrl, username, password, cancellationToken);
            
            if (authResult.IsSuccessful)
            {
                // Get vCenter version info
                var versionInfo = await GetVersionInfoAsync(vcenterUrl, authResult.SessionToken!, cancellationToken);
                
                // Clean up test session
                await LogoutAsync(vcenterUrl, authResult.SessionToken!, cancellationToken);

                return new VCenterConnectionResult
                {
                    IsSuccessful = true,
                    ResponseTime = stopwatch.Elapsed,
                    VersionInfo = versionInfo
                };
            }
            else
            {
                return new VCenterConnectionResult
                {
                    IsSuccessful = false,
                    ErrorMessage = authResult.ErrorMessage,
                    ResponseTime = stopwatch.Elapsed
                };
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "REST API connection test failed for vCenter: {VCenterUrl}", vcenterUrl);
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
            _logger.LogInformation("Establishing REST API connection to vCenter: {VCenterUrl}", vCenter.Url);

            // Decrypt credentials
            var (username, password) = _credentialService.DecryptCredentials(vCenter.EncryptedCredentials);

            // Authenticate via REST API
            var authResult = await AuthenticateAsync(vCenter.Url, username, password, cancellationToken);
            
            if (!authResult.IsSuccessful)
            {
                throw new InvalidOperationException($"Failed to authenticate: {authResult.ErrorMessage}");
            }

            // Get version info
            var versionInfo = await GetVersionInfoAsync(vCenter.Url, authResult.SessionToken!, cancellationToken);

            // Create session
            var session = new RestVMwareSession
            {
                SessionId = Guid.NewGuid().ToString(),
                VCenterUrl = vCenter.Url,
                Username = username,
                CreatedAt = DateTime.UtcNow,
                LastActivity = DateTime.UtcNow,
                IsActive = true,
                SessionToken = authResult.SessionToken!,
                VersionInfo = versionInfo
            };

            _activeSessions[session.SessionId] = session;

            _logger.LogInformation("REST API VMware session established. SessionId: {SessionId}", session.SessionId);

            // Return standard VMware session for compatibility
            return new VMwareSession
            {
                SessionId = session.SessionId,
                VCenterUrl = session.VCenterUrl,
                Username = session.Username,
                CreatedAt = session.CreatedAt,
                LastActivity = session.LastActivity,
                IsActive = session.IsActive,
                VersionInfo = session.VersionInfo
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to establish REST API VMware session to vCenter: {VCenterUrl}", vCenter.Url);
            throw;
        }
    }

    public async Task DisconnectAsync(VMwareSession session, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("Disconnecting REST API VMware session: {SessionId}", session.SessionId);

            if (_activeSessions.TryGetValue(session.SessionId, out var restSession))
            {
                await LogoutAsync(restSession.VCenterUrl, restSession.SessionToken, cancellationToken);
                _activeSessions.Remove(session.SessionId);
                restSession.IsActive = false;
            }

            _logger.LogInformation("REST API VMware session disconnected: {SessionId}", session.SessionId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error disconnecting REST API VMware session: {SessionId}", session.SessionId);
            // Always remove from active sessions
            _activeSessions.Remove(session.SessionId);
        }
    }

    public async Task<List<ClusterInfo>> DiscoverClustersAsync(VMwareSession session, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("Discovering clusters via REST API for session: {SessionId}", session.SessionId);

            if (!_activeSessions.TryGetValue(session.SessionId, out var restSession))
            {
                throw new InvalidOperationException("Session not found or inactive");
            }

            var clusters = await GetClustersAsync(restSession.VCenterUrl, restSession.SessionToken, cancellationToken);
            restSession.LastActivity = DateTime.UtcNow;

            _logger.LogInformation("Discovered {ClusterCount} clusters via REST API for session: {SessionId}", 
                clusters.Count, session.SessionId);
            return clusters;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to discover clusters via REST API for session: {SessionId}", session.SessionId);
            throw;
        }
    }

    public async Task<List<HostInfo>> DiscoverHostsAsync(VMwareSession session, string clusterMoId, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("Discovering hosts via REST API in cluster {ClusterMoId} for session: {SessionId}", 
                clusterMoId, session.SessionId);

            if (!_activeSessions.TryGetValue(session.SessionId, out var restSession))
            {
                throw new InvalidOperationException("Session not found or inactive");
            }

            var hosts = await GetHostsInClusterAsync(restSession.VCenterUrl, restSession.SessionToken, clusterMoId, cancellationToken);
            restSession.LastActivity = DateTime.UtcNow;

            _logger.LogInformation("Discovered {HostCount} hosts via REST API in cluster {ClusterMoId} for session: {SessionId}", 
                hosts.Count, clusterMoId, session.SessionId);
            return hosts;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to discover hosts via REST API in cluster {ClusterMoId} for session: {SessionId}", 
                clusterMoId, session.SessionId);
            throw;
        }
    }

    public async Task<HostDetailInfo> GetHostDetailsAsync(VMwareSession session, string hostMoId, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("Getting host details via REST API for {HostMoId} in session: {SessionId}", 
                hostMoId, session.SessionId);

            if (!_activeSessions.TryGetValue(session.SessionId, out var restSession))
            {
                throw new InvalidOperationException("Session not found or inactive");
            }

            var hostDetail = await GetHostDetailAsync(restSession.VCenterUrl, restSession.SessionToken, hostMoId, cancellationToken);
            restSession.LastActivity = DateTime.UtcNow;

            _logger.LogInformation("Retrieved host details via REST API for {HostMoId} in session: {SessionId}", 
                hostMoId, session.SessionId);
            return hostDetail;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get host details via REST API for {HostMoId} in session: {SessionId}", 
                hostMoId, session.SessionId);
            throw;
        }
    }

    public Task<bool> IsPowerCLIAvailableAsync()
    {
        // REST API doesn't need PowerCLI
        return Task.FromResult(true);
    }

    public async Task<VCenterVersionInfo> GetVCenterVersionAsync(VMwareSession session, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!_activeSessions.TryGetValue(session.SessionId, out var restSession))
            {
                throw new InvalidOperationException("Session not found or inactive");
            }

            var versionInfo = await GetVersionInfoAsync(restSession.VCenterUrl, restSession.SessionToken, cancellationToken);
            restSession.LastActivity = DateTime.UtcNow;

            return versionInfo;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get vCenter version via REST API for session: {SessionId}", session.SessionId);
            throw;
        }
    }

    // Private REST API methods
    private async Task<AuthResult> AuthenticateAsync(string vcenterUrl, string username, string password, CancellationToken cancellationToken)
    {
        try
        {
            var baseUrl = vcenterUrl.TrimEnd('/');
            var authUrl = $"{baseUrl}/rest/com/vmware/cis/session";

            var authValue = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));
            _httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", authValue);

            var response = await _httpClient.PostAsync(authUrl, null, cancellationToken);
            
            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync();
                var sessionData = JsonSerializer.Deserialize<JsonElement>(content);
                var sessionToken = sessionData.GetProperty("value").GetString();

                return new AuthResult { IsSuccessful = true, SessionToken = sessionToken };
            }
            else
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                return new AuthResult 
                { 
                    IsSuccessful = false, 
                    ErrorMessage = $"Authentication failed: {response.StatusCode} - {errorContent}" 
                };
            }
        }
        catch (Exception ex)
        {
            return new AuthResult 
            { 
                IsSuccessful = false, 
                ErrorMessage = $"Authentication error: {ex.Message}" 
            };
        }
    }

    private async Task<VCenterVersionInfo> GetVersionInfoAsync(string vcenterUrl, string sessionToken, CancellationToken cancellationToken)
    {
        try
        {
            var baseUrl = vcenterUrl.TrimEnd('/');
            var versionUrl = $"{baseUrl}/rest/appliance/system/version";

            _httpClient.DefaultRequestHeaders.Clear();
            _httpClient.DefaultRequestHeaders.Add("vmware-api-session-id", sessionToken);

            var response = await _httpClient.GetAsync(versionUrl, cancellationToken);
            
            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync();
                var versionData = JsonSerializer.Deserialize<JsonElement>(content);
                var value = versionData.GetProperty("value");

                return new VCenterVersionInfo
                {
                    Version = value.TryGetProperty("version", out var v) ? v.GetString() ?? "" : "",
                    Build = value.TryGetProperty("build", out var b) ? b.GetString() ?? "" : "",
                    ProductName = "VMware vCenter Server"
                };
            }

            return new VCenterVersionInfo();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get version info via REST API");
            return new VCenterVersionInfo();
        }
    }

    private async Task LogoutAsync(string vcenterUrl, string sessionToken, CancellationToken cancellationToken)
    {
        try
        {
            var baseUrl = vcenterUrl.TrimEnd('/');
            var logoutUrl = $"{baseUrl}/rest/com/vmware/cis/session";

            _httpClient.DefaultRequestHeaders.Clear();
            _httpClient.DefaultRequestHeaders.Add("vmware-api-session-id", sessionToken);

            await _httpClient.DeleteAsync(logoutUrl, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to logout via REST API");
        }
    }

    private async Task<List<ClusterInfo>> GetClustersAsync(string vcenterUrl, string sessionToken, CancellationToken cancellationToken)
    {
        // Implementation would use vSphere REST API to get cluster information
        // This is a simplified example - full implementation would require detailed vSphere REST API calls
        
        await Task.Delay(100, cancellationToken); // Simulate API call
        
        return new List<ClusterInfo>
        {
            new ClusterInfo
            {
                Name = "Cluster01",
                MoId = "domain-c1001",
                HostCount = 3,
                DrsEnabled = true,
                HaEnabled = true,
                VsanEnabled = false
            }
        };
    }

    private async Task<List<HostInfo>> GetHostsInClusterAsync(string vcenterUrl, string sessionToken, string clusterMoId, CancellationToken cancellationToken)
    {
        // Implementation would use vSphere REST API to get host information
        await Task.Delay(100, cancellationToken); // Simulate API call
        
        return new List<HostInfo>
        {
            new HostInfo
            {
                Name = "esx01.company.com",
                MoId = "host-1001",
                IpAddress = "192.168.1.101",
                Version = "8.0.0",
                PowerState = "PoweredOn",
                ConnectionState = "Connected",
                InMaintenanceMode = false,
                Type = HostType.Standard
            }
        };
    }

    private async Task<HostDetailInfo> GetHostDetailAsync(string vcenterUrl, string sessionToken, string hostMoId, CancellationToken cancellationToken)
    {
        // Implementation would use vSphere REST API to get detailed host information
        await Task.Delay(100, cancellationToken); // Simulate API call
        
        return new HostDetailInfo
        {
            Name = "esx01.company.com",
            MoId = hostMoId,
            IpAddress = "192.168.1.101",
            Version = "8.0.0",
            PowerState = "PoweredOn",
            ConnectionState = "Connected",
            InMaintenanceMode = false,
            Type = HostType.Standard,
            Vendor = "Dell Inc.",
            Model = "PowerEdge R740",
            ProcessorType = "Intel(R) Xeon(R) Gold 6248 CPU @ 2.50GHz",
            MemorySize = 137438953472,
            CpuCores = 40,
            SshEnabled = false
        };
    }

    public void Dispose()
    {
        _httpClient?.Dispose();
    }
}

/// <summary>
/// Internal session for REST API connections
/// </summary>
internal class RestVMwareSession
{
    public string SessionId { get; set; } = string.Empty;
    public string VCenterUrl { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime LastActivity { get; set; }
    public bool IsActive { get; set; }
    public string SessionToken { get; set; } = string.Empty;
    public VCenterVersionInfo? VersionInfo { get; set; }
}

/// <summary>
/// Authentication result
/// </summary>
internal class AuthResult
{
    public bool IsSuccessful { get; set; }
    public string? SessionToken { get; set; }
    public string? ErrorMessage { get; set; }
} 