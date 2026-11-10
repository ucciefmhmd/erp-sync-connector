using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace ErpSync.Api.Security;

public static class Scopes
{
    public const string ClaimType = "scope";
    public const string CatalogRead = "catalog:read";
    public const string OrdersWrite = "orders:write";
    public const string SyncAdmin = "sync:admin";

    public static readonly string[] All = [CatalogRead, OrdersWrite, SyncAdmin];
}

/// <summary>A client system allowed to call the API. Only a SHA-256 hash of its key is stored.</summary>
public sealed class ApiClient
{
    public string Name { get; set; } = string.Empty;

    /// <summary>Hex-encoded SHA-256 of the API key.</summary>
    public string KeyHash { get; set; } = string.Empty;

    public IList<string> Scopes { get; } = [];
}

public sealed class ApiKeyOptions : AuthenticationSchemeOptions
{
    public IList<ApiClient> Clients { get; } = [];
}

/// <summary>
/// Authenticates system-to-system callers by the X-Api-Key header. Keys are compared as SHA-256
/// hashes in constant time, so neither the stored config nor response timing reveals a key.
/// </summary>
internal sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<ApiKeyOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IProblemDetailsService problemDetails)
    : AuthenticationHandler<ApiKeyOptions>(options, logger, encoder)
{
    public const string SchemeName = "ApiKey";
    public const string HeaderName = "X-Api-Key";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(HeaderName, out var values) || string.IsNullOrWhiteSpace(values.ToString()))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var presentedHash = SHA256.HashData(Encoding.UTF8.GetBytes(values.ToString()));

        ApiClient? match = null;
        foreach (var client in Options.Clients)
        {
            // Check every client so the time taken doesn't depend on which key matched.
            if (TryDecodeHex(client.KeyHash, out var storedHash) &&
                CryptographicOperations.FixedTimeEquals(presentedHash, storedHash))
            {
                match ??= client;
            }
        }

        if (match is null)
        {
            return Task.FromResult(AuthenticateResult.Fail("Invalid API key."));
        }

        var claims = new List<Claim> { new(ClaimTypes.Name, match.Name) };
        claims.AddRange(match.Scopes.Select(scope => new Claim(Scopes.ClaimType, scope)));

        var identity = new ClaimsIdentity(claims, SchemeName);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }

    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = $"{SchemeName} header=\"{HeaderName}\"";
        await WriteProblemAsync("Unauthorized", $"Send a valid API key in the {HeaderName} header.").ConfigureAwait(false);
    }

    protected override async Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status403Forbidden;
        await WriteProblemAsync("Forbidden", "This API key does not have the scope required for this operation.").ConfigureAwait(false);
    }

    private ValueTask WriteProblemAsync(string title, string detail) =>
        problemDetails.WriteAsync(new ProblemDetailsContext
        {
            HttpContext = Context,
            ProblemDetails = { Status = Response.StatusCode, Title = title, Detail = detail },
        });

    private static bool TryDecodeHex(string hex, out byte[] bytes)
    {
        try
        {
            bytes = Convert.FromHexString(hex);
            return bytes.Length == SHA256.HashSizeInBytes;
        }
        catch (FormatException)
        {
            bytes = [];
            return false;
        }
    }
}
