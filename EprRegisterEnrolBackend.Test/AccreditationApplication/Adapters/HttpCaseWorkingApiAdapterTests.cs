using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using EprRegisterEnrolBackend.AccreditationApplication.Adapters;
using EprRegisterEnrolBackend.AccreditationApplication.Models;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace EprRegisterEnrolBackend.Test.AccreditationApplication.Adapters;

public class HttpCaseWorkingApiAdapterTests
{
    private const string TestUrl = "http://mgmt-be:8085";
    private const string TestClientId = "epr-register-enrol-backend";
    private const string TestApplicationReference = "APP26EA123451AAPL";

    private static AccreditationApplicationModel CreateTestApplication()
    {
        return new AccreditationApplicationModel
        {
            OrganisationId = "12345",
            OrganisationName = "Acme Recycling Ltd",
            Year = 2026,
            RegistrationId = "reg-001",
            RegistrationReference = "EPR-100023",
            MaterialType = MaterialType.Plastic,
            ApplicationStatus = ApplicationStatus.Started,
            SiteAddress = "123 High Street, London, SW1A 1AA",
            CompanyRegisterAddressPostcode = "EC1A 1BB",
            WasteProcessingType = "reprocessor",
            SubmittedBy = new SubmittedByModel
            {
                FullName = "Jane Smith",
                JobTitle = "Operations Manager",
                Email = "jane@example.com",
            },
        };
    }

    private static (
        HttpCaseWorkingApiAdapter adapter,
        CapturingHttpMessageHandler handler
    ) CreateAdapter(
        string? url = TestUrl,
        string clientId = TestClientId,
        string? sharedSecret = null
    )
    {
        var config = Options.Create(
            new CaseWorkingApiConfig
            {
                Url = url ?? "",
                ClientId = clientId,
                SharedSecret = sharedSecret,
            }
        );

        var handler = new CapturingHttpMessageHandler(
            HttpStatusCode.Created,
            new
            {
                id = Guid.NewGuid(),
                typeId = "re-accreditation",
                stateId = "submitted",
                payload = new { },
                applicationReference = TestApplicationReference,
            }
        );

        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient("DefaultClient").Returns(new HttpClient(handler));

        var adapter = new HttpCaseWorkingApiAdapter(
            httpClientFactory,
            config,
            NullLogger<HttpCaseWorkingApiAdapter>.Instance
        );

        return (adapter, handler);
    }

    [Fact]
    public async Task SubmitApplicationAsync_Success_ReturnsApplicationReferenceFromManagementBeResponse()
    {
        var (adapter, _) = CreateAdapter();
        var result = await adapter.SubmitApplicationAsync(CreateTestApplication());
        result.ApplicationReference.Should().Be(TestApplicationReference);
    }

    [Fact]
    public async Task SubmitApplicationAsync_Success_ReturnsWorkItemIdFromResponse()
    {
        var expectedId = Guid.NewGuid();
        var config = Options.Create(
            new CaseWorkingApiConfig { Url = TestUrl, ClientId = TestClientId }
        );
        var handler = new CapturingHttpMessageHandler(
            HttpStatusCode.Created,
            new
            {
                id = expectedId,
                typeId = "re-accreditation",
                stateId = "submitted",
                payload = new { },
                applicationReference = TestApplicationReference,
            }
        );
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient("DefaultClient").Returns(new HttpClient(handler));
        var adapter = new HttpCaseWorkingApiAdapter(
            httpClientFactory,
            config,
            NullLogger<HttpCaseWorkingApiAdapter>.Instance
        );

        var result = await adapter.SubmitApplicationAsync(CreateTestApplication());

        result.WorkItemId.Should().Be(expectedId);
    }

    [Fact]
    public async Task SubmitApplicationAsync_UnparseableResponseBody_ThrowsHttpRequestException()
    {
        // RA-318: applicationReference is ManagementBe-generated with no local fallback, so
        // an unparseable response can no longer be tolerated the way a missing id can — the
        // submission must fail rather than proceed without a valid reference to persist.
        var config = Options.Create(
            new CaseWorkingApiConfig { Url = TestUrl, ClientId = TestClientId }
        );
        var handler = new RawBodyHttpMessageHandler(HttpStatusCode.Created, "not valid json");
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient("DefaultClient").Returns(new HttpClient(handler));
        var adapter = new HttpCaseWorkingApiAdapter(
            httpClientFactory,
            config,
            NullLogger<HttpCaseWorkingApiAdapter>.Instance
        );

        var act = () => adapter.SubmitApplicationAsync(CreateTestApplication());

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task SubmitApplicationAsync_ClientTimeout_ThrowsCaseWorkingApiTimeoutException()
    {
        // Simulates the "DefaultClient" HttpClient.Timeout (Program.cs, 15s) elapsing: HttpClient
        // surfaces this as a TaskCanceledException that is NOT attributable to the caller's own
        // cancellationToken (which stays uncancelled here). RA-311 fix 3 requires this to become
        // a clean, distinguishable error rather than propagating an unhandled/generic exception.
        var config = Options.Create(
            new CaseWorkingApiConfig { Url = TestUrl, ClientId = TestClientId }
        );
        var handler = new TimeoutHttpMessageHandler();
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient("DefaultClient").Returns(new HttpClient(handler));
        var adapter = new HttpCaseWorkingApiAdapter(
            httpClientFactory,
            config,
            NullLogger<HttpCaseWorkingApiAdapter>.Instance
        );

        var act = () =>
            adapter.SubmitApplicationAsync(CreateTestApplication(), CancellationToken.None);

        await act.Should().ThrowAsync<CaseWorkingApiTimeoutException>();
    }

    [Fact]
    public async Task SubmitApplicationAsync_CallerCancellation_DoesNotThrowTimeoutException()
    {
        // Distinguishes "the client's own Timeout elapsed" from "the caller cancelled us" — only
        // the former should be reported as CaseWorkingApiTimeoutException. Both surface as
        // TaskCanceledException from HttpClient, so the adapter must tell them apart via the
        // supplied CancellationToken's own IsCancellationRequested state.
        var config = Options.Create(
            new CaseWorkingApiConfig { Url = TestUrl, ClientId = TestClientId }
        );
        var handler = new TimeoutHttpMessageHandler();
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient("DefaultClient").Returns(new HttpClient(handler));
        var adapter = new HttpCaseWorkingApiAdapter(
            httpClientFactory,
            config,
            NullLogger<HttpCaseWorkingApiAdapter>.Instance
        );

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => adapter.SubmitApplicationAsync(CreateTestApplication(), cts.Token);

        await act.Should().ThrowAsync<TaskCanceledException>();
    }

    [Fact]
    public async Task SubmitApplicationAsync_ResponseMissingApplicationReference_ThrowsHttpRequestException()
    {
        var config = Options.Create(
            new CaseWorkingApiConfig { Url = TestUrl, ClientId = TestClientId }
        );
        var handler = new CapturingHttpMessageHandler(
            HttpStatusCode.Created,
            new
            {
                id = Guid.NewGuid(),
                typeId = "re-accreditation",
                stateId = "submitted",
                payload = new { },
            }
        );
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient("DefaultClient").Returns(new HttpClient(handler));
        var adapter = new HttpCaseWorkingApiAdapter(
            httpClientFactory,
            config,
            NullLogger<HttpCaseWorkingApiAdapter>.Instance
        );

        var act = () => adapter.SubmitApplicationAsync(CreateTestApplication());

        await act.Should()
            .ThrowAsync<HttpRequestException>()
            .WithMessage("*application reference*");
    }

    [Fact]
    public async Task SubmitApplicationAsync_ResponseMissingIdField_ReturnsNullWorkItemId()
    {
        var config = Options.Create(
            new CaseWorkingApiConfig { Url = TestUrl, ClientId = TestClientId }
        );
        var handler = new CapturingHttpMessageHandler(
            HttpStatusCode.Created,
            new
            {
                typeId = "re-accreditation",
                stateId = "submitted",
                payload = new { },
                applicationReference = TestApplicationReference,
            }
        );
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient("DefaultClient").Returns(new HttpClient(handler));
        var adapter = new HttpCaseWorkingApiAdapter(
            httpClientFactory,
            config,
            NullLogger<HttpCaseWorkingApiAdapter>.Instance
        );

        var result = await adapter.SubmitApplicationAsync(CreateTestApplication());

        result.ApplicationReference.Should().Be(TestApplicationReference);
        result.WorkItemId.Should().BeNull();
    }

    [Fact]
    public async Task SubmitApplicationAsync_MapsPayloadCorrectly()
    {
        var (adapter, handler) = CreateAdapter();
        await adapter.SubmitApplicationAsync(CreateTestApplication());

        handler.CapturedRequestBody.Should().NotBeNullOrEmpty();
        var doc = JsonDocument.Parse(handler.CapturedRequestBody!);
        var root = doc.RootElement;

        root.GetProperty("typeId").GetString().Should().Be("re-accreditation");
        root.GetProperty("source").GetString().Should().Be("operator-fe");

        var payload = root.GetProperty("payload");
        payload.GetProperty("organisationName").GetString().Should().Be("Acme Recycling Ltd");
        payload.GetProperty("registrationNumber").GetString().Should().Be("EPR-100023");
        payload.GetProperty("material").GetString().Should().Be("plastic");
        payload.GetProperty("previousAccreditationYear").GetInt32().Should().Be(2025);
        payload.GetProperty("complianceIssuesReported").GetInt32().Should().Be(0);
        payload.GetProperty("operatorOrganisationId").GetString().Should().Be("12345");
        payload.GetProperty("operatorRegistrationId").GetString().Should().Be("reg-001");
        payload.GetProperty("operatorEmail").GetString().Should().Be("jane@example.com");
        payload.GetProperty("siteAddressPostcode").GetString().Should().Be("SW1A 1AA");
        payload.GetProperty("companyRegisterAddressPostcode").GetString().Should().Be("EC1A 1BB");
        payload.GetProperty("wasteProcessingType").GetString().Should().Be("reprocessor");
    }

    [Fact]
    public async Task SubmitApplicationAsync_GlassMaterial_IncludesGlassRecyclingProcessInPayload()
    {
        var application = CreateTestApplication();
        application.MaterialType = MaterialType.Glass;
        application.GlassRecyclingProcess = GlassRecyclingProcess.Remelt;

        var (adapter, handler) = CreateAdapter();
        await adapter.SubmitApplicationAsync(application);

        var doc = JsonDocument.Parse(handler.CapturedRequestBody!);
        var payload = doc.RootElement.GetProperty("payload");

        payload.GetProperty("materialsHandled")[0].GetString().Should().Be("glass");
        payload.GetProperty("glassRecyclingProcess").GetString().Should().Be("glass_re_melt");
    }

    [Fact]
    public async Task SubmitApplicationAsync_NonGlassMaterial_OmitsGlassRecyclingProcessFromPayload()
    {
        var (adapter, handler) = CreateAdapter();
        await adapter.SubmitApplicationAsync(CreateTestApplication());

        var doc = JsonDocument.Parse(handler.CapturedRequestBody!);
        var payload = doc.RootElement.GetProperty("payload");

        payload.TryGetProperty("glassRecyclingProcess", out _).Should().BeFalse();
    }

    [Fact]
    public async Task SubmitApplicationAsync_OrsSite_ForwardsOrsIdAndIsNewSite()
    {
        var application = CreateTestApplication();
        application.OverseasSites = new AccreditationApplicationOverseasSites
        {
            Sites =
            [
                new OverseasSiteModel
                {
                    SiteId = 1,
                    OrsId = "001",
                    SiteName = "Overseas Recycling Co",
                    IsNewSite = false,
                },
            ],
        };

        var (adapter, handler) = CreateAdapter();
        await adapter.SubmitApplicationAsync(application);

        var doc = JsonDocument.Parse(handler.CapturedRequestBody!);
        var site = doc
            .RootElement.GetProperty("payload")
            .GetProperty("overseasSites")
            .GetProperty("sites")[0];

        site.GetProperty("orsId").GetString().Should().Be("001");
        site.GetProperty("isNewSite").GetBoolean().Should().BeFalse();
        site.TryGetProperty("interimSite", out _).Should().BeFalse();
    }

    [Fact]
    public async Task SubmitApplicationAsync_SiteWithInterimSite_ForwardsNestedInterimSiteObject()
    {
        var application = CreateTestApplication();
        application.OverseasSites = new AccreditationApplicationOverseasSites
        {
            Sites =
            [
                new OverseasSiteModel
                {
                    SiteId = 1,
                    OrsId = "001",
                    SiteName = "Overseas Recycling Co",
                    IsNewSite = true,
                    InterimSite = new InterimSiteModel
                    {
                        SiteId = 2,
                        SiteNumber = "SN-0002",
                        Country = "France",
                        SiteName = "Interim Recycling Site",
                        AddressLine1 = "1 Rue Example",
                        TownOrCity = "Paris",
                        ContactName = "Jane Smith",
                        ContactEmail = "jane.smith@example.com",
                        ContactPhone = "+33 1 23 45 67 89",
                        // Set explicitly: IsNewSite defaults to false (RA-292), so relying on the
                        // default here would assert nothing about forwarding.
                        IsNewSite = true,
                    },
                },
            ],
        };

        var (adapter, handler) = CreateAdapter();
        await adapter.SubmitApplicationAsync(application);

        var doc = JsonDocument.Parse(handler.CapturedRequestBody!);
        var interimSite = doc
            .RootElement.GetProperty("payload")
            .GetProperty("overseasSites")
            .GetProperty("sites")[0]
            .GetProperty("interimSite");

        interimSite.GetProperty("siteNumber").GetString().Should().Be("SN-0002");
        interimSite.GetProperty("isNewSite").GetBoolean().Should().BeTrue();
        interimSite.GetProperty("siteName").GetString().Should().Be("Interim Recycling Site");
        interimSite.GetProperty("townOrCity").GetString().Should().Be("Paris");
        interimSite.GetProperty("contactPhone").GetString().Should().Be("+33 1 23 45 67 89");
    }

    #region RA-292 — ORS / interim / authority-to-issue wire contract

    // RA-292 AC01/AC02/AC04. ManagementBe can only show the regulator what we send, so the
    // serialised shape is pinned in full rather than field-by-field: a silently dropped key is
    // exactly the failure this ticket exists to fix.

    private static OverseasSiteModel FullyPopulatedSite() =>
        new()
        {
            SiteId = 1,
            OrsId = "001",
            SiteName = "Overseas Recycling Co",
            SiteAddress = "1 Rue Example, Paris, 75001",
            AddressLine1 = "1 Rue Example",
            AddressLine2 = "Zone Industrielle",
            TownOrCity = "Paris",
            Country = "France",
            Coordinates = "48.8566,2.3522",
            ContactName = "Pierre Dupont",
            ContactEmail = "pierre@example.com",
            ContactPhone = "+33 1 23 45 67 89",
            OperationCode = "R3",
            Code1 = "B3011",
            Code2 = "B3020",
            Code3 = "GH013",
            RepatriatedLoads = "12",
            ConditionsOfExport = true,
            IsEu = true,
            IsOecd = true,
            IsNewSite = true,
            RegisteredNowAccredited = true,
            BesEvidence = new BesEvidenceModel
            {
                BesEvidenceUploads =
                [
                    new BesEvidenceFileModel
                    {
                        FileId = "file-1",
                        Filename = "bes.pdf",
                        ContentType = "application/pdf",
                        ScanStatus = "complete",
                        BesEvidenceValidFromDate = "2026-01-01",
                        BesEvidenceExpiryDate = "2027-01-01",
                        UploadedAt = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
                        S3Key = "key-1",
                        S3Bucket = "bucket-1",
                    },
                ],
            },
            InterimSite = new InterimSiteModel
            {
                SiteId = 2,
                SiteNumber = "SN-0002",
                Country = "France",
                SiteName = "Interim Recycling Site",
                AddressLine1 = "9 Rue Interim",
                AddressLine2 = "Batiment B",
                TownOrCity = "Lyon",
                StateOrRegion = "Auvergne",
                Postcode = "69001",
                ContactName = "Marie Curie",
                ContactEmail = "marie@example.com",
                ContactPhone = "+33 4 11 22 33 44",
                IsNewSite = true,
            },
        };

    // Only the two properties the model makes mandatory. Stands in for a work item whose site
    // data predates RA-292 or was never captured — note it serialises with isNewSite false, since
    // a site with no stored newness must not arrive at the regulator wearing a "new" badge.
    private static OverseasSiteModel BareSite() => new() { SiteId = 3, SiteName = "Sparse Site" };

    private const string ExpectedOverseasSitesJson = """
        {
          "sites": [
            {
              "siteId": 1,
              "orsId": "001",
              "siteName": "Overseas Recycling Co",
              "siteAddress": "1 Rue Example, Paris, 75001",
              "addressLine1": "1 Rue Example",
              "addressLine2": "Zone Industrielle",
              "townOrCity": "Paris",
              "country": "France",
              "coordinates": "48.8566,2.3522",
              "contactName": "Pierre Dupont",
              "contactEmail": "pierre@example.com",
              "contactPhone": "+33 1 23 45 67 89",
              "operationCode": "R3",
              "code1": "B3011",
              "code2": "B3020",
              "code3": "GH013",
              "repatriatedLoads": "12",
              "conditionsOfExport": true,
              "isEu": true,
              "isOecd": true,
              "isNewSite": true,
              "registeredNowAccredited": true,
              "interimSite": {
                "siteId": 2,
                "siteNumber": "SN-0002",
                "isNewSite": true,
                "country": "France",
                "siteName": "Interim Recycling Site",
                "addressLine1": "9 Rue Interim",
                "addressLine2": "Batiment B",
                "townOrCity": "Lyon",
                "stateOrRegion": "Auvergne",
                "postcode": "69001",
                "contactName": "Marie Curie",
                "contactEmail": "marie@example.com",
                "contactPhone": "+33 4 11 22 33 44"
              },
              "besEvidence": {
                "files": [
                  {
                    "fileId": "file-1",
                    "filename": "bes.pdf",
                    "contentType": "application/pdf",
                    "uploadedAt": "2026-01-02T03:04:05Z",
                    "scanStatus": "complete",
                    "besEvidenceValidFromDate": "2026-01-01",
                    "besEvidenceExpiryDate": "2027-01-01",
                    "s3Key": "key-1",
                    "s3Bucket": "bucket-1"
                  }
                ]
              }
            },
            {
              "siteId": 3,
              "siteName": "Sparse Site",
              "isEu": false,
              "isOecd": false,
              "isNewSite": false,
              "registeredNowAccredited": false,
              "besEvidence": {
                "files": []
              }
            }
          ]
        }
        """;

    private const string ExpectedPrnsJson = """
        {
          "plannedTonnageBand": "UpTo1000",
          "authorisers": [
            {
              "fullName": "Old Hand",
              "email": "old@example.com",
              "isNew": false
            },
            {
              "fullName": "Fresh Face",
              "email": "fresh@example.com",
              "isNew": true
            }
          ]
        }
        """;

    // Compares field names, field order and values exactly, while letting the expected literal
    // above stay readable. JsonNode preserves object member order on both sides.
    private static string Canonical(string json) => JsonNode.Parse(json)!.ToJsonString();

    private static AccreditationApplicationModel ApplicationWithRa292Data()
    {
        var application = CreateTestApplication();
        application.CaseManagementWorkItemId = Guid.NewGuid();
        application.Prns.PlannedTonnageBand = PlannedTonnageBand.UpTo1000;
        application.Prns.Authorisers =
        [
            new PrnsAuthoriser
            {
                FullName = "Old Hand",
                Email = "old@example.com",
                IsNew = false,
            },
            new PrnsAuthoriser
            {
                FullName = "Fresh Face",
                Email = "fresh@example.com",
                IsNew = true,
            },
        ];
        application.OverseasSites = new AccreditationApplicationOverseasSites
        {
            Sites = [FullyPopulatedSite(), BareSite()],
        };
        return application;
    }

    private static async Task<JsonElement> CapturedSubmitPayload(
        AccreditationApplicationModel application
    )
    {
        var (adapter, handler) = CreateAdapter();
        await adapter.SubmitApplicationAsync(application);
        return JsonDocument.Parse(handler.CapturedRequestBody!).RootElement.GetProperty("payload");
    }

    private static async Task<JsonElement> CapturedResumeSections(
        AccreditationApplicationModel application,
        params string[] sectionKeys
    )
    {
        var (adapter, handler) = CreateAdapter();
        await adapter.ResumeFromQueryAsync(
            application,
            new QuerySubmitterContactDetails
            {
                FullName = "Jane Smith",
                Email = "jane@example.com",
                Role = "Manager",
            },
            sectionKeys
        );
        return JsonDocument.Parse(handler.CapturedRequestBody!).RootElement.GetProperty("sections");
    }

    [Fact]
    public async Task SubmitApplicationAsync_EmitsFullOverseasSitesContract()
    {
        var payload = await CapturedSubmitPayload(ApplicationWithRa292Data());

        Canonical(payload.GetProperty("overseasSites").GetRawText())
            .Should()
            .Be(Canonical(ExpectedOverseasSitesJson));
    }

    [Fact]
    public async Task SubmitApplicationAsync_EmitsAuthorisersWithIsNewFlag()
    {
        var payload = await CapturedSubmitPayload(ApplicationWithRa292Data());

        Canonical(payload.GetProperty("prns").GetRawText()).Should().Be(Canonical(ExpectedPrnsJson));
    }

    [Fact]
    public async Task ResumeFromQueryAsync_OverseasSitesSection_EmitsFullOverseasSitesContract()
    {
        // RA-292 (D): this projection used to be a weaker copy of the submit one — no orsId, no
        // isNewSite, no interimSite at all — so resubmitting a queried ORS section wiped the
        // interim site data ManagementBe held.
        var sections = await CapturedResumeSections(
            ApplicationWithRa292Data(),
            "overseas-reprocessing-sites"
        );

        Canonical(sections.GetProperty("OverseasSites").GetRawText())
            .Should()
            .Be(Canonical(ExpectedOverseasSitesJson));
    }

    [Fact]
    public async Task ResumeFromQueryAsync_PrnsSection_EmitsAuthorisersWithIsNewFlag()
    {
        var sections = await CapturedResumeSections(
            ApplicationWithRa292Data(),
            "authority-to-issue"
        );

        Canonical(sections.GetProperty("Prns").GetRawText()).Should().Be(Canonical(ExpectedPrnsJson));
    }

    [Theory]
    [InlineData("overseas-reprocessing-sites", "OverseasSites", "overseasSites")]
    [InlineData("authority-to-issue", "Prns", "prns")]
    [InlineData("prn-tonnage", "Prns", "prns")]
    public async Task ResumeFromQueryAsync_SectionMatchesSubmitPayloadByteForByte(
        string sectionKey,
        string sectionName,
        string payloadName
    )
    {
        // Belt and braces on top of the literals above: whatever the submit payload says, the
        // resubmit payload must say the same, so the two can never drift apart again.
        var application = ApplicationWithRa292Data();

        var payload = await CapturedSubmitPayload(application);
        var sections = await CapturedResumeSections(application, sectionKey);

        Canonical(sections.GetProperty(sectionName).GetRawText())
            .Should()
            .Be(Canonical(payload.GetProperty(payloadName).GetRawText()));
    }

    [Fact]
    public async Task SubmitApplicationAsync_SiteWithNoOptionalValues_OmitsThoseKeysEntirely()
    {
        // RA-292 (E): absent must stay safe for ManagementBe. Nulls are dropped rather than sent
        // as explicit nulls, and no new field is ever mandatory on the consumer side.
        var application = CreateTestApplication();
        application.OverseasSites = new AccreditationApplicationOverseasSites
        {
            Sites = [BareSite()],
        };

        var payload = await CapturedSubmitPayload(application);
        var site = payload.GetProperty("overseasSites").GetProperty("sites")[0];

        foreach (
            var absentKey in new[]
            {
                "orsId",
                "siteAddress",
                "addressLine1",
                "addressLine2",
                "townOrCity",
                "country",
                "coordinates",
                "contactName",
                "contactEmail",
                "contactPhone",
                "operationCode",
                "code1",
                "code2",
                "code3",
                "repatriatedLoads",
                "conditionsOfExport",
                "interimSite",
            }
        )
        {
            site.TryGetProperty(absentKey, out _)
                .Should()
                .BeFalse($"'{absentKey}' is null and must be omitted, not sent as null");
        }
    }

    [Fact]
    public async Task SubmitApplicationAsync_NoOverseasSites_SendsEmptySiteArray()
    {
        var payload = await CapturedSubmitPayload(CreateTestApplication());

        payload
            .GetProperty("overseasSites")
            .GetProperty("sites")
            .EnumerateArray()
            .Should()
            .BeEmpty();
    }

    [Fact]
    public async Task SubmitApplicationAsync_NoAuthorisers_SendsEmptyAuthoriserArray()
    {
        var payload = await CapturedSubmitPayload(CreateTestApplication());

        payload.GetProperty("prns").GetProperty("authorisers").EnumerateArray().Should().BeEmpty();
    }

    [Fact]
    public async Task ResumeFromQueryAsync_NoOverseasSites_SendsEmptySiteArray()
    {
        var application = CreateTestApplication();
        application.CaseManagementWorkItemId = Guid.NewGuid();
        application.OverseasSites = null;

        var sections = await CapturedResumeSections(application, "overseas-reprocessing-sites");

        sections
            .GetProperty("OverseasSites")
            .GetProperty("sites")
            .EnumerateArray()
            .Should()
            .BeEmpty();
    }

    #endregion

    [Fact]
    public async Task SubmitApplicationAsync_SetsAuthHeaders()
    {
        var (adapter, handler) = CreateAdapter();
        await adapter.SubmitApplicationAsync(CreateTestApplication());

        handler.CapturedRequest.Should().NotBeNull();
        var request = handler.CapturedRequest!;
        request.Headers.GetValues("x-cdp-client-id").Should().ContainSingle(TestClientId);
        request.Headers.GetValues("x-cdp-user-id").Should().ContainSingle("jane@example.com");
        request.Headers.GetValues("x-cdp-user-name").Should().ContainSingle("Jane Smith");
    }

    [Fact]
    public async Task SubmitApplicationAsync_WithSharedSecret_SetsHmacHeaders()
    {
        var (adapter, handler) = CreateAdapter(sharedSecret: "test-secret-key");
        await adapter.SubmitApplicationAsync(CreateTestApplication());

        var request = handler.CapturedRequest!;
        request.Headers.Contains("x-cdp-auth-signature").Should().BeTrue();
        request.Headers.Contains("x-cdp-auth-timestamp").Should().BeTrue();
        request.Headers.Contains("x-cdp-auth-nonce").Should().BeTrue();

        var timestamp = request.Headers.GetValues("x-cdp-auth-timestamp").Single();
        DateTime.TryParse(timestamp, out _).Should().BeTrue("timestamp should be parseable");

        var nonce = request.Headers.GetValues("x-cdp-auth-nonce").Single();
        nonce.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task SubmitApplicationAsync_WithSharedSecret_SignatureIsValid()
    {
        const string secret = "test-secret-key";
        var (adapter, handler) = CreateAdapter(sharedSecret: secret);
        await adapter.SubmitApplicationAsync(CreateTestApplication());

        var request = handler.CapturedRequest!;
        var timestamp = request.Headers.GetValues("x-cdp-auth-timestamp").Single();
        var nonce = request.Headers.GetValues("x-cdp-auth-nonce").Single();
        var actualSignature = request.Headers.GetValues("x-cdp-auth-signature").Single();

        var expectedSignature = HttpCaseWorkingApiAdapter.ComputeSignature(
            secret,
            TestClientId,
            "jane@example.com",
            "Jane Smith",
            timestamp,
            nonce
        );

        actualSignature.Should().Be(expectedSignature);
    }

    [Fact]
    public async Task SubmitApplicationAsync_WithoutSharedSecret_DoesNotSetHmacHeaders()
    {
        var (adapter, handler) = CreateAdapter(sharedSecret: null);
        await adapter.SubmitApplicationAsync(CreateTestApplication());

        var request = handler.CapturedRequest!;
        request.Headers.Contains("x-cdp-auth-signature").Should().BeFalse();
        request.Headers.Contains("x-cdp-auth-timestamp").Should().BeFalse();
        request.Headers.Contains("x-cdp-auth-nonce").Should().BeFalse();
        request.Headers.Contains("x-cdp-client-id").Should().BeTrue();
    }

    [Fact]
    public async Task SubmitApplicationAsync_NonSuccessResponse_ThrowsHttpRequestException()
    {
        var config = Options.Create(new CaseWorkingApiConfig { Url = TestUrl });
        var handler = new CapturingHttpMessageHandler(
            HttpStatusCode.InternalServerError,
            new { title = "Error", detail = "Something broke" }
        );

        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient("DefaultClient").Returns(new HttpClient(handler));

        var adapter = new HttpCaseWorkingApiAdapter(
            httpClientFactory,
            config,
            NullLogger<HttpCaseWorkingApiAdapter>.Instance
        );

        var act = () => adapter.SubmitApplicationAsync(CreateTestApplication());
        await act.Should().ThrowAsync<HttpRequestException>().WithMessage("*500*");
    }

    [Fact]
    public async Task SubmitApplicationAsync_DoesNotSendApplicationReferenceInRequest()
    {
        // RA-318: ManagementBe owns reference generation and ignores any value a caller
        // sends, so the backend must not send one at all — sending a value it knows will be
        // silently discarded is misleading about where the reference actually comes from.
        var (adapter, handler) = CreateAdapter();
        await adapter.SubmitApplicationAsync(CreateTestApplication());

        var doc = JsonDocument.Parse(handler.CapturedRequestBody!);
        doc.RootElement.TryGetProperty("applicationReference", out _).Should().BeFalse();
    }

    [Fact]
    public async Task SubmitApplicationAsync_EmptyUrl_ThrowsInvalidOperationException()
    {
        var (adapter, _) = CreateAdapter(url: "");

        var act = () => adapter.SubmitApplicationAsync(CreateTestApplication());
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*not configured*");
    }

    [Fact]
    public async Task SubmitApplicationAsync_NullSiteAddress_SendsNullPostcode()
    {
        var (adapter, handler) = CreateAdapter();
        var app = CreateTestApplication();
        app.SiteAddress = null;

        await adapter.SubmitApplicationAsync(app);

        var doc = JsonDocument.Parse(handler.CapturedRequestBody!);
        var payload = doc.RootElement.GetProperty("payload");
        payload
            .TryGetProperty("siteAddressPostcode", out var postcodeEl)
            .Should()
            .BeFalse("null-valued properties are excluded by WhenWritingNull");
    }

    [Fact]
    public async Task SubmitApplicationAsync_PostsToCorrectUrl()
    {
        var (adapter, handler) = CreateAdapter(url: "http://my-mgmt-be:9090");
        await adapter.SubmitApplicationAsync(CreateTestApplication());

        handler
            .CapturedRequest!.RequestUri!.ToString()
            .Should()
            .Be("http://my-mgmt-be:9090/work-items");
    }

    [Fact]
    public void ComputeSignature_MatchesKnownValue()
    {
        var result = HttpCaseWorkingApiAdapter.ComputeSignature(
            "my-secret",
            "my-client",
            null,
            null,
            "2026-06-22T10:00:00Z",
            "dGVzdC1ub25jZQ=="
        );

        // Pre-computed: HMAC-SHA256("my-secret", "v3\nmy-client\n\n\n2026-06-22T10:00:00Z\ndGVzdC1ub25jZQ==")
        // Computed with: printf 'v3\n...' | openssl dgst -sha256 -hmac 'my-secret' -binary | base64
        // Pinning the exact value (not just base64-of-32-bytes) is what
        // actually guards this port against drifting from ManagementBe's
        // canonical payload format — see the class comment on ComputeSignature.
        result.Should().Be("jjnCJCHFRVd/zdy16hBAQIzJ1NqP4OlupV4vlLvj9V4=");
    }

    [Theory]
    [InlineData("123 High Street, London, SW1A 1AA", "SW1A 1AA")]
    [InlineData("Flat 2, 10 Park Road, Manchester, M1 4BT", "M1 4BT")]
    [InlineData("SW1A 1AA", "SW1A 1AA")]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    public void ExtractPostcode_ReturnsExpected(string? input, string? expected)
    {
        HttpCaseWorkingApiAdapter.ExtractPostcode(input).Should().Be(expected);
    }

    // --- GetNotificationStatusAsync ---

    [Fact]
    public async Task GetNotificationStatusAsync_NoLinkedWorkItem_ReturnsNullWithoutCallingManagementBe()
    {
        var (adapter, handler) = CreateAdapter();
        var app = CreateTestApplication();
        app.CaseManagementWorkItemId = null;

        var result = await adapter.GetNotificationStatusAsync(app);

        result.Should().BeNull();
        handler.CapturedRequest.Should().BeNull();
    }

    [Fact]
    public async Task GetNotificationStatusAsync_ResolvesFromAuditLog()
    {
        var workItemId = Guid.NewGuid();
        var config = Options.Create(
            new CaseWorkingApiConfig { Url = TestUrl, ClientId = TestClientId }
        );
        var handler = new CapturingHttpMessageHandler(
            HttpStatusCode.OK,
            new
            {
                auditLog = new[]
                {
                    new
                    {
                        action = "notification-sent",
                        details = new Dictionary<string, string?>
                        {
                            ["templateKey"] = "SubmissionConfirmation",
                        },
                        createdAt = DateTime.UtcNow,
                    },
                },
            }
        );
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient("DefaultClient").Returns(new HttpClient(handler));
        var adapter = new HttpCaseWorkingApiAdapter(
            httpClientFactory,
            config,
            NullLogger<HttpCaseWorkingApiAdapter>.Instance
        );
        var app = CreateTestApplication();
        app.CaseManagementWorkItemId = workItemId;

        var result = await adapter.GetNotificationStatusAsync(app);

        result.Should().Be("sent");
        handler
            .CapturedRequest!.RequestUri!.ToString()
            .Should()
            .Be($"{TestUrl}/work-items/{workItemId}");
        handler.CapturedRequest!.Method.Should().Be(HttpMethod.Get);
    }

    [Fact]
    public async Task GetNotificationStatusAsync_NonSuccessResponse_ReturnsNull()
    {
        var config = Options.Create(new CaseWorkingApiConfig { Url = TestUrl });
        var handler = new CapturingHttpMessageHandler(
            HttpStatusCode.InternalServerError,
            new { title = "Error" }
        );
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient("DefaultClient").Returns(new HttpClient(handler));
        var adapter = new HttpCaseWorkingApiAdapter(
            httpClientFactory,
            config,
            NullLogger<HttpCaseWorkingApiAdapter>.Instance
        );
        var app = CreateTestApplication();
        app.CaseManagementWorkItemId = Guid.NewGuid();

        var result = await adapter.GetNotificationStatusAsync(app);

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetNotificationStatusAsync_ManagementBeUnreachable_ReturnsNull()
    {
        var config = Options.Create(new CaseWorkingApiConfig { Url = TestUrl });
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory
            .CreateClient("DefaultClient")
            .Returns(new HttpClient(new ThrowingHttpMessageHandler()));
        var adapter = new HttpCaseWorkingApiAdapter(
            httpClientFactory,
            config,
            NullLogger<HttpCaseWorkingApiAdapter>.Instance
        );
        var app = CreateTestApplication();
        app.CaseManagementWorkItemId = Guid.NewGuid();

        var result = await adapter.GetNotificationStatusAsync(app);

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetNotificationStatusAsync_EmptyUrl_ReturnsNullWithoutThrowing()
    {
        var (adapter, _) = CreateAdapter(url: "");
        var app = CreateTestApplication();
        app.CaseManagementWorkItemId = Guid.NewGuid();

        var result = await adapter.GetNotificationStatusAsync(app);

        result.Should().BeNull();
    }

    // --- ResumeFromQueryAsync ---

    [Fact]
    public async Task ResumeFromQueryAsync_NoLinkedWorkItem_ReturnsFailureWithoutCallingManagementBe()
    {
        var (adapter, handler) = CreateAdapter();
        var app = CreateTestApplication();
        app.CaseManagementWorkItemId = null;

        var result = await adapter.ResumeFromQueryAsync(
            app,
            new QuerySubmitterContactDetails
            {
                FullName = "Jane Smith",
                Email = "jane@example.com",
                Role = "Manager",
            },
            ["business-plan"]
        );

        result.IsSuccess.Should().BeFalse();
        handler.CapturedRequest.Should().BeNull();
    }

    [Fact]
    public async Task ResumeFromQueryAsync_EmptyUrl_ReturnsFailureWithoutThrowing()
    {
        var (adapter, _) = CreateAdapter(url: "");
        var app = CreateTestApplication();
        app.CaseManagementWorkItemId = Guid.NewGuid();

        var result = await adapter.ResumeFromQueryAsync(
            app,
            new QuerySubmitterContactDetails
            {
                FullName = "Jane Smith",
                Email = "jane@example.com",
                Role = "Manager",
            },
            ["business-plan"]
        );

        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task ResumeFromQueryAsync_Success_PostsToWorkItemResumeFromQueryUrl()
    {
        var workItemId = Guid.NewGuid();
        var (adapter, handler) = CreateAdapter();
        var app = CreateTestApplication();
        app.CaseManagementWorkItemId = workItemId;

        var result = await adapter.ResumeFromQueryAsync(
            app,
            new QuerySubmitterContactDetails
            {
                FullName = "Jane Smith",
                Email = "jane@example.com",
                Role = "Manager",
            },
            ["business-plan"]
        );

        result.IsSuccess.Should().BeTrue();
        handler
            .CapturedRequest!.RequestUri!.ToString()
            .Should()
            .Be($"{TestUrl}/work-items/re-accreditation/{workItemId}/resume-from-query");
        handler.CapturedRequest!.Method.Should().Be(HttpMethod.Post);
    }

    [Fact]
    public async Task ResumeFromQueryAsync_MapsResponderContactDetailsAndSectionKeysIntoPayload()
    {
        var app = CreateTestApplication();
        app.CaseManagementWorkItemId = Guid.NewGuid();
        app.BusinessPlan.NewInfrastructurePercent = 40;

        var (adapter, handler) = CreateAdapter();
        await adapter.ResumeFromQueryAsync(
            app,
            new QuerySubmitterContactDetails
            {
                FullName = "Jane Smith",
                Email = "jane@example.com",
                Role = "Manager",
            },
            ["business-plan"]
        );

        var doc = JsonDocument.Parse(handler.CapturedRequestBody!);
        var root = doc.RootElement;

        root.GetProperty("responderContactDetails")
            .GetProperty("fullName")
            .GetString()
            .Should()
            .Be("Jane Smith");
        root.GetProperty("responderContactDetails")
            .GetProperty("email")
            .GetString()
            .Should()
            .Be("jane@example.com");
        root.GetProperty("sectionKeys")[0].GetString().Should().Be("business-plan");
        root.GetProperty("sections")
            .GetProperty("BusinessPlan")
            .GetProperty("newInfrastructurePercent")
            .GetInt32()
            .Should()
            .Be(40);
    }

    [Fact]
    public async Task ResumeFromQueryAsync_DoesNotSendContactDetailsPropertyName()
    {
        // OBE-F5: MBE-1 expects "responderContactDetails", not "contactDetails" — assert the
        // old property name is genuinely absent rather than substring-matching the body.
        var app = CreateTestApplication();
        app.CaseManagementWorkItemId = Guid.NewGuid();

        var (adapter, handler) = CreateAdapter();
        await adapter.ResumeFromQueryAsync(
            app,
            new QuerySubmitterContactDetails
            {
                FullName = "Jane Smith",
                Email = "jane@example.com",
                Role = "Manager",
            },
            ["business-plan"]
        );

        var doc = JsonDocument.Parse(handler.CapturedRequestBody!);
        doc.RootElement.TryGetProperty("contactDetails", out _).Should().BeFalse();
    }

    [Fact]
    public async Task ResumeFromQueryAsync_NonSuccessResponse_ReturnsFailure()
    {
        var config = Options.Create(new CaseWorkingApiConfig { Url = TestUrl });
        var handler = new CapturingHttpMessageHandler(
            HttpStatusCode.InternalServerError,
            new { title = "Error" }
        );
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient("DefaultClient").Returns(new HttpClient(handler));
        var adapter = new HttpCaseWorkingApiAdapter(
            httpClientFactory,
            config,
            NullLogger<HttpCaseWorkingApiAdapter>.Instance
        );
        var app = CreateTestApplication();
        app.CaseManagementWorkItemId = Guid.NewGuid();

        var result = await adapter.ResumeFromQueryAsync(
            app,
            new QuerySubmitterContactDetails
            {
                FullName = "Jane Smith",
                Email = "jane@example.com",
                Role = "Manager",
            },
            ["business-plan"]
        );

        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task ResumeFromQueryAsync_ManagementBeUnreachable_ReturnsFailureWithoutThrowing()
    {
        var config = Options.Create(new CaseWorkingApiConfig { Url = TestUrl });
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory
            .CreateClient("DefaultClient")
            .Returns(new HttpClient(new ThrowingHttpMessageHandler()));
        var adapter = new HttpCaseWorkingApiAdapter(
            httpClientFactory,
            config,
            NullLogger<HttpCaseWorkingApiAdapter>.Instance
        );
        var app = CreateTestApplication();
        app.CaseManagementWorkItemId = Guid.NewGuid();

        var result = await adapter.ResumeFromQueryAsync(
            app,
            new QuerySubmitterContactDetails
            {
                FullName = "Jane Smith",
                Email = "jane@example.com",
                Role = "Manager",
            },
            ["business-plan"]
        );

        result.IsSuccess.Should().BeFalse();
    }

    // --- WithdrawApplicationAsync ---

    // The acting user withdrawing now — deliberately different from CreateTestApplication's
    // SubmittedBy (Jane Smith) so tests can prove the adapter sends the withdrawer's identity,
    // not the original submitter's (RA-252 review fix).
    private static QuerySubmitterContactDetails WithdrawingUserContactDetails() =>
        new()
        {
            FullName = "Alex Withdrawer",
            Email = "alex.withdrawer@example.com",
            Role = string.Empty,
        };

    [Fact]
    public async Task WithdrawApplicationAsync_NoLinkedWorkItem_ReturnsFailureWithoutCallingManagementBe()
    {
        var (adapter, handler) = CreateAdapter();
        var app = CreateTestApplication();
        app.CaseManagementWorkItemId = null;

        var result = await adapter.WithdrawApplicationAsync(
            app,
            WithdrawingUserContactDetails(),
            "No longer required"
        );

        result.IsSuccess.Should().BeFalse();
        handler.CapturedRequest.Should().BeNull();
    }

    [Fact]
    public async Task WithdrawApplicationAsync_EmptyUrl_ReturnsFailureWithoutThrowing()
    {
        var (adapter, _) = CreateAdapter(url: "");
        var app = CreateTestApplication();
        app.CaseManagementWorkItemId = Guid.NewGuid();

        var result = await adapter.WithdrawApplicationAsync(
            app,
            WithdrawingUserContactDetails(),
            "No longer required"
        );

        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task WithdrawApplicationAsync_Success_PostsToWorkItemWithdrawUrl()
    {
        var workItemId = Guid.NewGuid();
        var (adapter, handler) = CreateAdapter();
        var app = CreateTestApplication();
        app.CaseManagementWorkItemId = workItemId;

        var result = await adapter.WithdrawApplicationAsync(
            app,
            WithdrawingUserContactDetails(),
            "No longer required"
        );

        result.IsSuccess.Should().BeTrue();
        handler
            .CapturedRequest!.RequestUri!.ToString()
            .Should()
            .Be($"{TestUrl}/work-items/re-accreditation/{workItemId}/withdraw");
        handler.CapturedRequest!.Method.Should().Be(HttpMethod.Post);
    }

    [Fact]
    public async Task WithdrawApplicationAsync_MapsReasonIntoPayload()
    {
        var app = CreateTestApplication();
        app.CaseManagementWorkItemId = Guid.NewGuid();

        var (adapter, handler) = CreateAdapter();
        await adapter.WithdrawApplicationAsync(
            app,
            WithdrawingUserContactDetails(),
            "No longer required"
        );

        var doc = JsonDocument.Parse(handler.CapturedRequestBody!);
        doc.RootElement.GetProperty("reason").GetString().Should().Be("No longer required");
    }

    [Fact]
    public async Task WithdrawApplicationAsync_SetsAuthHeadersFromActingUserNotOriginalSubmitter()
    {
        var app = CreateTestApplication();
        app.CaseManagementWorkItemId = Guid.NewGuid();

        var (adapter, handler) = CreateAdapter();
        await adapter.WithdrawApplicationAsync(
            app,
            WithdrawingUserContactDetails(),
            "No longer required"
        );

        handler.CapturedRequest.Should().NotBeNull();
        var request = handler.CapturedRequest!;
        request
            .Headers.GetValues("x-cdp-user-id")
            .Should()
            .ContainSingle("alex.withdrawer@example.com");
        request.Headers.GetValues("x-cdp-user-name").Should().ContainSingle("Alex Withdrawer");
    }

    [Fact]
    public async Task WithdrawApplicationAsync_NonSuccessResponse_ReturnsFailure()
    {
        var config = Options.Create(new CaseWorkingApiConfig { Url = TestUrl });
        var handler = new CapturingHttpMessageHandler(
            HttpStatusCode.InternalServerError,
            new { title = "Error" }
        );
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient("DefaultClient").Returns(new HttpClient(handler));
        var adapter = new HttpCaseWorkingApiAdapter(
            httpClientFactory,
            config,
            NullLogger<HttpCaseWorkingApiAdapter>.Instance
        );
        var app = CreateTestApplication();
        app.CaseManagementWorkItemId = Guid.NewGuid();

        var result = await adapter.WithdrawApplicationAsync(
            app,
            WithdrawingUserContactDetails(),
            "No longer required"
        );

        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task WithdrawApplicationAsync_ManagementBeUnreachable_ReturnsFailureWithoutThrowing()
    {
        var config = Options.Create(new CaseWorkingApiConfig { Url = TestUrl });
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory
            .CreateClient("DefaultClient")
            .Returns(new HttpClient(new ThrowingHttpMessageHandler()));
        var adapter = new HttpCaseWorkingApiAdapter(
            httpClientFactory,
            config,
            NullLogger<HttpCaseWorkingApiAdapter>.Instance
        );
        var app = CreateTestApplication();
        app.CaseManagementWorkItemId = Guid.NewGuid();

        var result = await adapter.WithdrawApplicationAsync(
            app,
            WithdrawingUserContactDetails(),
            "No longer required"
        );

        result.IsSuccess.Should().BeFalse();
    }

    // --- NotifySiteAddedAsync ---

    [Fact]
    public async Task NotifySiteAddedAsync_NoLinkedWorkItem_DoesNotCallManagementBe()
    {
        var (adapter, handler) = CreateAdapter();
        var app = CreateTestApplication();
        app.CaseManagementWorkItemId = null;

        await adapter.NotifySiteAddedAsync(app, "ors", "001", null, true);

        handler.CapturedRequest.Should().BeNull();
    }

    [Fact]
    public async Task NotifySiteAddedAsync_OrsSite_PostsToSiteAddedUrlWithNullSiteNumber()
    {
        var workItemId = Guid.NewGuid();
        var (adapter, handler) = CreateAdapter();
        var app = CreateTestApplication();
        app.CaseManagementWorkItemId = workItemId;

        await adapter.NotifySiteAddedAsync(app, "ors", "001", null, true);

        handler
            .CapturedRequest!.RequestUri!.ToString()
            .Should()
            .Be($"{TestUrl}/work-items/re-accreditation/{workItemId}/site-added");
        handler.CapturedRequest!.Method.Should().Be(HttpMethod.Post);

        var doc = JsonDocument.Parse(handler.CapturedRequestBody!);
        var root = doc.RootElement;
        root.GetProperty("siteType").GetString().Should().Be("ors");
        root.GetProperty("orsId").GetString().Should().Be("001");
        root.GetProperty("siteNumber").ValueKind.Should().Be(JsonValueKind.Null);
        root.GetProperty("isNewSite").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task NotifySiteAddedAsync_InterimSite_PostsSiteNumber()
    {
        var workItemId = Guid.NewGuid();
        var (adapter, handler) = CreateAdapter();
        var app = CreateTestApplication();
        app.CaseManagementWorkItemId = workItemId;

        await adapter.NotifySiteAddedAsync(app, "interim", "001", "SN-0002", true);

        var doc = JsonDocument.Parse(handler.CapturedRequestBody!);
        var root = doc.RootElement;
        root.GetProperty("siteType").GetString().Should().Be("interim");
        root.GetProperty("siteNumber").GetString().Should().Be("SN-0002");
    }

    [Fact]
    public async Task NotifySiteAddedAsync_EmptyUrl_DoesNotThrow()
    {
        var (adapter, _) = CreateAdapter(url: "");
        var app = CreateTestApplication();
        app.CaseManagementWorkItemId = Guid.NewGuid();

        var act = async () => await adapter.NotifySiteAddedAsync(app, "ors", "001", null, true);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task NotifySiteAddedAsync_ManagementBeUnreachable_DoesNotThrow()
    {
        var config = Options.Create(new CaseWorkingApiConfig { Url = TestUrl });
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory
            .CreateClient("DefaultClient")
            .Returns(new HttpClient(new ThrowingHttpMessageHandler()));
        var adapter = new HttpCaseWorkingApiAdapter(
            httpClientFactory,
            config,
            NullLogger<HttpCaseWorkingApiAdapter>.Instance
        );
        var app = CreateTestApplication();
        app.CaseManagementWorkItemId = Guid.NewGuid();

        var act = async () => await adapter.NotifySiteAddedAsync(app, "ors", "001", null, true);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task NotifySiteAddedAsync_NonSuccessResponse_DoesNotThrow()
    {
        var config = Options.Create(new CaseWorkingApiConfig { Url = TestUrl });
        var handler = new CapturingHttpMessageHandler(
            HttpStatusCode.InternalServerError,
            new { title = "Error" }
        );
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient("DefaultClient").Returns(new HttpClient(handler));
        var adapter = new HttpCaseWorkingApiAdapter(
            httpClientFactory,
            config,
            NullLogger<HttpCaseWorkingApiAdapter>.Instance
        );
        var app = CreateTestApplication();
        app.CaseManagementWorkItemId = Guid.NewGuid();

        var act = async () => await adapter.NotifySiteAddedAsync(app, "ors", "001", null, true);

        await act.Should().NotThrowAsync();
    }

    #region Test infrastructure

    internal class CapturingHttpMessageHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly object _body;

        public HttpRequestMessage? CapturedRequest { get; private set; }
        public string? CapturedRequestBody { get; private set; }

        public CapturingHttpMessageHandler(HttpStatusCode status, object body)
        {
            _status = status;
            _body = body;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            CapturedRequest = request;
            if (request.Content != null)
                CapturedRequestBody = await request.Content.ReadAsStringAsync(cancellationToken);

            return new HttpResponseMessage(_status) { Content = JsonContent.Create(_body) };
        }
    }

    internal class RawBodyHttpMessageHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;

        public RawBodyHttpMessageHandler(HttpStatusCode status, string body)
        {
            _status = status;
            _body = body;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            return Task.FromResult(
                new HttpResponseMessage(_status) { Content = new StringContent(_body) }
            );
        }
    }

    internal class ThrowingHttpMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            throw new HttpRequestException("Simulated network failure");
        }
    }

    // Simulates HttpClient.Timeout elapsing: real HttpClient reports this as a
    // TaskCanceledException whose inner exception is a TimeoutException, distinct in shape from
    // a plain caller-driven OperationCanceledException.
    internal class TimeoutHttpMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            throw new TaskCanceledException(
                "The request was canceled due to the configured HttpClient.Timeout of 15 seconds elapsing.",
                new TimeoutException()
            );
        }
    }

    #endregion
}
