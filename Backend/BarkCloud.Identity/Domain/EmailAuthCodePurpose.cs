namespace BarkCloud.Identity.Domain;

/// <summary>
/// Назначение одноразового email-кода: код, выданный для одного сценария, не подходит для другого.
/// </summary>
public enum EmailAuthCodePurpose
{
    Login = 1,
    EnableEmailOtp = 2
}
