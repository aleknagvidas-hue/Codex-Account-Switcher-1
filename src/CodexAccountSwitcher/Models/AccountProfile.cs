namespace CodexAccountSwitcher.Models;

public sealed class AccountProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string DisplayName { get; set; } = "Account";
    public string ColorHex { get; set; } = "#7C8CFF";
    public string Fingerprint { get; set; } = string.Empty;
    public string? BrowserProfileKey { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastUsedAt { get; set; }
    public UsageSnapshot? Usage { get; set; }
}

public sealed class UsageSnapshot
{
    public string Status { get; set; } = "unavailable";
    public string? Message { get; set; }
    public string? PlanType { get; set; }
    public DateTimeOffset CheckedAt { get; set; } = DateTimeOffset.UtcNow;
    public UsageWindow? ShortTerm { get; set; }
    public UsageWindow? Weekly { get; set; }
}

public sealed class UsageWindow
{
    public double RemainingPercent { get; set; }
    public double UsedPercent { get; set; }
    public int? WindowDurationMinutes { get; set; }
    public DateTimeOffset? ResetsAt { get; set; }
}

public sealed record AppServerAuthResult(byte[] AuthJson, string? PlanType);

public sealed record AppServerUsageResult(UsageSnapshot Snapshot, byte[]? UpdatedAuthJson);
