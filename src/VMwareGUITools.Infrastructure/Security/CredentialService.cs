using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace VMwareGUITools.Infrastructure.Security;

/// <summary>
/// Implementation of credential service using Windows DPAPI for encryption
/// </summary>
public class CredentialService : ICredentialService
{
    private readonly ILogger<CredentialService> _logger;
    private bool _useMachineScope = false;

    public CredentialService(ILogger<CredentialService> logger)
    {
        _logger = logger;
    }

    public string EncryptCredentials(string username, string password)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            throw new ArgumentException("Username and password cannot be null or empty");
        }

        try
        {
            var credentials = new VCenterCredentials
            {
                Username = username,
                Password = password
            };

            var json = JsonSerializer.Serialize(credentials);
            var data = Encoding.UTF8.GetBytes(json);

            var scope = _useMachineScope ? DataProtectionScope.LocalMachine : DataProtectionScope.CurrentUser;
            var encryptedData = ProtectedData.Protect(data, null, scope);

            var result = Convert.ToBase64String(encryptedData);
            _logger.LogDebug("Successfully encrypted credentials for user: {Username}", username);
            
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to encrypt credentials for user: {Username}", username);
            throw new InvalidOperationException("Failed to encrypt credentials", ex);
        }
    }

    public (string Username, string Password) DecryptCredentials(string encryptedCredentials)
    {
        if (string.IsNullOrWhiteSpace(encryptedCredentials))
        {
            throw new ArgumentException("Encrypted credentials cannot be null or empty");
        }

        try
        {
            var encryptedData = Convert.FromBase64String(encryptedCredentials);
            var scope = _useMachineScope ? DataProtectionScope.LocalMachine : DataProtectionScope.CurrentUser;
            var decryptedData = ProtectedData.Unprotect(encryptedData, null, scope);

            var json = Encoding.UTF8.GetString(decryptedData);
            var credentials = JsonSerializer.Deserialize<VCenterCredentials>(json);

            if (credentials == null || !credentials.IsValid)
            {
                throw new InvalidOperationException("Decrypted credentials are invalid");
            }

            _logger.LogDebug("Successfully decrypted credentials for user: {Username}", credentials.Username);
            return (credentials.Username, credentials.Password);
        }
        catch (CryptographicException ex)
        {
            _logger.LogError(ex, "Failed to decrypt credentials - cryptographic error");
            throw new UnauthorizedAccessException("Failed to decrypt credentials - invalid encryption scope or corrupted data", ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to decrypt credentials");
            throw new InvalidOperationException("Failed to decrypt credentials", ex);
        }
    }

    public bool ValidateCredentials(string encryptedCredentials)
    {
        if (string.IsNullOrWhiteSpace(encryptedCredentials))
        {
            return false;
        }

        try
        {
            var (username, password) = DecryptCredentials(encryptedCredentials);
            return !string.IsNullOrWhiteSpace(username) && !string.IsNullOrWhiteSpace(password);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Credential validation failed");
            return false;
        }
    }

    public void SetEncryptionScope(bool useMachineScope)
    {
        _useMachineScope = useMachineScope;
        _logger.LogInformation("Encryption scope changed to: {Scope}", 
            useMachineScope ? "LocalMachine" : "CurrentUser");
    }

    public Task<VCenterCredentials> DecryptCredentialsAsync(string encryptedCredentials)
    {
        return Task.Run(() =>
        {
            var (username, password) = DecryptCredentials(encryptedCredentials);
            return new VCenterCredentials 
            { 
                Username = username, 
                Password = password 
            };
        });
    }

    /// <summary>
    /// Creates a VCenterCredentials object from username and password with optional domain parsing
    /// </summary>
    public static VCenterCredentials CreateCredentials(string usernameInput, string password)
    {
        var credentials = new VCenterCredentials { Password = password };

        // Parse domain from username if present (domain\username or username@domain)
        if (usernameInput.Contains('\\'))
        {
            var parts = usernameInput.Split('\\');
            if (parts.Length == 2)
            {
                credentials.Domain = parts[0];
                credentials.Username = parts[1];
            }
            else
            {
                credentials.Username = usernameInput;
            }
        }
        else if (usernameInput.Contains('@'))
        {
            var parts = usernameInput.Split('@');
            if (parts.Length == 2)
            {
                credentials.Username = parts[0];
                credentials.Domain = parts[1];
            }
            else
            {
                credentials.Username = usernameInput;
            }
        }
        else
        {
            credentials.Username = usernameInput;
        }

        return credentials;
    }
} 