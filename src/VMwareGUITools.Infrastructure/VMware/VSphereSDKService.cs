using Microsoft.Extensions.Logging;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using VMware.Vim;
using VMwareGUITools.Core.Models;
using VMwareGUITools.Infrastructure.Security;

namespace VMwareGUITools.Infrastructure.VMware;

/// <summary>
/// Implementation of vSphere SDK service using VMware.Vim for managed object access
/// </summary>
public class VSphereSDKService : IVSphereSDKService
{
    private readonly ILogger<VSphereSDKService> _logger;
    private readonly ICredentialService _credentialService;

    public VSphereSDKService(
        ILogger<VSphereSDKService> logger,
        ICredentialService credentialService)
    {
        _logger = logger;
        _credentialService = credentialService;
    }

    public async Task<VimClientConnection> ConnectAsync(VCenter vCenter, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("Connecting to vCenter {VCenterName} using vSphere SDK", vCenter.Name);

            // Decrypt credentials
            var credentials = await _credentialService.DecryptCredentialsAsync(vCenter.EncryptedCredentials);
            if (credentials == null)
            {
                throw new InvalidOperationException("Failed to decrypt vCenter credentials");
            }

            // Configure certificate validation to ignore self-signed certificates
            ServicePointManager.ServerCertificateValidationCallback = (sender, certificate, chain, sslPolicyErrors) => true;

            // Create VIM client
            var vimClient = new VimClientImpl();
            
            // Connect to vCenter
            var serviceContent = await vimClient.Connect(vCenter.Url);
            
            // Login
            var sessionManager = new SessionManager(vimClient, serviceContent.sessionManager);
            var userSession = await sessionManager.Login(credentials.Username, credentials.Password);

            _logger.LogInformation("Successfully connected to vCenter {VCenterName} with session {SessionId}", 
                vCenter.Name, userSession.key);

            return new VimClientConnection
            {
                VimClient = vimClient,
                ServiceContent = serviceContent,
                SessionId = userSession.key,
                ConnectedAt = DateTime.UtcNow,
                VCenter = vCenter
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect to vCenter {VCenterName}", vCenter.Name);
            throw;
        }
    }

    public async Task DisconnectAsync(VimClientConnection connection, CancellationToken cancellationToken = default)
    {
        try
        {
            if (connection?.VimClient != null)
            {
                // Logout session
                var sessionManager = new SessionManager(connection.VimClient, connection.ServiceContent.sessionManager);
                await sessionManager.Logout();

                // Disconnect client
                await connection.VimClient.Disconnect();

                _logger.LogInformation("Disconnected from vCenter {VCenterName}", connection.VCenter.Name);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error disconnecting from vCenter");
        }
    }

    public async Task<ISCSIPathResult> GetISCSIPathStatusAsync(VimClientConnection connection, string hostMoId, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("Getting iSCSI path status for host {HostMoId}", hostMoId);

            var result = new ISCSIPathResult
            {
                HostMoId = hostMoId,
                CheckedAt = DateTime.UtcNow
            };

            // Get host managed object reference
            var hostRef = new ManagedObjectReference
            {
                type = "HostSystem",
                Value = hostMoId
            };

            // Get host system
            var hostSystem = new HostSystem(connection.VimClient, hostRef);
            
            // Get host properties including name and config manager
            var hostProperties = await hostSystem.GetProperties("name", "configManager");
            result.HostName = hostProperties["name"]?.ToString() ?? "Unknown";

            // Get configManager
            var configManager = hostProperties["configManager"] as HostConfigManager;
            if (configManager?.storageSystem == null)
            {
                result.IsSuccess = false;
                result.ErrorMessage = "Host storage system not available";
                return result;
            }

            // Get storage system
            var storageSystem = new HostStorageSystem(connection.VimClient, configManager.storageSystem);

            // Get storage device info - this contains the HBAs and their paths
            var storageDeviceInfo = await storageSystem.GetStorageDeviceInfo();

            // Process iSCSI HBAs
            var iscsiAdapters = new List<ISCSIAdapterInfo>();
            var allPaths = new List<PathInfo>();

            if (storageDeviceInfo?.hostBusAdapter != null)
            {
                foreach (var hba in storageDeviceInfo.hostBusAdapter)
                {
                    // Check if this is an iSCSI adapter
                    if (hba is HostInternetScsiHba iscsiHba)
                    {
                        var adapterInfo = new ISCSIAdapterInfo
                        {
                            AdapterName = iscsiHba.device,
                            HbaType = "iSCSI",
                            IsOnline = iscsiHba.status == "online"
                        };

                        // Get portal addresses if available
                        if (iscsiHba is HostInternetScsiHba internetScsiHba && internetScsiHba.configuredSendTarget != null)
                        {
                            foreach (var target in internetScsiHba.configuredSendTarget)
                            {
                                adapterInfo.PortalAddresses.Add(target.address);
                            }
                        }

                        // Get paths for this adapter
                        var adapterPaths = await GetPathsForAdapter(storageDeviceInfo, iscsiHba.device);
                        adapterInfo.Paths.AddRange(adapterPaths);
                        allPaths.AddRange(adapterPaths);

                        iscsiAdapters.Add(adapterInfo);
                    }
                }
            }

            // Calculate path statistics
            result.ISCSIAdapters = iscsiAdapters;
            result.TotalPaths = allPaths.Count;
            result.ActivePaths = allPaths.Count(p => p.PathState == "active");
            result.DeadPaths = allPaths.Count(p => p.PathState == "dead");
            result.StandbyPaths = allPaths.Count(p => p.PathState == "standby");
            result.PathDetails = allPaths;
            result.IsSuccess = true;

            _logger.LogInformation("Found {AdapterCount} iSCSI adapters with {TotalPaths} total paths ({ActivePaths} active, {DeadPaths} dead) for host {HostName}",
                iscsiAdapters.Count, result.TotalPaths, result.ActivePaths, result.DeadPaths, result.HostName);

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get iSCSI path status for host {HostMoId}", hostMoId);
            return new ISCSIPathResult
            {
                HostMoId = hostMoId,
                IsSuccess = false,
                ErrorMessage = ex.Message,
                CheckedAt = DateTime.UtcNow
            };
        }
    }

    public async Task<List<StorageAdapterInfo>> GetStorageAdaptersAsync(VimClientConnection connection, string hostMoId, CancellationToken cancellationToken = default)
    {
        try
        {
            var hostRef = new ManagedObjectReference { type = "HostSystem", Value = hostMoId };
            var hostSystem = new HostSystem(connection.VimClient, hostRef);
            var hostProperties = await hostSystem.GetProperties("configManager");
            var configManager = hostProperties["configManager"] as HostConfigManager;

            if (configManager?.storageSystem == null)
            {
                return new List<StorageAdapterInfo>();
            }

            var storageSystem = new HostStorageSystem(connection.VimClient, configManager.storageSystem);
            var storageDeviceInfo = await storageSystem.GetStorageDeviceInfo();

            var adapters = new List<StorageAdapterInfo>();

            if (storageDeviceInfo?.hostBusAdapter != null)
            {
                foreach (var hba in storageDeviceInfo.hostBusAdapter)
                {
                    var adapter = new StorageAdapterInfo
                    {
                        Key = hba.key,
                        Device = hba.device,
                        Bus = hba.bus.ToString(),
                        Status = hba.status,
                        Model = hba.model,
                        Driver = hba.driver
                    };

                    if (hba.pci != null)
                    {
                        adapter.PciId = new[] { hba.pci };
                    }

                    adapters.Add(adapter);
                }
            }

            return adapters;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get storage adapters for host {HostMoId}", hostMoId);
            return new List<StorageAdapterInfo>();
        }
    }

    public async Task<List<MultipathInfo>> GetMultipathInfoAsync(VimClientConnection connection, string hostMoId, CancellationToken cancellationToken = default)
    {
        try
        {
            var hostRef = new ManagedObjectReference { type = "HostSystem", Value = hostMoId };
            var hostSystem = new HostSystem(connection.VimClient, hostRef);
            var hostProperties = await hostSystem.GetProperties("configManager");
            var configManager = hostProperties["configManager"] as HostConfigManager;

            if (configManager?.storageSystem == null)
            {
                return new List<MultipathInfo>();
            }

            var storageSystem = new HostStorageSystem(connection.VimClient, configManager.storageSystem);
            var storageDeviceInfo = await storageSystem.GetStorageDeviceInfo();

            var multipathInfos = new List<MultipathInfo>();

            if (storageDeviceInfo?.multipathInfo != null)
            {
                foreach (var mpInfo in storageDeviceInfo.multipathInfo)
                {
                    var multipathInfo = new MultipathInfo
                    {
                        LunKey = mpInfo.lun,
                        Id = mpInfo.id,
                        Name = mpInfo.name,
                        PolicyName = mpInfo.policy?.policy
                    };

                    // Get paths for this multipath device
                    if (mpInfo.path != null)
                    {
                        foreach (var path in mpInfo.path)
                        {
                            var pathInfo = new PathInfo
                            {
                                PathName = path.name,
                                Adapter = path.adapter,
                                PathState = path.state,
                                PathStatus = path.pathState,
                                IsActive = path.state == "active",
                                Transport = path.transport?.ToString() ?? "unknown"
                            };

                            // Parse target and LUN information from path name if available
                            if (!string.IsNullOrEmpty(path.name))
                            {
                                // Path names typically follow format like "vmhba64:C0:T0:L0"
                                var parts = path.name.Split(':');
                                if (parts.Length >= 4)
                                {
                                    pathInfo.Target = parts[2]; // T0
                                    if (int.TryParse(parts[3].Substring(1), out var lun)) // L0 -> 0
                                    {
                                        pathInfo.Lun = lun;
                                    }
                                }
                            }

                            multipathInfo.Paths.Add(pathInfo);
                        }
                    }

                    multipathInfos.Add(multipathInfo);
                }
            }

            return multipathInfos;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get multipath info for host {HostMoId}", hostMoId);
            return new List<MultipathInfo>();
        }
    }

    private static List<PathInfo> GetPathsForAdapter(HostStorageDeviceInfo storageDeviceInfo, string adapterName)
    {
        var paths = new List<PathInfo>();

        // Get all paths for this specific adapter
        if (storageDeviceInfo.multipathInfo != null)
        {
            foreach (var mpInfo in storageDeviceInfo.multipathInfo)
            {
                if (mpInfo.path != null)
                {
                    foreach (var path in mpInfo.path)
                    {
                        if (path.adapter == adapterName)
                        {
                            var pathInfo = new PathInfo
                            {
                                PathName = path.name,
                                Adapter = path.adapter,
                                LunUuid = mpInfo.lun,
                                PathState = path.state,
                                PathStatus = path.pathState,
                                IsActive = path.state == "active",
                                Transport = path.transport?.ToString() ?? "unknown"
                            };

                            // Parse additional path information
                            if (!string.IsNullOrEmpty(path.name))
                            {
                                var parts = path.name.Split(':');
                                if (parts.Length >= 4)
                                {
                                    pathInfo.Target = parts[2]; // T0
                                    if (int.TryParse(parts[3].Substring(1), out var lun))
                                    {
                                        pathInfo.Lun = lun;
                                    }
                                }
                            }

                            paths.Add(pathInfo);
                        }
                    }
                }
            }
        }

        return paths;
    }
} 