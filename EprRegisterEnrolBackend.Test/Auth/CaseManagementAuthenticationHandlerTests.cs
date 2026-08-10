using System.Text.Encodings.Web;
using EprRegisterEnrolBackend.AccreditationApplication.Adapters;
using EprRegisterEnrolBackend.Auth;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace EprRegisterEnrolBackend.Test.Auth;

public class CaseManagementAuthenticationHandlerTests
{
    private const string TestClientId = "epr-register-enrol-management-be";
    private const string TestSecret = "test-shared-secret";

    private static async Task<AuthenticateResult> AuthenticateAsync(
        HttpContext context,
        string? sharedSecret = TestSecret,
        bool isDevelopment = false,
        IMemoryCache? cache = null,
        ILoggerFactory? loggerFactory = null
    )
    {
        var options = new StaticOptionsMonitor<CaseManagementAuthenticationOptions>(new());
        var authConfig = Options.Create(
            new CaseManagementAuthConfig
            {
                SharedSecret = sharedSecret,
                ExpectedClientId = TestClientId,
            }
        );
        var environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns(
            isDevelopment ? Environments.Development : Environments.Production
        );

        var handler = new CaseManagementAuthenticationHandler(
            options,
            loggerFactory ?? NullLoggerFactory.Instance,
            UrlEncoder.Default,
            authConfig,
            cache ?? new MemoryCache(new MemoryCacheOptions()),
            environment
        );

        var scheme = new AuthenticationScheme(
            CaseManagementAuthenticationHandler.SchemeName,
            CaseManagementAuthenticationHandler.SchemeName,
            typeof(CaseManagementAuthenticationHandler)
        );
        await handler.InitializeAsync(scheme, context);
        return await handler.AuthenticateAsync();
    }

    private static HttpContext CreateValidRequestContext(
        string? clientId = TestClientId,
        string? secret = TestSecret,
        string? timestampOverride = null,
        string? nonceOverride = null,
        string? signatureOverride = null
    )
    {
        var context = new DefaultHttpContext();
        var timestamp = timestampOverride ?? DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
        var nonce = nonceOverride ?? Convert.ToBase64String(Guid.NewGuid().ToByteArray());
        var signature =
            signatureOverride
            ?? (
                secret is null
                    ? "invalid"
                    : CaseManagementAuthenticationHandler.ComputeSignature(
                        secret,
                        clientId ?? TestClientId,
                        "jane@example.com",
                        "Jane Smith",
                        timestamp,
                        nonce
                    )
            );

        if (clientId is not null)
            context.Request.Headers["x-cdp-client-id"] = clientId;
        context.Request.Headers["x-cdp-user-id"] = "jane@example.com";
        context.Request.Headers["x-cdp-user-name"] = "Jane Smith";
        context.Request.Headers["x-cdp-auth-signature"] = signature;
        context.Request.Headers["x-cdp-auth-timestamp"] = timestamp;
        context.Request.Headers["x-cdp-auth-nonce"] = nonce;

        return context;
    }

    [Fact]
    public async Task ValidSignature_Succeeds()
    {
        var context = CreateValidRequestContext();
        var result = await AuthenticateAsync(context);
        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task MissingClientIdHeader_Fails()
    {
        var context = CreateValidRequestContext(clientId: null);
        var result = await AuthenticateAsync(context);
        result.Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task MissingSignatureHeader_Fails()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["x-cdp-client-id"] = TestClientId;
        context.Request.Headers["x-cdp-auth-timestamp"] = DateTime.UtcNow.ToString(
            "yyyy-MM-ddTHH:mm:ssZ"
        );
        context.Request.Headers["x-cdp-auth-nonce"] = Convert.ToBase64String(
            Guid.NewGuid().ToByteArray()
        );
        // x-cdp-auth-signature intentionally omitted.

        var result = await AuthenticateAsync(context);

        result.Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task MissingTimestampHeader_Fails()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["x-cdp-client-id"] = TestClientId;
        context.Request.Headers["x-cdp-auth-signature"] = "irrelevant-timestamp-missing";
        context.Request.Headers["x-cdp-auth-nonce"] = Convert.ToBase64String(
            Guid.NewGuid().ToByteArray()
        );
        // x-cdp-auth-timestamp intentionally omitted.

        var result = await AuthenticateAsync(context);

        result.Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task MissingNonceHeader_Fails()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["x-cdp-client-id"] = TestClientId;
        context.Request.Headers["x-cdp-auth-signature"] = "irrelevant-nonce-missing";
        context.Request.Headers["x-cdp-auth-timestamp"] = DateTime.UtcNow.ToString(
            "yyyy-MM-ddTHH:mm:ssZ"
        );
        // x-cdp-auth-nonce intentionally omitted.

        var result = await AuthenticateAsync(context);

        result.Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task UnrecognisedClientId_Fails()
    {
        var context = new DefaultHttpContext();
        var timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
        var nonce = Convert.ToBase64String(Guid.NewGuid().ToByteArray());
        var signature = CaseManagementAuthenticationHandler.ComputeSignature(
            TestSecret,
            "some-other-client",
            null,
            null,
            timestamp,
            nonce
        );
        context.Request.Headers["x-cdp-client-id"] = "some-other-client";
        context.Request.Headers["x-cdp-auth-signature"] = signature;
        context.Request.Headers["x-cdp-auth-timestamp"] = timestamp;
        context.Request.Headers["x-cdp-auth-nonce"] = nonce;

        var result = await AuthenticateAsync(context);

        result.Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task TamperedSignature_Fails()
    {
        var context = CreateValidRequestContext(signatureOverride: "not-the-real-signature");
        var result = await AuthenticateAsync(context);
        result.Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task ExpiredTimestamp_Fails()
    {
        var staleTimestamp = DateTime
            .UtcNow.AddMinutes(-10)
            .ToString("yyyy-MM-ddTHH:mm:ssZ");
        var context = CreateValidRequestContext(timestampOverride: staleTimestamp);

        var result = await AuthenticateAsync(context);

        result.Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task UnparseableTimestamp_Fails()
    {
        // Caught by the DateTime.TryParse guard, which runs before signature verification —
        // distinct from ExpiredTimestamp_Fails (a validly-formatted but stale timestamp).
        var context = CreateValidRequestContext(timestampOverride: "not-a-timestamp");

        var result = await AuthenticateAsync(context);

        result.Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task ReplayedNonce_SecondRequestFails()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var nonce = Convert.ToBase64String(Guid.NewGuid().ToByteArray());
        var timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
        var signature = CaseManagementAuthenticationHandler.ComputeSignature(
            TestSecret,
            TestClientId,
            "jane@example.com",
            "Jane Smith",
            timestamp,
            nonce
        );

        var firstContext = CreateValidRequestContext(
            timestampOverride: timestamp,
            nonceOverride: nonce,
            signatureOverride: signature
        );
        var firstResult = await AuthenticateAsync(firstContext, cache: cache);
        firstResult.Succeeded.Should().BeTrue();

        var secondContext = CreateValidRequestContext(
            timestampOverride: timestamp,
            nonceOverride: nonce,
            signatureOverride: signature
        );
        var secondResult = await AuthenticateAsync(secondContext, cache: cache);

        secondResult.Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task ReplayedNonce_ConcurrentRequests_OnlyOneSucceeds()
    {
        // Nonce reuse is checked via TryGetValue+Set, which is not atomic on its own — two
        // requests racing on the same nonce could otherwise both observe "not present" and
        // both be authenticated. Fire a batch of identical requests concurrently and assert
        // single-use is still enforced under contention.
        var cache = new MemoryCache(new MemoryCacheOptions());
        var nonce = Convert.ToBase64String(Guid.NewGuid().ToByteArray());
        var timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
        var signature = CaseManagementAuthenticationHandler.ComputeSignature(
            TestSecret,
            TestClientId,
            "jane@example.com",
            "Jane Smith",
            timestamp,
            nonce
        );

        var tasks = Enumerable
            .Range(0, 20)
            .Select(_ =>
                AuthenticateAsync(
                    CreateValidRequestContext(
                        timestampOverride: timestamp,
                        nonceOverride: nonce,
                        signatureOverride: signature
                    ),
                    cache: cache
                )
            )
            .ToArray();

        var results = await Task.WhenAll(tasks);

        results.Count(r => r.Succeeded).Should().Be(1);
    }

    [Fact]
    public async Task MissingSharedSecret_OutsideDevelopment_FailsClosed()
    {
        var context = CreateValidRequestContext();
        var result = await AuthenticateAsync(context, sharedSecret: null, isDevelopment: false);
        result.Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task MissingSharedSecret_InDevelopment_Succeeds()
    {
        var context = new DefaultHttpContext();
        var result = await AuthenticateAsync(context, sharedSecret: null, isDevelopment: true);
        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task ValidSignature_LogsCorrelationIdOnSuccess()
    {
        var loggerFactory = new CapturingLoggerFactory();
        var context = CreateValidRequestContext();
        context.Request.Headers["X-Correlation-Id"] = "corr-success-123";

        var result = await AuthenticateAsync(context, loggerFactory: loggerFactory);

        result.Succeeded.Should().BeTrue();
        loggerFactory
            .Logger.Entries.Should()
            .Contain(e => e.Message.Contains("corr-success-123"));
    }

    [Fact]
    public async Task InvalidSignature_LogsCorrelationIdOnFailure()
    {
        var loggerFactory = new CapturingLoggerFactory();
        var context = CreateValidRequestContext(signatureOverride: "not-the-real-signature");
        context.Request.Headers["X-Correlation-Id"] = "corr-failure-456";

        var result = await AuthenticateAsync(context, loggerFactory: loggerFactory);

        result.Succeeded.Should().BeFalse();
        loggerFactory
            .Logger.Entries.Should()
            .Contain(e => e.LogLevel == LogLevel.Warning && e.Message.Contains("corr-failure-456"));
    }

    [Fact]
    public async Task AuthFailure_MissingCorrelationId_LogsWithoutFailingRequest()
    {
        // Correlation id is a diagnostic aid only (RA-311) — its absence must never turn a
        // request that would otherwise be handled into a failure over-and-above whatever the
        // real auth outcome was.
        var loggerFactory = new CapturingLoggerFactory();
        var context = CreateValidRequestContext(signatureOverride: "not-the-real-signature");

        var result = await AuthenticateAsync(context, loggerFactory: loggerFactory);

        result.Succeeded.Should().BeFalse();
        loggerFactory
            .Logger.Entries.Should()
            .Contain(e => e.LogLevel == LogLevel.Warning && e.Message.Contains("(absent)"));
    }

    [Fact]
    public void ComputeSignature_MatchesManagementBeEquivalentV3Algorithm()
    {
        // Contract test (RA-311 fix 2): ManagementBe's ClientIdAuthenticationHandler
        // signs a 5-field v3 payload — "v3", clientId, userId, userName, timestamp, nonce —
        // with no role-membership field. The computation is replicated here (not copied from
        // the sibling repo) so any future drift between the two shapes fails this test in CI
        // rather than silently causing 100% auth failures again.
        const string timestamp = "2026-07-29T10:00:00Z";
        const string nonce = "contract-test-nonce";

        var expected = ManagementBeEquivalentComputeSignature(
            TestSecret,
            TestClientId,
            "jane@example.com",
            "Jane Smith",
            timestamp,
            nonce
        );

        var actual = CaseManagementAuthenticationHandler.ComputeSignature(
            TestSecret,
            TestClientId,
            "jane@example.com",
            "Jane Smith",
            timestamp,
            nonce
        );

        actual.Should().Be(expected);
    }

    // Standalone replica of ManagementBe's ClientIdAuthenticationHandler.ComputeSignature
    // (v3 canonical payload). Deliberately independent of the production implementation under
    // test above — asserting the two happen to match is the whole point of the contract test.
    private static string ManagementBeEquivalentComputeSignature(
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
        var keyBytes = System.Text.Encoding.UTF8.GetBytes(sharedSecret);
        var payloadBytes = System.Text.Encoding.UTF8.GetBytes(payload);
        var mac = System.Security.Cryptography.HMACSHA256.HashData(keyBytes, payloadBytes);
        return Convert.ToBase64String(mac);
    }

    private sealed class CapturingLoggerFactory : ILoggerFactory
    {
        public CapturingLogger Logger { get; } = new();

        public ILogger CreateLogger(string categoryName) => Logger;

        public void AddProvider(ILoggerProvider provider) { }

        public void Dispose() { }
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<(LogLevel LogLevel, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        )
        {
            Entries.Add((logLevel, formatter(state, exception)));
        }
    }

    private sealed class StaticOptionsMonitor<T>(T currentValue) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = currentValue;

        public T Get(string? name) => CurrentValue;

        public IDisposable OnChange(Action<T, string?> listener) => NullDisposable.Instance;

        private sealed class NullDisposable : IDisposable
        {
            public static readonly NullDisposable Instance = new();

            public void Dispose() { }
        }
    }
}
