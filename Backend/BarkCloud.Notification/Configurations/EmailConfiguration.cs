namespace BarkCloud.Notification.Configurations;

public class EmailConfiguration
{
    public string Host { get; set; }

    public int Port { get; set; }

    public string SenderEmail { get; set; }

    public string SenderPassword { get; set; }

    public bool AllowInsecure { get; set; }

    public static bool ParseAllowInsecure(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;
        if (bool.TryParse(value, out var allowInsecure))
            return allowInsecure;

        throw new FormatException("SMTP_ALLOW_INSECURE должен быть true или false.");
    }
}