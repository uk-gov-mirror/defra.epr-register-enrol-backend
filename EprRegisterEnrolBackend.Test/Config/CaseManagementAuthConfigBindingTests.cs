using EprRegisterEnrolBackend.AccreditationApplication.Adapters;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace EprRegisterEnrolBackend.Test.Config;

// Naming-convention fix: CaseManagementAuthConfig.SharedSecret must be sourced
// from the flat AUTH_SHARED_SECRET__MANAGEMENT_BE env var (CDP's secrets naming
// convention — flat UPPER_SNAKE_CASE, not the nested CaseManagementAuth__*
// form ExpectedClientId uses), rather than CaseManagementAuth:SharedSecret.
// See Program.cs and CaseManagementAuthConfig.cs.
public class CaseManagementAuthConfigBindingTests
{
    [Fact]
    public async Task SharedSecret_BindsFromAuthSharedSecretManagementBeConfigKey()
    {
        // Config-key (colon) form — proves the lookup key itself is right.
        // ClientSecrets_bind_from_real_double_underscore_environment_variable
        // (below) proves the real, double-underscore-named env var reaches
        // this same config key via EnvironmentVariablesConfigurationProvider.
        await using var factory = new BindingTestFactory(
            new Dictionary<string, string?>
            {
                ["AUTH_SHARED_SECRET:MANAGEMENT_BE"] = "test-secret",
                ["CaseManagementAuth:ExpectedClientId"] = "epr-register-enrol-management-be",
            }
        );
        using var scope = factory.Services.CreateScope();

        var config = scope
            .ServiceProvider.GetRequiredService<IOptions<CaseManagementAuthConfig>>()
            .Value;

        config.SharedSecret.Should().Be("test-secret");
        config.ExpectedClientId.Should().Be("epr-register-enrol-management-be");
    }

    [Fact]
    public async Task SharedSecret_IgnoresRetiredNestedCaseManagementAuthSharedSecretKey()
    {
        // The old CaseManagementAuth__SharedSecret env var name must have no
        // effect after the naming-convention fix — otherwise an operator who
        // hasn't migrated their secret's env var name yet would be silently
        // unsigned (empty SharedSecret) rather than getting a clear signal
        // that the old name no longer works.
        await using var factory = new BindingTestFactory(
            new Dictionary<string, string?>
            {
                ["CaseManagementAuth:SharedSecret"] = "should-not-be-used",
            }
        );
        using var scope = factory.Services.CreateScope();

        var config = scope
            .ServiceProvider.GetRequiredService<IOptions<CaseManagementAuthConfig>>()
            .Value;

        config.SharedSecret.Should().BeNullOrEmpty();
    }

    private sealed class BindingTestFactory(IReadOnlyDictionary<string, string?> configOverrides)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration(
                (_, config) => config.AddInMemoryCollection(configOverrides)
            );
        }
    }
}

/// <summary>
/// Collection that disables parallelization for tests that mutate
/// process-global environment variables.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class CaseManagementAuthEnvVarMutationCollection
{
    public const string Name = "case-management-auth-env-var-mutation";
}

// Regression test for the exact bug class an external review caught once
// already in this project (see ManagementBe's ClientIdAuthentication
// BuildClientSecrets fix): EnvironmentVariablesConfigurationProvider rewrites
// "__" to ":" only for REAL OS environment variables, not for config injected
// via AddInMemoryCollection/UseSetting — so a config-key-level test alone
// cannot prove the real env var actually binds. This sets and reads a real
// process environment variable to prove it does.
[Collection(CaseManagementAuthEnvVarMutationCollection.Name)]
public class CaseManagementAuthConfigEnvVarBindingTests
{
    [Fact]
    public async Task SharedSecret_binds_from_the_real_double_underscore_environment_variable()
    {
        const string envVarName = "AUTH_SHARED_SECRET__MANAGEMENT_BE";
        var previous = Environment.GetEnvironmentVariable(envVarName);
        try
        {
            Environment.SetEnvironmentVariable(envVarName, "real-env-var-secret");
            await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(
                builder => builder.UseEnvironment("Development")
            );
            using var scope = factory.Services.CreateScope();

            var config = scope
                .ServiceProvider.GetRequiredService<IOptions<CaseManagementAuthConfig>>()
                .Value;

            config.SharedSecret.Should().Be("real-env-var-secret");
        }
        finally
        {
            Environment.SetEnvironmentVariable(envVarName, previous);
        }
    }
}
