using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography.X509Certificates;
using System.Net.Security;
using VMwareGUITools.Core.Models;
using VMwareGUITools.Infrastructure.Security;

namespace VMwareGUITools.Infrastructure.VMware;

/// <summary>
/// Comprehensive vSphere REST API service that replaces PowerCLI functionality
/// </summary>
public class VSphereRestAPIService : IVSphereRestAPIService
{
    private readonly ILogger<VSphereRestAPIService> _logger;
    private readonly ICredentialService _credentialService;
    private readonly HttpClient _httpClient;
    private readonly VSphereRestAPIOptions _options;
    private readonly Dictionary<string, VSphereSession> _activeSessions = new();
    private readonly SemaphoreSlim _sessionSemaphore = new(1, 1);

    public VSphereRestAPIService(
        ILogger<VSphereRestAPIService> logger,
        ICredentialService credentialService,
        IHttpClientFactory httpClientFactory,
        IOptions<VSphereRestAPIOptions> options)
    {
        _logger = logger;
        _credentialService = credentialService;
        _options = options.Value;

        // Create HTTP client with custom certificate handling
        var handler = new HttpClientHandler();
        if (_options.IgnoreInvalidCertificates)
        {
            handler.ServerCertificateCustomValidationCallback = (sender, cert, chain, sslPolicyErrors) => true;
        }

        _httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(_options.RequestTimeoutSeconds)
        };
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "VMwareGUITools/2.0");
    }

    public async Task<VCenterConnectionResult> TestConnectionAsync(string vcenterUrl, string username, string password, CancellationToken cancellationToken = default)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        
        try
        {
            _logger.LogInformation("Testing vSphere REST API connection to: {VCenterUrl}", vcenterUrl);

            // Validate URL format
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

            // Test authentication
            var authResult = await AuthenticateAsync(vcenterUrl, username, password, cancellationToken);
            
            if (authResult.IsSuccessful)
            {
                // Get vCenter version and system information
                var versionInfo = await GetVCenterInfoAsync(vcenterUrl, authResult.SessionToken!, cancellationToken);
                
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
            _logger.LogError(ex, "vSphere REST API connection test failed for: {VCenterUrl}", vcenterUrl);
            return new VCenterConnectionResult
            {
                IsSuccessful = false,
                ErrorMessage = $"Connection test failed: {ex.Message}",
                ResponseTime = stopwatch.Elapsed
            };
        }
    }

    public async Task<VSphereSession> ConnectAsync(VCenter vCenter, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("Establishing vSphere REST API connection to: {VCenterUrl}", vCenter.Url);

            // Check if we already have an active session
            await _sessionSemaphore.WaitAsync(cancellationToken);
            try
            {
                var existingSession = _activeSessions.Values
                    .FirstOrDefault(s => s.VCenterUrl == vCenter.Url && s.IsActive && !s.IsExpired);

                if (existingSession != null)
                {
                    _logger.LogDebug("Reusing existing vSphere session: {SessionId}", existingSession.SessionId);
                    existingSession.LastActivity = DateTime.UtcNow;
                    return existingSession;
                }
            }
            finally
            {
                _sessionSemaphore.Release();
            }

            // Decrypt credentials
            var (username, password) = _credentialService.DecryptCredentials(vCenter.EncryptedCredentials);

            // Authenticate
            var authResult = await AuthenticateAsync(vCenter.Url, username, password, cancellationToken);
            
            if (!authResult.IsSuccessful)
            {
                throw new InvalidOperationException($"Failed to authenticate: {authResult.ErrorMessage}");
            }

            // Get system information
            var versionInfo = await GetVCenterInfoAsync(vCenter.Url, authResult.SessionToken!, cancellationToken);

            // Create new session
            var session = new VSphereSession
            {
                SessionId = Guid.NewGuid().ToString(),
                VCenterUrl = vCenter.Url,
                Username = username,
                CreatedAt = DateTime.UtcNow,
                LastActivity = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddMinutes(_options.SessionIdleTimeoutMinutes),
                IsActive = true,
                SessionToken = authResult.SessionToken!,
                VersionInfo = versionInfo
            };

            await _sessionSemaphore.WaitAsync(cancellationToken);
            try
            {
                _activeSessions[session.SessionId] = session;
            }
            finally
            {
                _sessionSemaphore.Release();
            }

            _logger.LogInformation("vSphere REST API session established. SessionId: {SessionId}", session.SessionId);
            return session;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to establish vSphere REST API session to: {VCenterUrl}", vCenter.Url);
            throw;
        }
    }

    public async Task DisconnectAsync(VSphereSession session, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("Disconnecting vSphere REST API session: {SessionId}", session.SessionId);

            if (session.IsActive)
            {
                await LogoutAsync(session.VCenterUrl, session.SessionToken, cancellationToken);
            }

            await _sessionSemaphore.WaitAsync(cancellationToken);
            try
            {
                _activeSessions.Remove(session.SessionId);
            }
            finally
            {
                _sessionSemaphore.Release();
            }

            session.IsActive = false;
            _logger.LogInformation("vSphere REST API session disconnected: {SessionId}", session.SessionId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error disconnecting vSphere REST API session: {SessionId}", session.SessionId);
            // Always cleanup
            await _sessionSemaphore.WaitAsync(cancellationToken);
            try
            {
                _activeSessions.Remove(session.SessionId);
            }
            finally
            {
                _sessionSemaphore.Release();
            }
            session.IsActive = false;
        }
    }

    public async Task<List<ClusterInfo>> DiscoverClustersAsync(VSphereSession session, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("Discovering clusters via vSphere REST API for session: {SessionId}", session.SessionId);

            await EnsureSessionValidAsync(session, cancellationToken);

            var clusters = await GetClustersAsync(session.VCenterUrl, session.SessionToken, cancellationToken);
            session.LastActivity = DateTime.UtcNow;

            _logger.LogInformation("Discovered {ClusterCount} clusters for session: {SessionId}", 
                clusters.Count, session.SessionId);
            return clusters;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to discover clusters for session: {SessionId}", session.SessionId);
            throw;
        }
    }

    public async Task<List<HostInfo>> DiscoverHostsAsync(VSphereSession session, string clusterMoId, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("Discovering hosts in cluster {ClusterMoId} for session: {SessionId}", 
                clusterMoId, session.SessionId);

            await EnsureSessionValidAsync(session, cancellationToken);

            var hosts = await GetHostsInClusterAsync(session.VCenterUrl, session.SessionToken, clusterMoId, cancellationToken);
            session.LastActivity = DateTime.UtcNow;

            _logger.LogInformation("Discovered {HostCount} hosts in cluster {ClusterMoId} for session: {SessionId}", 
                hosts.Count, clusterMoId, session.SessionId);
            return hosts;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to discover hosts in cluster {ClusterMoId} for session: {SessionId}", 
                clusterMoId, session.SessionId);
            throw;
        }
    }

    public async Task<HostDetailInfo> GetHostDetailsAsync(VSphereSession session, string hostMoId, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("Getting host details for {HostMoId} for session: {SessionId}", 
                hostMoId, session.SessionId);

            await EnsureSessionValidAsync(session, cancellationToken);

            var hostDetails = await GetHostDetailInfoAsync(session.VCenterUrl, session.SessionToken, hostMoId, cancellationToken);
            session.LastActivity = DateTime.UtcNow;

            return hostDetails;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get host details for {HostMoId} for session: {SessionId}", 
                hostMoId, session.SessionId);
            throw;
        }
    }

    public async Task<VSphereApiResult> ExecuteCheckAsync(VSphereSession session, string hostMoId, string checkType, Dictionary<string, object>? parameters = null, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("Executing check {CheckType} on host {HostMoId} for session: {SessionId}", 
                checkType, hostMoId, session.SessionId);

            await EnsureSessionValidAsync(session, cancellationToken);

            var result = checkType.ToLower() switch
            {
                "host-performance" => await GetHostPerformanceAsync(session, hostMoId, parameters, cancellationToken),
                "host-hardware" => await GetHostHardwareAsync(session, hostMoId, parameters, cancellationToken),
                "host-networking" => await GetHostNetworkingAsync(session, hostMoId, parameters, cancellationToken),
                "host-storage" => await GetHostStorageAsync(session, hostMoId, parameters, cancellationToken),
                "host-security" => await GetHostSecurityAsync(session, hostMoId, parameters, cancellationToken),
                "host-configuration" => await GetHostConfigurationAsync(session, hostMoId, parameters, cancellationToken),
                _ => throw new NotSupportedException($"Check type '{checkType}' is not supported")
            };

            session.LastActivity = DateTime.UtcNow;
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to execute check {CheckType} on host {HostMoId} for session: {SessionId}", 
                checkType, hostMoId, session.SessionId);
            throw;
        }
    }

    // Private helper methods
    private async Task<AuthResult> AuthenticateAsync(string vcenterUrl, string username, string password, CancellationToken cancellationToken)
    {
        try
        {
            var baseUrl = vcenterUrl.TrimEnd('/');
            var authUrl = $"{baseUrl}/api/session";

            var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));
            
            using var request = new HttpRequestMessage(HttpMethod.Post, authUrl);
            request.Headers.Add("Authorization", $"Basic {credentials}");

            var response = await _httpClient.SendAsync(request, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                var sessionToken = await response.Content.ReadAsStringAsync();
                sessionToken = sessionToken.Trim('"'); // Remove quotes from JSON string

                return new AuthResult
                {
                    IsSuccessful = true,
                    SessionToken = sessionToken
                };
            }
            else
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                return new AuthResult
                {
                    IsSuccessful = false,
                    ErrorMessage = $"Authentication failed: {response.ReasonPhrase}. Details: {errorContent}"
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

    private async Task<VCenterVersionInfo> GetVCenterInfoAsync(string vcenterUrl, string sessionToken, CancellationToken cancellationToken)
    {
        try
        {
            var baseUrl = vcenterUrl.TrimEnd('/');
            var infoUrl = $"{baseUrl}/api/appliance/system/version";

            using var request = new HttpRequestMessage(HttpMethod.Get, infoUrl);
            request.Headers.Add("vmware-api-session-id", sessionToken);

            var response = await _httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();

            var content = await response.Content.ReadAsStringAsync();
            var versionData = JsonSerializer.Deserialize<JsonElement>(content);

            return new VCenterVersionInfo
            {
                Version = versionData.GetProperty("version").GetString() ?? "Unknown",
                Build = versionData.GetProperty("build").GetString() ?? "Unknown",
                Product = versionData.GetProperty("product").GetString() ?? "vCenter Server",
                Summary = versionData.GetProperty("summary").GetString() ?? "VMware vCenter Server"
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get vCenter version info");
            return new VCenterVersionInfo
            {
                Version = "Unknown",
                Build = "Unknown",
                Product = "vCenter Server",
                Summary = "VMware vCenter Server"
            };
        }
    }

    private async Task LogoutAsync(string vcenterUrl, string sessionToken, CancellationToken cancellationToken)
    {
        try
        {
            var baseUrl = vcenterUrl.TrimEnd('/');
            var logoutUrl = $"{baseUrl}/api/session";

            using var request = new HttpRequestMessage(HttpMethod.Delete, logoutUrl);
            request.Headers.Add("vmware-api-session-id", sessionToken);

            await _httpClient.SendAsync(request, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error during session logout");
        }
    }

    private async Task EnsureSessionValidAsync(VSphereSession session, CancellationToken cancellationToken)
    {
        if (!session.IsActive || session.IsExpired)
        {
            throw new InvalidOperationException("Session is not active or has expired");
        }

        // TODO: Implement session refresh if needed
    }

    private async Task<List<ClusterInfo>> GetClustersAsync(string vcenterUrl, string sessionToken, CancellationToken cancellationToken)
    {
        var baseUrl = vcenterUrl.TrimEnd('/');
        var clustersUrl = $"{baseUrl}/api/vcenter/cluster";

        using var request = new HttpRequestMessage(HttpMethod.Get, clustersUrl);
        request.Headers.Add("vmware-api-session-id", sessionToken);

        var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadAsStringAsync();
        var clustersData = JsonSerializer.Deserialize<JsonElement>(content);

        var clusters = new List<ClusterInfo>();
        foreach (var cluster in clustersData.EnumerateArray())
        {
            clusters.Add(new ClusterInfo
            {
                MoId = cluster.GetProperty("cluster").GetString() ?? "",
                Name = cluster.GetProperty("name").GetString() ?? "",
                DrsEnabled = cluster.TryGetProperty("drs_enabled", out var drs) ? drs.GetBoolean() : false,
                HaEnabled = cluster.TryGetProperty("ha_enabled", out var ha) ? ha.GetBoolean() : false
            });
        }

        return clusters;
    }

    private async Task<List<HostInfo>> GetHostsInClusterAsync(string vcenterUrl, string sessionToken, string clusterMoId, CancellationToken cancellationToken)
    {
        var baseUrl = vcenterUrl.TrimEnd('/');
        var hostsUrl = $"{baseUrl}/api/vcenter/host?clusters={clusterMoId}";

        using var request = new HttpRequestMessage(HttpMethod.Get, hostsUrl);
        request.Headers.Add("vmware-api-session-id", sessionToken);

        var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadAsStringAsync();
        var hostsData = JsonSerializer.Deserialize<JsonElement>(content);

        var hosts = new List<HostInfo>();
        foreach (var host in hostsData.EnumerateArray())
        {
            hosts.Add(new HostInfo
            {
                MoId = host.GetProperty("host").GetString() ?? "",
                Name = host.GetProperty("name").GetString() ?? "",
                ConnectionState = host.GetProperty("connection_state").GetString() ?? "",
                PowerState = host.GetProperty("power_state").GetString() ?? ""
            });
        }

        return hosts;
    }

    private async Task<HostDetailInfo> GetHostDetailInfoAsync(string vcenterUrl, string sessionToken, string hostMoId, CancellationToken cancellationToken)
    {
        var baseUrl = vcenterUrl.TrimEnd('/');
        var hostUrl = $"{baseUrl}/api/vcenter/host/{hostMoId}";

        using var request = new HttpRequestMessage(HttpMethod.Get, hostUrl);
        request.Headers.Add("vmware-api-session-id", sessionToken);

        var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadAsStringAsync();
        var hostData = JsonSerializer.Deserialize<JsonElement>(content);

        return new HostDetailInfo
        {
            MoId = hostMoId,
            Name = hostData.GetProperty("name").GetString() ?? "",
            ConnectionState = hostData.GetProperty("connection_state").GetString() ?? "",
            PowerState = hostData.GetProperty("power_state").GetString() ?? "",
            // Add more properties as needed from the REST API response
        };
    }

    // Check execution methods
    private async Task<VSphereApiResult> GetHostPerformanceAsync(VSphereSession session, string hostMoId, Dictionary<string, object>? parameters, CancellationToken cancellationToken)
    {
        try
        {
            var baseUrl = session.VCenterUrl.TrimEnd('/');
            
            // Get CPU info
            var cpuUrl = $"{baseUrl}/api/vcenter/host/{hostMoId}/hardware/cpu";
            using var cpuRequest = new HttpRequestMessage(HttpMethod.Get, cpuUrl);
            cpuRequest.Headers.Add("vmware-api-session-id", session.SessionToken);
            
            var cpuResponse = await _httpClient.SendAsync(cpuRequest, cancellationToken);
            cpuResponse.EnsureSuccessStatusCode();
            
            var cpuContent = await cpuResponse.Content.ReadAsStringAsync();
            var cpuData = JsonSerializer.Deserialize<JsonElement>(cpuContent);

            // Get Memory info
            var memUrl = $"{baseUrl}/api/vcenter/host/{hostMoId}/hardware/memory";
            using var memRequest = new HttpRequestMessage(HttpMethod.Get, memUrl);
            memRequest.Headers.Add("vmware-api-session-id", session.SessionToken);
            
            var memResponse = await _httpClient.SendAsync(memRequest, cancellationToken);
            memResponse.EnsureSuccessStatusCode();
            
            var memContent = await memResponse.Content.ReadAsStringAsync();
            var memData = JsonSerializer.Deserialize<JsonElement>(memContent);

            var result = new VSphereApiResult
            {
                IsSuccess = true,
                Data = $"CPU Cores: {cpuData.GetProperty("cores").GetInt32()}, Memory: {memData.GetProperty("size_MiB").GetInt64()} MiB",
                Timestamp = DateTime.UtcNow
            };

            result.Properties["cpu_cores"] = cpuData.GetProperty("cores").GetInt32();
            result.Properties["memory_mib"] = memData.GetProperty("size_MiB").GetInt64();

            return result;
        }
        catch (Exception ex)
        {
            return new VSphereApiResult
            {
                IsSuccess = false,
                ErrorMessage = $"Failed to get host performance data: {ex.Message}",
                Timestamp = DateTime.UtcNow
            };
        }
    }

    private async Task<VSphereApiResult> GetHostHardwareAsync(VSphereSession session, string hostMoId, Dictionary<string, object>? parameters, CancellationToken cancellationToken)
    {
        try
        {
            var baseUrl = session.VCenterUrl.TrimEnd('/');
            var hardwareUrl = $"{baseUrl}/api/vcenter/host/{hostMoId}/hardware";
            
            using var request = new HttpRequestMessage(HttpMethod.Get, hardwareUrl);
            request.Headers.Add("vmware-api-session-id", session.SessionToken);
            
            var response = await _httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            
            var content = await response.Content.ReadAsStringAsync();
            var hwData = JsonSerializer.Deserialize<JsonElement>(content);

            return new VSphereApiResult
            {
                IsSuccess = true,
                Data = $"Hardware vendor: {hwData.GetProperty("vendor").GetString()}, Model: {hwData.GetProperty("model").GetString()}",
                Timestamp = DateTime.UtcNow
            };
        }
        catch (Exception ex)
        {
            return new VSphereApiResult
            {
                IsSuccess = false,
                ErrorMessage = $"Failed to get host hardware data: {ex.Message}",
                Timestamp = DateTime.UtcNow
            };
        }
    }

    private async Task<VSphereApiResult> GetHostNetworkingAsync(VSphereSession session, string hostMoId, Dictionary<string, object>? parameters, CancellationToken cancellationToken)
    {
        try
        {
            var baseUrl = session.VCenterUrl.TrimEnd('/');
            var networkUrl = $"{baseUrl}/api/vcenter/host/{hostMoId}/networking";
            
            using var request = new HttpRequestMessage(HttpMethod.Get, networkUrl);
            request.Headers.Add("vmware-api-session-id", session.SessionToken);
            
            var response = await _httpClient.SendAsync(request, cancellationToken);
            
            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync();
                return new VSphereApiResult
                {
                    IsSuccess = true,
                    Data = $"Network configuration retrieved: {content.Length} bytes",
                    Timestamp = DateTime.UtcNow
                };
            }
            else
            {
                return new VSphereApiResult
                {
                    IsSuccess = false,
                    ErrorMessage = $"Failed to get networking data: {response.StatusCode}",
                    Timestamp = DateTime.UtcNow
                };
            }
        }
        catch (Exception ex)
        {
            return new VSphereApiResult
            {
                IsSuccess = false,
                ErrorMessage = $"Failed to get host networking data: {ex.Message}",
                Timestamp = DateTime.UtcNow
            };
        }
    }

    private async Task<VSphereApiResult> GetHostStorageAsync(VSphereSession session, string hostMoId, Dictionary<string, object>? parameters, CancellationToken cancellationToken)
    {
        try
        {
            var baseUrl = session.VCenterUrl.TrimEnd('/');
            var storageUrl = $"{baseUrl}/api/vcenter/host/{hostMoId}/storage";
            
            using var request = new HttpRequestMessage(HttpMethod.Get, storageUrl);
            request.Headers.Add("vmware-api-session-id", session.SessionToken);
            
            var response = await _httpClient.SendAsync(request, cancellationToken);
            
            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync();
                return new VSphereApiResult
                {
                    IsSuccess = true,
                    Data = $"Storage configuration retrieved: {content.Length} bytes",
                    Timestamp = DateTime.UtcNow
                };
            }
            else
            {
                return new VSphereApiResult
                {
                    IsSuccess = false,
                    ErrorMessage = $"Failed to get storage data: {response.StatusCode}",
                    Timestamp = DateTime.UtcNow
                };
            }
        }
        catch (Exception ex)
        {
            return new VSphereApiResult
            {
                IsSuccess = false,
                ErrorMessage = $"Failed to get host storage data: {ex.Message}",
                Timestamp = DateTime.UtcNow
            };
        }
    }

    private async Task<VSphereApiResult> GetHostSecurityAsync(VSphereSession session, string hostMoId, Dictionary<string, object>? parameters, CancellationToken cancellationToken)
    {
        try
        {
            var baseUrl = session.VCenterUrl.TrimEnd('/');
            var securityUrl = $"{baseUrl}/api/vcenter/host/{hostMoId}/services";
            
            using var request = new HttpRequestMessage(HttpMethod.Get, securityUrl);
            request.Headers.Add("vmware-api-session-id", session.SessionToken);
            
            var response = await _httpClient.SendAsync(request, cancellationToken);
            
            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync();
                return new VSphereApiResult
                {
                    IsSuccess = true,
                    Data = $"Security/Services configuration retrieved: {content.Length} bytes",
                    Timestamp = DateTime.UtcNow
                };
            }
            else
            {
                return new VSphereApiResult
                {
                    IsSuccess = false,
                    ErrorMessage = $"Failed to get security data: {response.StatusCode}",
                    Timestamp = DateTime.UtcNow
                };
            }
        }
        catch (Exception ex)
        {
            return new VSphereApiResult
            {
                IsSuccess = false,
                ErrorMessage = $"Failed to get host security data: {ex.Message}",
                Timestamp = DateTime.UtcNow
            };
        }
    }

    private async Task<VSphereApiResult> GetHostConfigurationAsync(VSphereSession session, string hostMoId, Dictionary<string, object>? parameters, CancellationToken cancellationToken)
    {
        try
        {
            var baseUrl = session.VCenterUrl.TrimEnd('/');
            var configUrl = $"{baseUrl}/api/vcenter/host/{hostMoId}";
            
            using var request = new HttpRequestMessage(HttpMethod.Get, configUrl);
            request.Headers.Add("vmware-api-session-id", session.SessionToken);
            
            var response = await _httpClient.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            
            var content = await response.Content.ReadAsStringAsync();
            var configData = JsonSerializer.Deserialize<JsonElement>(content);

            return new VSphereApiResult
            {
                IsSuccess = true,
                Data = $"Host configuration: Name={configData.GetProperty("name").GetString()}, State={configData.GetProperty("connection_state").GetString()}",
                Timestamp = DateTime.UtcNow
            };
        }
        catch (Exception ex)
        {
            return new VSphereApiResult
            {
                IsSuccess = false,
                ErrorMessage = $"Failed to get host configuration data: {ex.Message}",
                Timestamp = DateTime.UtcNow
            };
        }
    }

    public void Dispose()
    {
        // Cleanup all active sessions
        foreach (var session in _activeSessions.Values.ToList())
        {
            try
            {
                DisconnectAsync(session, CancellationToken.None).Wait(TimeSpan.FromSeconds(5));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error disposing session {SessionId}", session.SessionId);
            }
        }
        
        _activeSessions.Clear();
        _sessionSemaphore.Dispose();
        _httpClient.Dispose();
    }
}

// Configuration options
public class VSphereRestAPIOptions
{
    public const string SectionName = "vSphereRestAPI";

    public int ConnectionTimeoutSeconds { get; set; } = 60;
    public int RequestTimeoutSeconds { get; set; } = 300;
    public bool IgnoreInvalidCertificates { get; set; } = true;
    public bool EnableVerboseLogging { get; set; } = false;
    public int MaxConcurrentConnections { get; set; } = 10;
    public bool EnableSessionPooling { get; set; } = true;
    public int SessionPoolSize { get; set; } = 5;
    public int SessionIdleTimeoutMinutes { get; set; } = 30;
    public int RetryAttempts { get; set; } = 3;
    public int RetryDelaySeconds { get; set; } = 2;
}

// Session and result models
public class VSphereSession
{
    public string SessionId { get; set; } = string.Empty;
    public string VCenterUrl { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime LastActivity { get; set; }
    public DateTime ExpiresAt { get; set; }
    public bool IsActive { get; set; }
    public string SessionToken { get; set; } = string.Empty;
    public VCenterVersionInfo? VersionInfo { get; set; }

    public bool IsExpired => DateTime.UtcNow > ExpiresAt;
}

public class VSphereApiResult
{
    public bool IsSuccess { get; set; }
    public string Data { get; set; } = string.Empty;
    public string? ErrorMessage { get; set; }
    public DateTime Timestamp { get; set; }
    public Dictionary<string, object> Properties { get; set; } = new();
}

internal class AuthResult
{
    public bool IsSuccessful { get; set; }
    public string? SessionToken { get; set; }
    public string? ErrorMessage { get; set; }
} 