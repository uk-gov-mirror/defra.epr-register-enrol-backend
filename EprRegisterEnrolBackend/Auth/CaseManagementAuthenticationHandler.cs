using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using EprRegisterEnrolBackend.AccreditationApplication.Adapters;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace EprRegisterEnrolBackend.Auth;

// Verifies inbound pushes from ManagementBe (RA-311 OBE-2), the reverse direction of
// HttpCaseWorkingApiAdapter's own outbound signing. Recomputes the same v3 canonical-payload
// HMAC-SHA256 signature ManagementBe's ClientIdAuthenticationHandler produces, with
// clock-skew bounding and single-use nonce replay protection.
public class CaseManagementAuthenticationHandler(
    IOptionsMonitor<CaseManagementAuthenticationOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IOptions<CaseManagementAuthConfig> authConfig,
    IMemoryCache nonceCache,
    IHostEnvironment environment
) : AuthenticationHandler<CaseManagementAuthenticationOptions>(options, logger, encoder)
{
    public const string SchemeName = "CaseManagement";

    private static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(5);

    // Guards the nonce check-then-set below across concurrent requests on this instance.
    private static readonly object NonceLock = new();

    // Header the CM BE push hook sends on every request for cross-service tracing (RA-311).
    // Purely a diagnostic aid: absence must never fail the request.
    private const string CorrelationIdHeaderName = "X-Correlation-Id";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var config = authConfig.Value;
        var correlationId = GetCorrelationId();

        // Centralises failure logging so every Fail() return below is logged the same way,
        // without repeating the log call at each of the many early-return sites. Never logs
        // the shared secret, computed signature, or nonce — outcomes only (RA-311 security note).
        AuthenticateResult Fail(string reason)
        {
            Logger.LogWarning(
                "CaseManagement auth failed: {Reason} correlationId={CorrelationId}",
                reason,
                correlationId ?? "(absent)"
            );
            return AuthenticateResult.Fail(reason);
        }

        // Fail closed everywhere except Development — mirrors the outbound adapter's own
        // dev-mode bypass when CaseWorkingApiConfig.SharedSecret is empty.
        if (string.IsNullOrEmpty(config.SharedSecret))
        {
            if (!environment.IsDevelopment())
            {
                Logger.LogError(
                    "CaseManagement auth misconfigured: shared secret is not configured in environment '{Environment}'. correlationId={CorrelationId}",
                    environment.EnvironmentName,
                    correlationId ?? "(absent)"
                );
                return Task.FromResult(
                    AuthenticateResult.Fail("CaseManagement shared secret is not configured.")
                );
            }

            var devPrincipal = new ClaimsPrincipal(new ClaimsIdentity(SchemeName));
            Logger.LogInformation(
                "CaseManagement auth succeeded (development header-trust bypass) correlationId={CorrelationId}",
                correlationId ?? "(absent)"
            );
            return Task.FromResult(
                AuthenticateResult.Success(new AuthenticationTicket(devPrincipal, SchemeName))
            );
        }

        Logger.LogInformation(
            "CaseManagement auth request received correlationId={CorrelationId}",
            correlationId ?? "(absent)"
        );

        if (!Request.Headers.TryGetValue("x-cdp-client-id", out var clientIdValues))
            return Task.FromResult(Fail("Missing x-cdp-client-id header."));

        var clientId = clientIdValues.ToString();
        if (!string.Equals(clientId, config.ExpectedClientId, StringComparison.Ordinal))
            return Task.FromResult(Fail("Unrecognised x-cdp-client-id."));

        if (!Request.Headers.TryGetValue("x-cdp-auth-signature", out var signatureValues))
            return Task.FromResult(Fail("Missing x-cdp-auth-signature header."));
        if (!Request.Headers.TryGetValue("x-cdp-auth-timestamp", out var timestampValues))
            return Task.FromResult(Fail("Missing x-cdp-auth-timestamp header."));
        if (!Request.Headers.TryGetValue("x-cdp-auth-nonce", out var nonceValues))
            return Task.FromResult(Fail("Missing x-cdp-auth-nonce header."));

        var signature = signatureValues.ToString();
        var timestamp = timestampValues.ToString();
        var nonce = nonceValues.ToString();
        var userId = Request.Headers.TryGetValue("x-cdp-user-id", out var userIdValues)
            ? userIdValues.ToString()
            : null;
        var userName = Request.Headers.TryGetValue("x-cdp-user-name", out var userNameValues)
            ? userNameValues.ToString()
            : null;

        if (
            !DateTime.TryParse(
                timestamp,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                out var requestTime
            )
        )
            return Task.FromResult(Fail("Invalid x-cdp-auth-timestamp header."));

        if ((DateTime.UtcNow - requestTime).Duration() > ClockSkew)
            return Task.FromResult(
                Fail("Request timestamp is outside the allowed clock-skew window.")
            );

        var expectedSignature = ComputeSignature(
            config.SharedSecret,
            clientId,
            userId,
            userName,
            timestamp,
            nonce
        );
        var signatureValid = CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(signature),
            Encoding.UTF8.GetBytes(expectedSignature)
        );
        if (!signatureValid)
            return Task.FromResult(Fail("Invalid signature."));

        // TryGetValue + Set is check-then-act; without the lock, two requests racing on the
        // same nonce could both observe "not present" and both proceed, defeating single-use
        // replay protection. IMemoryCache (incl. the GetOrCreate extension) has no atomic
        // add-if-absent primitive, so the critical section is serialised explicitly.
        var nonceCacheKey = $"case-management-auth-nonce:{nonce}";
        lock (NonceLock)
        {
            if (nonceCache.TryGetValue(nonceCacheKey, out _))
                return Task.FromResult(Fail("Nonce has already been used."));
            nonceCache.Set(nonceCacheKey, true, ClockSkew);
        }

        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, clientId) };
        if (!string.IsNullOrEmpty(userId))
            claims.Add(new Claim("cdp_user_id", userId));
        if (!string.IsNullOrEmpty(userName))
            claims.Add(new Claim("cdp_user_name", userName));

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName));
        Logger.LogInformation(
            "CaseManagement auth succeeded for clientId={ClientId} correlationId={CorrelationId}",
            clientId,
            correlationId ?? "(absent)"
        );
        return Task.FromResult(
            AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName))
        );
    }

    private string? GetCorrelationId() =>
        Request.Headers.TryGetValue(CorrelationIdHeaderName, out var values)
            ? values.ToString()
            : null;

    // Verification-side counterpart of HttpCaseWorkingApiAdapter.ComputeSignature (v3 canonical
    // payload) — the reverse direction of the same scheme ManagementBe's
    // ClientIdAuthenticationHandler uses. Must stay in sync — any change is breaking.
    internal static string ComputeSignature(
        string sharedSecret,
        string clientId,
        string? userId,
        string? userName,
        string timestamp,
        string nonce
    )
    {
        var payload = string.Join(
            '\n',
            "v3",
            clientId,
            userId ?? string.Empty,
            userName ?? string.Empty,
            timestamp,
            nonce
        );
        var keyBytes = Encoding.UTF8.GetBytes(sharedSecret);
        var payloadBytes = Encoding.UTF8.GetBytes(payload);
        var mac = HMACSHA256.HashData(keyBytes, payloadBytes);
        return Convert.ToBase64String(mac);
    }
}
