using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace MadeeAutomation.WebApi.Security;

/// <summary>
/// Protects endpoints that only MadeeAutomation itself may call (Admin.Web's "Test" button,
/// the scheduled-trigger runner). The caller must send header X-Internal-Key with the value of
/// InternalApi:Key - set the SAME value in WebApi and Admin.Web user-secrets.
/// No key configured = the endpoints are switched off (503), never open.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class RequireInternalKeyAttribute : Attribute, IAuthorizationFilter
{
    public const string HeaderName = "X-Internal-Key";

    public void OnAuthorization(AuthorizationFilterContext context)
    {
        var configuration = context.HttpContext.RequestServices.GetRequiredService<IConfiguration>();
        var expected = configuration["InternalApi:Key"];

        if (string.IsNullOrWhiteSpace(expected))
        {
            context.Result = new ObjectResult(new { error = "InternalApi:Key is not configured on WebApi - internal endpoints are off." })
            {
                StatusCode = StatusCodes.Status503ServiceUnavailable
            };
            return;
        }

        var supplied = context.HttpContext.Request.Headers[HeaderName].ToString();
        var a = Encoding.UTF8.GetBytes(expected.Trim());
        var b = Encoding.UTF8.GetBytes(supplied.Trim());

        if (b.Length == 0 || !CryptographicOperations.FixedTimeEquals(a, b))
        {
            context.Result = new UnauthorizedObjectResult(new { error = $"Missing or wrong {HeaderName} header." });
        }
    }
}
