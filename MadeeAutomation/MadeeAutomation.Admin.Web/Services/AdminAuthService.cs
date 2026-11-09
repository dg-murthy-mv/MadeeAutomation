using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using MadeeAutomation.Domain.Entities;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;

namespace MadeeAutomation.Admin.Web.Services;

/// <summary>
/// Password hashing (ASP.NET Core Identity's PBKDF2 hasher - no Identity tables needed),
/// password rules, sign-in cookie, and reset-token helpers for Admin.Web.
/// </summary>
public class AdminAuthService
{
    public const string StampClaim = "madee_stamp";
    public const string MustChangeClaim = "madee_must_change";
    public const int MaxFailedLogins = 5;
    public static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan ResetLinkLifetime = TimeSpan.FromHours(1);
    public const int MinPasswordLength = 10;

    private readonly PasswordHasher<AdminUser> _hasher = new();

    public string HashPassword(AdminUser user, string password) => _hasher.HashPassword(user, password);

    public bool VerifyPassword(AdminUser user, string password, out bool needsRehash)
    {
        needsRehash = false;
        if (string.IsNullOrEmpty(user.PasswordHash)) return false;
        var result = _hasher.VerifyHashedPassword(user, user.PasswordHash, password);
        needsRehash = result == PasswordVerificationResult.SuccessRehashNeeded;
        return result != PasswordVerificationResult.Failed;
    }

    /// <summary>Returns a problem in plain words, or null when the password is acceptable.</summary>
    public static string? CheckPasswordRules(string? password, string? email = null)
    {
        if (string.IsNullOrEmpty(password) || password.Length < MinPasswordLength)
            return $"Use at least {MinPasswordLength} characters.";
        if (!password.Any(char.IsLetter) || !password.Any(char.IsDigit))
            return "Use at least one letter and one number.";
        if (!string.IsNullOrEmpty(email) && password.Contains(email.Split('@')[0], StringComparison.OrdinalIgnoreCase) && email.Split('@')[0].Length >= 4)
            return "Don't use your email name in the password.";
        return null;
    }

    public static string GenerateTemporaryPassword()
    {
        const string letters = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnpqrstuvwxyz";
        const string digits = "23456789";
        var chars = new char[12];
        for (var i = 0; i < chars.Length; i++)
        {
            var set = i % 4 == 3 ? digits : letters;
            chars[i] = set[RandomNumberGenerator.GetInt32(set.Length)];
        }
        return new string(chars);
    }

    public async Task SignInAsync(HttpContext http, AdminUser user, bool rememberMe)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.DisplayName),
            new(ClaimTypes.Email, user.Email),
            new(StampClaim, user.SecurityStamp)
        };
        if (user.MustChangePassword) claims.Add(new Claim(MustChangeClaim, "1"));

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
        await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal, new AuthenticationProperties
        {
            IsPersistent = rememberMe,
            ExpiresUtc = rememberMe ? DateTimeOffset.UtcNow.AddDays(14) : null
        });
    }

    public static Guid? CurrentUserId(ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;

    /// <summary>A random token for the email link, and the SHA-256 hash that is stored.</summary>
    public static (string token, string hash) NewResetToken()
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
        return (token, HashToken(token));
    }

    public static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token ?? "")));
}
