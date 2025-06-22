namespace VMwareGUITools.Infrastructure.Security;

/// <summary>
/// Interface for secure credential management
/// </summary>
public interface ICredentialService
{
    /// <summary>
    /// Encrypts the provided credentials using Windows DPAPI
    /// </summary>
    /// <param name="username">The username to encrypt</param>
    /// <param name="password">The password to encrypt</param>
    /// <returns>Encrypted credential string</returns>
    string EncryptCredentials(string username, string password);

    /// <summary>
    /// Decrypts the provided encrypted credentials
    /// </summary>
    /// <param name="encryptedCredentials">The encrypted credential string</param>
    /// <returns>Tuple containing username and password</returns>
    (string Username, string Password) DecryptCredentials(string encryptedCredentials);

    /// <summary>
    /// Validates if the encrypted credentials can be successfully decrypted
    /// </summary>
    /// <param name="encryptedCredentials">The encrypted credential string</param>
    /// <returns>True if credentials are valid and can be decrypted</returns>
    bool ValidateCredentials(string encryptedCredentials);

    /// <summary>
    /// Changes the encryption scope for credentials (machine-level vs user-level)
    /// </summary>
    /// <param name="useMachineScope">If true, uses machine-level encryption; otherwise user-level</param>
    void SetEncryptionScope(bool useMachineScope);

    /// <summary>
    /// Asynchronously decrypts the provided encrypted credentials
    /// </summary>
    /// <param name="encryptedCredentials">The encrypted credential string</param>
    /// <returns>VCenterCredentials object containing decrypted credentials</returns>
    Task<VCenterCredentials> DecryptCredentialsAsync(string encryptedCredentials);
}

/// <summary>
/// Represents a set of credentials for vCenter authentication
/// </summary>
public class VCenterCredentials
{
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string Domain { get; set; } = string.Empty;

    /// <summary>
    /// Gets the full username including domain if specified
    /// </summary>
    public string FullUsername => string.IsNullOrEmpty(Domain) ? Username : $"{Domain}\\{Username}";

    /// <summary>
    /// Validates if the credentials have minimum required information
    /// </summary>
    public bool IsValid => !string.IsNullOrWhiteSpace(Username) && !string.IsNullOrWhiteSpace(Password);
} 