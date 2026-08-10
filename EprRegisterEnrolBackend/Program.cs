using System.Diagnostics.CodeAnalysis;
using EprRegisterEnrolBackend.AccreditationApplication.Adapters;
using EprRegisterEnrolBackend.AccreditationApplication.Endpoints;
using EprRegisterEnrolBackend.AccreditationApplication.Services;
using EprRegisterEnrolBackend.Auth;
using EprRegisterEnrolBackend.CdpUploader.Config;
using EprRegisterEnrolBackend.CdpUploader.Services;
using EprRegisterEnrolBackend.Config;
using EprRegisterEnrolBackend.FileUpload.Config;
using EprRegisterEnrolBackend.FileUpload.Endpoints;
using EprRegisterEnrolBackend.FileUpload.Services;
using EprRegisterEnrolBackend.Organisation.Endpoints;
using EprRegisterEnrolBackend.Organisation.Services;
using EprRegisterEnrolBackend.ReEx;
using EprRegisterEnrolBackend.ReEx.Config;
using EprRegisterEnrolBackend.StubPersistence.Endpoints;
using EprRegisterEnrolBackend.StubPersistence.Services;
using EprRegisterEnrolBackend.Utils;
using EprRegisterEnrolBackend.Utils.Http;
using EprRegisterEnrolBackend.Utils.Logging;
using EprRegisterEnrolBackend.Utils.Mongo;
using FluentValidation;
using MongoDB.Driver;
using MongoDB.Driver.Authentication.AWS;
using Serilog;

var app = CreateWebApplication(args);
await app.RunAsync();
return;

[ExcludeFromCodeCoverage]
static WebApplication CreateWebApplication(string[] args)
{
    var builder = WebApplication.CreateBuilder(args);
    ConfigureBuilder(builder);

    // Suppress the "Server: Kestrel" response header. It's a minor
    // fingerprinting aid with no direct exploit path, but there's no
    // reason to advertise the server stack (pentest 2026-08-08, L4).
    builder.WebHost.ConfigureKestrel(options => options.AddServerHeader = false);

    var app = builder.Build();
    return SetupApplication(app);
}

[ExcludeFromCodeCoverage]
static void ConfigureBuilder(WebApplicationBuilder builder)
{
    builder.Configuration.AddEnvironmentVariables();

    // Load certificates into Trust Store - Note must happen before Mongo and Http client connections.
    builder.Services.AddCustomTrustStore();

    // Configure logging to use the CDP Platform standards.
    builder.Services.AddHttpContextAccessor();
    builder.Host.UseSerilog(CdpLogging.Configuration);

    // Default HTTP Client. Used (among other things) for the OJ BE -> ManagementBe submit call
    // (HttpCaseWorkingApiAdapter.SubmitApplicationAsync). Without an explicit Timeout this
    // defaults to .NET's 100s, which can leave OJ BE still waiting long after OJ FE's own
    // ~20s submit-call budget has already given up, producing a false-failure page for the
    // operator (RA-311). 15s keeps this comfortably under that budget with margin; it is a
    // principled starting point, not measured CM BE latency.
    builder
        .Services.AddHttpClient(
            "DefaultClient",
            client => client.Timeout = TimeSpan.FromSeconds(15)
        )
        .AddHeaderPropagation();

    // Proxy HTTP Client
    builder.Services.AddTransient<ProxyHttpMessageHandler>();
    builder
        .Services.AddHttpClient("proxy")
        .ConfigurePrimaryHttpMessageHandler<ProxyHttpMessageHandler>();

    // Propagate trace header.
    builder.Services.AddHeaderPropagation(options =>
    {
        var traceHeader = builder.Configuration.GetValue<string>("TraceHeader");
        if (!string.IsNullOrWhiteSpace(traceHeader))
        {
            options.Headers.Add(traceHeader);
        }
    });

    // Set up the MongoDB client. Config and credentials are injected automatically at runtime.
    // Guard against duplicate registration when the factory is instantiated multiple times in tests.
    try
    {
        MongoClientSettings.Extensions.AddAWSAuthentication();
    }
    catch (ArgumentException)
    { /* already registered */
    }
    builder.Services.Configure<MongoConfig>(builder.Configuration.GetSection("Mongo"));
    builder.Services.AddSingleton<IMongoDbClientFactory, MongoDbClientFactory>();

    builder.Services.AddExceptionHandler<ExceptionLoggingHandler>();
    builder.Services.AddProblemDetails();

    // Add healthcheck, this is required for the platform to know your service is alive.
    builder.Services.AddHealthChecks();
    // Swagger/OpenAPI
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen();

    // Allow enum values to be serialised/deserialised by name (e.g. "Wood") rather than index.
    builder.Services.ConfigureHttpJsonOptions(options =>
        options.SerializerOptions.Converters.Add(
            new System.Text.Json.Serialization.JsonStringEnumConverter()
        )
    );
    builder.Services.AddValidatorsFromAssemblyContaining<Program>();
    builder.Services.ConfigureHttpJsonOptions(options =>
        options.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
    );

    // Accreditation Application
    builder.Services.AddSingleton<
        IAccreditationApplicationPersistence,
        AccreditationApplicationPersistence
    >();
    // File Uploads
    builder.Services.AddSingleton<IFileUploadPersistence, FileUploadPersistence>();
    builder.Services.Configure<S3Config>(builder.Configuration.GetSection("S3"));
    builder.Services.AddSingleton<IS3Service, S3Service>();

    // CDP Uploader
    builder.Services.Configure<CdpUploaderConfig>(builder.Configuration.GetSection("CdpUploader"));
    builder.Services.Configure<AppConfig>(builder.Configuration.GetSection("App"));
    builder.Services.AddSingleton<ICdpUploaderService, CdpUploaderService>();
    builder.Services.AddSingleton<IPendingUploadService, PendingUploadService>();

    // CaseManagement inbound auth: verifies pushes from ManagementBe (RA-311 OBE-2).
    // SharedSecret is deliberately NOT part of the CaseManagementAuth__* section —
    // CDP's secrets naming convention is a flat UPPER_SNAKE_CASE name, not the
    // nested Section__Property form non-secret config uses — so it's sourced from
    // AUTH_SHARED_SECRET__MANAGEMENT_BE instead, extending the same AUTH_SHARED_SECRET__*
    // per-caller family ManagementBe itself uses for its own inbound callers
    // (AUTH_SHARED_SECRET__MANAGEMENT_FE / AUTH_SHARED_SECRET__BACKEND) — this service
    // has exactly one known caller (ManagementBe) today, but naming by caller rather
    // than by feature keeps room to add another without renaming this one.
    //
    // Config-key form, NOT the literal env var name: EnvironmentVariablesConfigurationProvider
    // rewrites "__" to ":" while loading, so the real env var
    // AUTH_SHARED_SECRET__MANAGEMENT_BE is stored under config key
    // "AUTH_SHARED_SECRET:MANAGEMENT_BE" — a GetValue call using the literal
    // double-underscore string never matches it (see ManagementBe's own
    // ClientIdAuthentication BuildClientSecrets for the same gotcha).
    builder.Services.AddMemoryCache();
    builder.Services.Configure<CaseManagementAuthConfig>(config =>
    {
        builder.Configuration.GetSection("CaseManagementAuth").Bind(config);
        config.SharedSecret = builder.Configuration.GetValue<string>(
            "AUTH_SHARED_SECRET:MANAGEMENT_BE"
        );
    });
    builder
        .Services.AddAuthentication()
        .AddScheme<CaseManagementAuthenticationOptions, CaseManagementAuthenticationHandler>(
            CaseManagementAuthenticationHandler.SchemeName,
            _ => { }
        );
    builder.Services.AddAuthorization();

    // CaseWorking: config-driven stub/real switch (default: stub). SharedSecret
    // is deliberately NOT part of the CaseWorking__* section — CDP's secrets
    // naming convention is a flat UPPER_SNAKE_CASE name (e.g. AUTH_SHARED_SECRET,
    // NOTIFY_API_KEY), not the nested Section__Property form non-secret config
    // uses, so it's sourced from CASE_MANAGEMENT_API_SHARED_SECRET instead.
    builder.Services.Configure<CaseWorkingApiConfig>(config =>
    {
        builder.Configuration.GetSection("CaseWorking").Bind(config);
        config.SharedSecret = builder.Configuration.GetValue<string>(
            "CASE_MANAGEMENT_API_SHARED_SECRET"
        );
    });
    var caseWorkingConfig = new CaseWorkingApiConfig();
    builder.Configuration.GetSection("CaseWorking").Bind(caseWorkingConfig);
    caseWorkingConfig.SharedSecret = builder.Configuration.GetValue<string>(
        "CASE_MANAGEMENT_API_SHARED_SECRET"
    );
    if (caseWorkingConfig.UseStub)
        builder.Services.AddSingleton<ICaseWorkingApiAdapter, StubCaseWorkingApiAdapter>();
    else
        builder.Services.AddSingleton<ICaseWorkingApiAdapter, HttpCaseWorkingApiAdapter>();

    // ReEx API client (org + overseas-sites)
    builder.Services.AddReExClients(builder.Configuration);

    if (builder.Environment.IsDevelopment())
    {
        builder.Services.AddSingleton<IReExApiAdapter, StubReExApiAdapter>();

        builder.Services.AddHostedService<EprRegisterEnrolBackend.CdpUploader.Services.DevScanAutoCompleteService>();
        builder.Services.AddSingleton<IStubApplicationPersistence, StubApplicationPersistence>();

        builder.Services.AddSingleton<OrganisationPersistence>();
        builder.Services.AddSingleton<FakeOrganisationPersistence>();
        builder.Services.AddSingleton<IOrganisationPersistence, FallbackOrganisationPersistence>();
    }
    else
    {
        builder.Services.AddSingleton<IReExApiAdapter, HttpReExApiAdapter>();

        builder.Services.AddSingleton<IOrganisationPersistence, OrganisationPersistence>();
    }
}

[ExcludeFromCodeCoverage]
static WebApplication SetupApplication(WebApplication app)
{
    var startupLogger = app.Services.GetRequiredService<ILogger<Program>>();
    var cdpConfig = app
        .Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<CdpUploaderConfig>>()
        .Value;
    var appCfg = app
        .Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<AppConfig>>()
        .Value;
    var s3Cfg = app
        .Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<S3Config>>()
        .Value;

    if (string.IsNullOrWhiteSpace(cdpConfig.Url))
        startupLogger.LogWarning(
            "CDP_UPLOADER_URL (CdpUploader:Url) is not configured — file uploads will fail at runtime."
        );
    if (string.IsNullOrWhiteSpace(appCfg.BaseUrl))
        startupLogger.LogWarning(
            "APP_BASE_URL (App:BaseUrl) is not configured — CDP callback and status URLs will be incorrect."
        );
    if (string.IsNullOrWhiteSpace(s3Cfg.Region))
        startupLogger.LogWarning(
            "S3_REGION (S3:Region) is not configured — file downloads may fail at runtime."
        );

    var reExConfig = app
        .Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<ReExConfig>>()
        .Value;
    if (string.IsNullOrWhiteSpace(reExConfig.BaseUrl))
        startupLogger.LogWarning(
            "ReExApi__BaseUrl is not configured — ReEx API calls will fail at runtime."
        );

    var reExCreds = app
        .Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<ReExCredentials>>()
        .Value;
    if (string.IsNullOrWhiteSpace(reExCreds.Username))
        startupLogger.LogWarning(
            "REEX_API_BASIC_AUTH_USERNAME is not configured — ReEx API calls will be unauthenticated."
        );
    if (string.IsNullOrWhiteSpace(reExCreds.Password))
        startupLogger.LogWarning(
            "REEX_API_BASIC_AUTH_PASSWORD is not configured — ReEx API calls will be unauthenticated."
        );

    app.UseExceptionHandler();
    app.UseHeaderPropagation();
    app.UseRouting();
    app.UseAuthentication();
    app.UseAuthorization();
    app.MapHealthChecks("/health")
        .WithMetadata(new HttpMethodMetadata(new[] { HttpMethods.Get, HttpMethods.Head }));

    // Enable Swagger UI so the API can be explored in the browser
    app.UseSwagger();
    app.UseSwaggerUI();

    // Organisation endpoints
    app.UseOrganisationEndpoints();
    // ReEx-backed organisation endpoints (live ReEx lookups, e.g. Defra org link)
    app.UseReExOrganisationEndpoints();
    // Accreditation application endpoints
    app.UseAccreditationApplicationEndpoints();
    // File upload endpoints
    app.UseFileUploadEndpoints();

    if (app.Environment.IsDevelopment())
    {
        app.UseStubApplicationEndpoints();
    }

    return app;
}
