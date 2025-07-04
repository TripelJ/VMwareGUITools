using Microsoft.Extensions.Logging;
using System.Management.Automation;
using VMwareGUITools.Core.Models;
using VMwareGUITools.Infrastructure.PowerShell;
using VMwareGUITools.Infrastructure.Security;

namespace VMwareGUITools.Infrastructure.VMware;

/// <summary>
/// Implementation of VMware vCenter connection service using PowerCLI
/// </summary>
public class VMwareConnectionService : IVMwareConnectionService
{
    private readonly ILogger<VMwareConnectionService> _logger;
    private readonly ICredentialService _credentialService;
    private readonly IPowerShellService _powerShellService;
    private readonly Dictionary<string, VMwareSession> _activeSessions = new();

    public VMwareConnectionService(
        ILogger<VMwareConnectionService> logger, 
        ICredentialService credentialService,
        IPowerShellService powerShellService)
    {
        _logger = logger;
        _credentialService = credentialService;
        _powerShellService = powerShellService;
    }

    public async Task<VCenterConnectionResult> TestConnectionAsync(string vcenterUrl, string username, string password, CancellationToken cancellationToken = default)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        
        try
        {
            _logger.LogInformation("Testing connection to vCenter: {VCenterUrl}", vcenterUrl);

            // Basic URL validation
            if (!Uri.TryCreate(vcenterUrl, UriKind.Absolute, out var uri) || 
                (uri.Scheme != "https" && uri.Scheme != "http"))
            {
                return new VCenterConnectionResult
                {
                    IsSuccessful = false,
                    ErrorMessage = "Invalid vCenter URL format",
                    ResponseTime = stopwatch.Elapsed
                };
            }

            // Check if PowerCLI is available first
            if (!await _powerShellService.IsPowerCLIAvailableAsync())
            {
                _logger.LogError("PowerCLI modules are not available. This is typically due to PowerShell execution policy restrictions.");
                return new VCenterConnectionResult
                {
                    IsSuccessful = false,
                    ErrorMessage = "PowerCLI modules are not available. This is typically due to PowerShell execution policy restrictions. Please run 'Set-ExecutionPolicy -ExecutionPolicy RemoteSigned -Scope CurrentUser -Force' in PowerShell to fix this issue.",
                    ResponseTime = stopwatch.Elapsed
                };
            }

            // Test PowerCLI connection
            var script = @"
                param($Server, $Username, $Password)
                
                try {
                    # Set invalid certificate action to ignore for self-signed certificates
                    Set-PowerCLIConfiguration -InvalidCertificateAction Ignore -Confirm:$false -ErrorAction SilentlyContinue
                    
                    # Connect to vCenter
                    $connection = Connect-VIServer -Server $Server -User $Username -Password $Password -ErrorAction Stop
                    
                    if ($connection) {
                        # Test basic functionality by getting vCenter info
                        $vcInfo = Get-View -ViewType ServiceInstance -ErrorAction Stop
                        $result = [PSCustomObject]@{
                            IsConnected = $true
                            Version = $vcInfo.Content.About.Version
                            Build = $vcInfo.Content.About.Build
                            InstanceUuid = $vcInfo.Content.About.InstanceUuid
                            ProductName = $vcInfo.Content.About.Name
                            ApiVersion = $vcInfo.Content.About.ApiVersion
                            Error = $null
                        }
                        
                        # Disconnect
                        Disconnect-VIServer -Server $Server -Confirm:$false -ErrorAction SilentlyContinue
                        
                        return $result
                    } else {
                        return [PSCustomObject]@{
                            IsConnected = $false
                            Error = 'Failed to establish connection'
                        }
                    }
                } catch {
                    # Check if the error is related to PowerCLI availability
                    if ($_.Exception.Message -like '*not recognized*' -or $_.Exception.Message -like '*cannot be loaded*') {
                        return [PSCustomObject]@{
                            IsConnected = $false
                            Error = ""PowerCLI cmdlets are not available. This is typically due to PowerShell execution policy restrictions. Error: $($_.Exception.Message)""
                        }
                    } else {
                        return [PSCustomObject]@{
                            IsConnected = $false
                            Error = $_.Exception.Message
                        }
                    }
                }
            ";

            var parameters = new Dictionary<string, object>
            {
                ["Server"] = vcenterUrl,
                ["Username"] = username,
                ["Password"] = password
            };

            var psResult = await _powerShellService.ExecutePowerCLICommandAsync(script, parameters, 60, cancellationToken);

            if (psResult.IsSuccess && psResult.Objects.Count > 0)
            {
                if (psResult.Objects[0] is PSObject psObj)
                {
                    var isConnected = bool.Parse(psObj.Properties["IsConnected"]?.Value?.ToString() ?? "false");
                    if (isConnected)
                    {
                        var versionInfo = new VCenterVersionInfo
                        {
                            Version = psObj.Properties["Version"]?.Value?.ToString() ?? "",
                            Build = psObj.Properties["Build"]?.Value?.ToString() ?? "",
                            InstanceUuid = psObj.Properties["InstanceUuid"]?.Value?.ToString() ?? "",
                            ProductName = psObj.Properties["ProductName"]?.Value?.ToString() ?? "",
                            ApiVersion = psObj.Properties["ApiVersion"]?.Value?.ToString() ?? ""
                        };

                        _logger.LogInformation("Successfully connected to vCenter: {VCenterUrl} (Version: {Version}, Build: {Build})", 
                            vcenterUrl, versionInfo.Version, versionInfo.Build);

                        return new VCenterConnectionResult
                        {
                            IsSuccessful = true,
                            ResponseTime = stopwatch.Elapsed,
                            VersionInfo = versionInfo
                        };
                    }
                    else
                    {
                        var error = psObj.Properties["Error"]?.Value?.ToString() ?? "Unknown error";
                        _logger.LogWarning("Failed to connect to vCenter: {VCenterUrl} - {Error}", vcenterUrl, error);
                        
                        // Enhance error message if it's related to PowerCLI
                        if (error.Contains("PowerCLI") || error.Contains("not recognized") || error.Contains("cannot be loaded"))
                        {
                            error += "\n\nTo fix this issue, please run the following command in PowerShell:\nSet-ExecutionPolicy -ExecutionPolicy RemoteSigned -Scope CurrentUser -Force";
                        }
                        
                        return new VCenterConnectionResult
                        {
                            IsSuccessful = false,
                            ErrorMessage = error,
                            ResponseTime = stopwatch.Elapsed
                        };
                    }
                }
            }

            var errorMessage = psResult.ErrorOutput;
            
            // Enhance error message if it's related to execution policy
            if (errorMessage.Contains("cannot be loaded because running scripts is disabled") || 
                errorMessage.Contains("execution of scripts is disabled") ||
                errorMessage.Contains("ExecutionPolicy"))
            {
                errorMessage = "PowerShell execution policy is preventing PowerCLI modules from loading. " +
                              "Please run 'Set-ExecutionPolicy -ExecutionPolicy RemoteSigned -Scope CurrentUser -Force' in PowerShell to fix this issue.\n\n" +
                              "Original error: " + errorMessage;
            }

            _logger.LogError("Failed to test connection to vCenter: {VCenterUrl} - {Error}", vcenterUrl, errorMessage);
            return new VCenterConnectionResult
            {
                IsSuccessful = false,
                ErrorMessage = errorMessage,
                ResponseTime = stopwatch.Elapsed
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Connection test failed for vCenter: {VCenterUrl}", vcenterUrl);
            return new VCenterConnectionResult
            {
                IsSuccessful = false,
                ErrorMessage = ex.Message,
                ResponseTime = stopwatch.Elapsed
            };
        }
    }

    public async Task<VMwareSession> ConnectAsync(VCenter vCenter, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("Connecting to vCenter: {VCenterName} ({VCenterUrl})", vCenter.Name, vCenter.Url);

            var (username, password) = _credentialService.DecryptCredentials(vCenter.EncryptedCredentials);

            // TODO: Implement actual PowerCLI connection
            // For now, create a simulated session
            await Task.Delay(500, cancellationToken); // Simulate connection time

            var session = new VMwareSession
            {
                SessionId = Guid.NewGuid().ToString(),
                VCenterUrl = vCenter.Url,
                Username = username,
                CreatedAt = DateTime.UtcNow,
                LastActivity = DateTime.UtcNow,
                IsActive = true,
                VersionInfo = new VCenterVersionInfo
                {
                    Version = "8.0.0",
                    Build = "20920323",
                    ApiVersion = "8.0.0.0",
                    ProductName = "VMware vCenter Server",
                    InstanceUuid = Guid.NewGuid().ToString()
                }
            };

            _activeSessions[session.SessionId] = session;

            _logger.LogInformation("Successfully connected to vCenter: {VCenterName} with session: {SessionId}", 
                vCenter.Name, session.SessionId);

            return session;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect to vCenter: {VCenterName}", vCenter.Name);
            throw;
        }
    }

    public async Task DisconnectAsync(VMwareSession session, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("Disconnecting from vCenter session: {SessionId}", session.SessionId);

            // TODO: Implement actual PowerCLI disconnection
            await Task.Delay(100, cancellationToken); // Simulate disconnection time

            session.IsActive = false;
            _activeSessions.Remove(session.SessionId);

            _logger.LogInformation("Successfully disconnected from vCenter session: {SessionId}", session.SessionId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to disconnect from vCenter session: {SessionId}", session.SessionId);
            throw;
        }
    }

    public async Task<List<ClusterInfo>> DiscoverClustersAsync(VMwareSession session, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("Discovering clusters for session: {SessionId}", session.SessionId);

            var script = @"
                param($Server)
                
                try {
                    # Connect to vCenter (assuming we're using the same session)
                    $clusters = Get-Cluster | ForEach-Object {
                        $cluster = $_
                        $hosts = Get-VMHost -Location $cluster
                        $clusterView = Get-View $cluster
                        
                        # Check for vSAN
                        $vsanEnabled = $false
                        try {
                            $vsanConfig = Get-VsanClusterConfiguration -Cluster $cluster -ErrorAction SilentlyContinue
                            $vsanEnabled = $vsanConfig.VsanEnabled
                        } catch {
                            # vSAN module might not be available
                            $vsanEnabled = $false
                        }
                        
                        [PSCustomObject]@{
                            Name = $cluster.Name
                            MoId = $cluster.Id
                            HostCount = $hosts.Count
                            ParentFolder = $cluster.ParentFolder.Name
                            DrsEnabled = $cluster.DrsEnabled
                            HaEnabled = $cluster.HAEnabled
                            VsanEnabled = $vsanEnabled
                        }
                    }
                    
                    return $clusters
                } catch {
                    Write-Error $_.Exception.Message
                    return @()
                }
            ";

            var parameters = new Dictionary<string, object>
            {
                ["Server"] = session.VCenterUrl
            };

            var psResult = await _powerShellService.ExecutePowerCLICommandAsync(script, parameters, 120, cancellationToken);

            var clusters = new List<ClusterInfo>();

            if (psResult.IsSuccess)
            {
                foreach (var obj in psResult.Objects)
                {
                    if (obj is PSObject psObj)
                    {
                        var cluster = new ClusterInfo
                        {
                            Name = psObj.Properties["Name"]?.Value?.ToString() ?? "",
                            MoId = psObj.Properties["MoId"]?.Value?.ToString() ?? "",
                            HostCount = int.Parse(psObj.Properties["HostCount"]?.Value?.ToString() ?? "0"),
                            ParentFolder = psObj.Properties["ParentFolder"]?.Value?.ToString(),
                            DrsEnabled = bool.Parse(psObj.Properties["DrsEnabled"]?.Value?.ToString() ?? "false"),
                            HaEnabled = bool.Parse(psObj.Properties["HaEnabled"]?.Value?.ToString() ?? "false"),
                            VsanEnabled = bool.Parse(psObj.Properties["VsanEnabled"]?.Value?.ToString() ?? "false")
                        };
                        clusters.Add(cluster);
                    }
                }
            }
            else
            {
                _logger.LogError("Failed to discover clusters for session: {SessionId} - {Error}", session.SessionId, psResult.ErrorOutput);
            }

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

    public async Task<List<HostInfo>> DiscoverHostsAsync(VMwareSession session, string clusterMoId, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("Discovering hosts in cluster: {ClusterMoId} for session: {SessionId}", 
                clusterMoId, session.SessionId);

            // TODO: Implement actual PowerCLI host discovery
            await Task.Delay(800, cancellationToken); // Simulate discovery time

            // Return sample host data
            var hosts = new List<HostInfo>
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
                },
                new HostInfo
                {
                    Name = "esx02.company.com",
                    MoId = "host-1002",
                    IpAddress = "192.168.1.102",
                    Version = "8.0.0",
                    PowerState = "PoweredOn",
                    ConnectionState = "Connected",
                    InMaintenanceMode = false,
                    Type = HostType.VsanNode
                }
            };

            session.LastActivity = DateTime.UtcNow;

            _logger.LogInformation("Discovered {HostCount} hosts in cluster: {ClusterMoId}", 
                hosts.Count, clusterMoId);

            return hosts;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to discover hosts in cluster: {ClusterMoId}", clusterMoId);
            throw;
        }
    }

    public async Task<HostDetailInfo> GetHostDetailsAsync(VMwareSession session, string hostMoId, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("Getting host details for: {HostMoId}", hostMoId);

            // TODO: Implement actual PowerCLI host details retrieval
            await Task.Delay(500, cancellationToken);

            var hostDetails = new HostDetailInfo
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
                MemorySize = 137438953472, // 128 GB
                CpuCores = 40,
                NetworkAdapters = new List<string> { "vmnic0", "vmnic1", "vmnic2", "vmnic3" },
                StorageAdapters = new List<string> { "vmhba0", "vmhba1", "vmhba2" },
                SshEnabled = false
            };

            session.LastActivity = DateTime.UtcNow;

            return hostDetails;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get host details for: {HostMoId}", hostMoId);
            throw;
        }
    }

    public async Task<List<DatastoreInfo>> DiscoverDatastoresAsync(VMwareSession session, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("Discovering datastores for session: {SessionId}", session.SessionId);

            var script = @"
                param($Server)
                
                try {
                    # Connect to vCenter (assuming we're using the same session)
                    $datastores = Get-Datastore | ForEach-Object {
                        $datastore = $_
                        $hosts = Get-VMHost -Datastore $datastore
                        
                        [PSCustomObject]@{
                            Name = $datastore.Name
                            MoId = $datastore.Id
                            Type = $datastore.Type
                            CapacityMB = [math]::Round($datastore.CapacityMB)
                            FreeMB = [math]::Round($datastore.FreeSpaceMB)
                            Accessible = ($datastore.State -eq 'Available')
                            Url = if ($datastore.RemoteHost) { $datastore.RemoteHost + ':' + $datastore.RemotePath } else { $datastore.Name }
                            MaintenanceMode = ($datastore.ExtensionData.Summary.MaintenanceMode -eq 'inMaintenance')
                            HostNames = ($hosts | Select-Object -ExpandProperty Name) -join ','
                        }
                    }
                    
                    return $datastores
                } catch {
                    Write-Error $_.Exception.Message
                    return @()
                }
            ";

            var parameters = new Dictionary<string, object>
            {
                ["Server"] = session.VCenterUrl
            };

            var psResult = await _powerShellService.ExecutePowerCLICommandAsync(script, parameters, 120, cancellationToken);

            var datastores = new List<DatastoreInfo>();

            if (psResult.IsSuccess)
            {
                foreach (var obj in psResult.Objects)
                {
                    if (obj is PSObject psObj)
                    {
                        var hostNamesString = psObj.Properties["HostNames"]?.Value?.ToString() ?? "";
                        var hostNames = hostNamesString.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();

                        var datastore = new DatastoreInfo
                        {
                            Name = psObj.Properties["Name"]?.Value?.ToString() ?? "",
                            MoId = psObj.Properties["MoId"]?.Value?.ToString() ?? "",
                            Type = psObj.Properties["Type"]?.Value?.ToString() ?? "",
                            CapacityMB = long.Parse(psObj.Properties["CapacityMB"]?.Value?.ToString() ?? "0"),
                            FreeMB = long.Parse(psObj.Properties["FreeMB"]?.Value?.ToString() ?? "0"),
                            Accessible = bool.Parse(psObj.Properties["Accessible"]?.Value?.ToString() ?? "true"),
                            Url = psObj.Properties["Url"]?.Value?.ToString() ?? "",
                            MaintenanceMode = bool.Parse(psObj.Properties["MaintenanceMode"]?.Value?.ToString() ?? "false"),
                            HostNames = hostNames
                        };
                        datastores.Add(datastore);
                    }
                }
            }
            else
            {
                _logger.LogError("Failed to discover datastores for session: {SessionId} - {Error}", session.SessionId, psResult.ErrorOutput);
            }

            session.LastActivity = DateTime.UtcNow;

            _logger.LogInformation("Discovered {DatastoreCount} datastores for session: {SessionId}", 
                datastores.Count, session.SessionId);

            return datastores;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to discover datastores for session: {SessionId}", session.SessionId);
            throw;
        }
    }

    public async Task<bool> IsPowerCLIAvailableAsync()
    {
        try
        {
            _logger.LogInformation("Checking PowerCLI availability");
            return await _powerShellService.IsPowerCLIAvailableAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to check PowerCLI availability");
            return false;
        }
    }

    public async Task<VCenterVersionInfo> GetVCenterVersionAsync(VMwareSession session, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("Getting vCenter version for session: {SessionId}", session.SessionId);

            // TODO: Implement actual PowerCLI version retrieval
            await Task.Delay(200, cancellationToken);

            if (session.VersionInfo != null)
            {
                return session.VersionInfo;
            }

            // Return cached version info from session
            return new VCenterVersionInfo
            {
                Version = "8.0.0",
                Build = "20920323",
                ApiVersion = "8.0.0.0",
                ProductName = "VMware vCenter Server",
                InstanceUuid = Guid.NewGuid().ToString()
            };
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
            _logger.LogDebug("Testing connection health for vCenter: {VCenterName}", vCenter.Name);

            // Decrypt credentials
            var (username, password) = _credentialService.DecryptCredentials(vCenter.EncryptedCredentials);

            // Basic URL validation
            if (!Uri.TryCreate(vCenter.Url, UriKind.Absolute, out var uri) || 
                (uri.Scheme != "https" && uri.Scheme != "http"))
            {
                return false;
            }

            // Quick connection test with shorter timeout
            var script = @"
                param($Server, $Username, $Password)
                
                try {
                    # Set timeout and configure PowerCLI
                    Set-PowerCLIConfiguration -InvalidCertificateAction Ignore -Confirm:$false -ErrorAction SilentlyContinue
                    
                    # Quick connect test
                    $connection = Connect-VIServer -Server $Server -User $Username -Password $Password -ErrorAction Stop
                    
                    if ($connection) {
                        # Disconnect immediately
                        Disconnect-VIServer -Server $Server -Confirm:$false -ErrorAction SilentlyContinue
                        return $true
                    }
                    return $false
                } catch {
                    return $false
                }
            ";

            var parameters = new Dictionary<string, object>
            {
                ["Server"] = vCenter.Url,
                ["Username"] = username,
                ["Password"] = password
            };

            var psResult = await _powerShellService.ExecutePowerCLICommandAsync(script, parameters, 30, cancellationToken);
            
            if (psResult.IsSuccess && psResult.Objects.Count > 0)
            {
                if (psResult.Objects[0] is bool result)
                {
                    _logger.LogDebug("Health check for vCenter {VCenterName}: {Result}", vCenter.Name, result);
                    return result;
                }
            }

            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Health check failed for vCenter: {VCenterName}", vCenter.Name);
            return false;
        }
    }
} 