namespace MadeeAutomation.Domain.Entities;

/// <summary>
/// A person who can sign in to Admin.Web. Two kinds, in one table so sign-in, lockout and password
/// reset work the same for both:
///   - MadeeAutomation staff (ClientConfigId = null): can open everything.
///   - A client's own user (ClientConfigId set): the client portal only, and only that one business.
/// Passwords are stored as ASP.NET Core Identity password hashes (PBKDF2), never in plain text.
/// </summary>
public class AdminUser
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Email { get; set; } = string.Empty;       // stored lower-case, unique
    public string DisplayName { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;

    /// <summary>Changes whenever the password changes or the user is deactivated - signs out every other session.</summary>
    public string SecurityStamp { get; set; } = Guid.NewGuid().ToString("N");

    public bool IsActive { get; set; } = true;

    /// <summary>Null = MadeeAutomation staff. Set = a client's portal user, limited to this client.</summary>
    public Guid? ClientConfigId { get; set; }

    public bool IsClientUser => ClientConfigId.HasValue;

    /// <summary>Times on admin-only pages (e.g. last sign-in) are shown in this zone. Client pages use the client's zone.</summary>
    public string TimeZoneId { get; set; } = "Asia/Bangkok";

    /// <summary>Set for accounts created with a temporary password - they must pick their own at first sign-in.</summary>
    public bool MustChangePassword { get; set; }

    public int FailedLoginCount { get; set; }
    public DateTime? LockoutEndUtc { get; set; }
    public DateTime? LastLoginAtUtc { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAtUtc { get; set; }

    public bool IsLockedOut(DateTime nowUtc) => LockoutEndUtc.HasValue && LockoutEndUtc.Value > nowUtc;

    public void RotateSecurityStamp() => SecurityStamp = Guid.NewGuid().ToString("N");
}
