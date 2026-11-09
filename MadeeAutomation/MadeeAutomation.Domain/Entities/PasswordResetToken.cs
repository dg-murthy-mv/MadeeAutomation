namespace MadeeAutomation.Domain.Entities;

/// <summary>
/// One "forgot password" link. Only a SHA-256 hash of the token is stored, so a database
/// leak can't be used to reset passwords. Single use, short expiry.
/// </summary>
public class PasswordResetToken
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AdminUserId { get; set; }
    public string TokenHash { get; set; } = string.Empty;
    public DateTime ExpiresAtUtc { get; set; }
    public DateTime? UsedAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
