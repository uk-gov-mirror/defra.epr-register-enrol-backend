namespace EprRegisterEnrolBackend.AccreditationApplication.Adapters;

public class CaseManagementAuthConfig
{
    /// <summary>
    /// Sourced from the flat <c>AUTH_SHARED_SECRET__MANAGEMENT_BE</c> env var
    /// (looked up via its config-key colon form,
    /// <c>AUTH_SHARED_SECRET:MANAGEMENT_BE</c>), not a nested
    /// <c>CaseManagementAuth__*</c> key — see Program.cs's binding.
    /// </summary>
    public string? SharedSecret { get; set; }

    public string ExpectedClientId { get; set; } = "epr-register-enrol-management-be";
}
