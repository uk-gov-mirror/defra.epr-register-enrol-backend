namespace EprRegisterEnrolBackend.AccreditationApplication.Adapters;

public class CaseWorkingApiConfig
{
    public string Url { get; set; } = "http://localhost:8085";

    public string ClientId { get; set; } = "epr-register-enrol-backend";

    /// <summary>
    /// HMAC secret this service signs its outbound calls to ManagementBe
    /// with. Sourced from the flat <c>CASE_MANAGEMENT_API_SHARED_SECRET</c>
    /// env var (CDP's secrets naming convention — flat UPPER_SNAKE_CASE,
    /// not the nested <c>CaseWorking__*</c> form the rest of this config
    /// uses) rather than the <c>CaseWorking</c> config section — see
    /// <c>Program.cs</c>. Must match
    /// <c>AUTH_SHARED_SECRET__BACKEND</c> on ManagementBe exactly.
    /// </summary>
    public string? SharedSecret { get; set; }

    public bool UseStub { get; set; } = true;
}
