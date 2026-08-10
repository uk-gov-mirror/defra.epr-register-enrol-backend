using EprRegisterEnrolBackend.AccreditationApplication.Adapters;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace EprRegisterEnrolBackend.Test.Config;

// RA-345 naming-convention fix: CaseWorkingApiConfig.SharedSecret must be
// sourced from the flat CASE_MANAGEMENT_API_SHARED_SECRET env var (CDP's
// secrets naming convention — flat UPPER_SNAKE_CASE, not the nested
// CaseWorking__* form the rest of this config uses), rather than
// CaseWorking:SharedSecret. See Program.cs and CaseWorkingApiConfig.cs.
public class CaseWorkingApiConfigBindingTests
{
    [Fact]
    public async Task SharedSecret_BindsFromFlatCaseManagementApiSharedSecretEnvVar()
    {
        await using var factory = new BindingTestFactory(
            new Dictionary<string, string?>
            {
                ["CASE_MANAGEMENT_API_SHARED_SECRET"] = "test-secret",
                ["CaseWorking:Url"] = "http://example.test",
                ["CaseWorking:ClientId"] = "epr-register-enrol-backend",
            }
        );
        using var scope = factory.Services.CreateScope();

        var config = scope
            .ServiceProvider.GetRequiredService<IOptions<CaseWorkingApiConfig>>()
            .Value;

        config.SharedSecret.Should().Be("test-secret");
        config.Url.Should().Be("http://example.test");
        config.ClientId.Should().Be("epr-register-enrol-backend");
    }

    [Fact]
    public async Task SharedSecret_IgnoresRetiredNestedCaseWorkingSharedSecretKey()
    {
        // The old CaseWorking__SharedSecret env var name must have no
        // effect after the naming-convention fix — otherwise an operator
        // who hasn't migrated their secret's env var name yet would be
        // silently unsigned (empty SharedSecret) rather than getting a
        // clear signal that the old name no longer works.
        await using var factory = new BindingTestFactory(
            new Dictionary<string, string?> { ["CaseWorking:SharedSecret"] = "should-not-be-used" }
        );
        using var scope = factory.Services.CreateScope();

        var config = scope
            .ServiceProvider.GetRequiredService<IOptions<CaseWorkingApiConfig>>()
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
