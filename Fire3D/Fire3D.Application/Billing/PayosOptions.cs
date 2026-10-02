namespace Fire3D.Application.Billing;

public sealed class PayosOptions
{
    public const string Section = "PayOS";
    public bool Enabled { get; set; }
    public bool WorkerEnabled { get; set; }
    public int PollSeconds { get; set; } = 30;
    public string ClientId { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string ChecksumKey { get; set; } = "";
    public string ReturnUrl { get; set; } = "";
    public string CancelUrl { get; set; } = "";
}
