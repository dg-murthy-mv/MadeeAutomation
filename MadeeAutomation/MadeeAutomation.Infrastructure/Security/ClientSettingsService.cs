using MadeeAutomation.Application.Abstractions;
using MadeeAutomation.Domain.Security;
using MadeeAutomation.Domain.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MadeeAutomation.Infrastructure.Persistence;

namespace MadeeAutomation.Infrastructure.Security;

/// <summary>
/// Decrypts / encrypts ClientConfig.IntegrationSettingsJson using Security:SettingsEncryptionKey.
/// Reading old plain-JSON rows works without a key; saving always requires the key.
/// </summary>
public class ClientSettingsService : IClientSettingsService
{
    private readonly AutomationDbContext _db;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ClientSettingsService> _logger;

    public ClientSettingsService(AutomationDbContext db, IConfiguration configuration, ILogger<ClientSettingsService> logger)
    {
        _db = db;
        _configuration = configuration;
        _logger = logger;
    }

    private byte[] Key => SettingsCipher.ParseKey(_configuration["Security:SettingsEncryptionKey"]);

    public async Task<ClientIntegrationSettings> GetAsync(Guid clientConfigId, CancellationToken cancellationToken = default)
    {
        var stored = await _db.ClientConfigs
            .Where(c => c.Id == clientConfigId)
            .Select(c => c.IntegrationSettingsJson)
            .FirstOrDefaultAsync(cancellationToken);

        return Read(stored);
    }

    public ClientIntegrationSettings Read(string? storedValue)
    {
        if (string.IsNullOrWhiteSpace(storedValue)) return new ClientIntegrationSettings();
        if (!SettingsCipher.IsEncrypted(storedValue)) return ClientIntegrationSettings.FromJson(storedValue);

        try
        {
            return ClientIntegrationSettings.FromJson(SettingsCipher.Decrypt(storedValue, Key));
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or FormatException)
        {
            _logger.LogError("Client settings could not be decrypted - is Security:SettingsEncryptionKey the same in WebApi and Admin.Web?");
            throw new InvalidOperationException(
                "Client settings could not be decrypted. Security:SettingsEncryptionKey must be the same key that saved them.", ex);
        }
    }

    public string Protect(ClientIntegrationSettings settings) => SettingsCipher.Encrypt(settings.ToJson(), Key);
}
