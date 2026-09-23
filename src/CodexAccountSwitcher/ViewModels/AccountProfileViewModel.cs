using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using CodexAccountSwitcher.Models;

namespace CodexAccountSwitcher.ViewModels;

public sealed class AccountProfileViewModel : INotifyPropertyChanged
{
    private bool _isActive;
    private bool _isRefreshing;
    private int _resetOrder;
    private bool _isTopChoice;

    public AccountProfileViewModel(AccountProfile profile) => Profile = profile;

    public AccountProfile Profile { get; }
    public int FreshForMinutes { get; set; } = 10;
    public string DisplayName => Profile.DisplayName;
    public string ColorHex => Profile.ColorHex;
    public string Initial => string.IsNullOrWhiteSpace(DisplayName) ? "?" : DisplayName.Trim()[0].ToString().ToUpperInvariant();
    public string PlanText => string.IsNullOrWhiteSpace(Profile.Usage?.PlanType)
        ? "Codex account"
        : $"Codex • {Profile.Usage.PlanType!.ToUpperInvariant()}";

    public DateTimeOffset MembershipEndsAt => Profile.CreatedAt.AddMonths(1);
    public string AddedAtText => Profile.CreatedAt.ToLocalTime()
        .ToString("dd MMM yyyy  •  HH:mm", CultureInfo.InvariantCulture);
    public string MembershipEndsText => MembershipEndsAt.ToLocalTime()
        .ToString("dd MMM yyyy  •  HH:mm", CultureInfo.InvariantCulture);
    public bool MembershipExpired => MembershipEndsAt <= DateTimeOffset.UtcNow;
    public string MembershipRemainingText => FormatMembershipRemaining(MembershipEndsAt, DateTimeOffset.UtcNow);
    public string MembershipBrush
    {
        get
        {
            var remaining = MembershipEndsAt - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero) return "#FF6B72";
            if (remaining <= TimeSpan.FromDays(3)) return "#FFB45E";
            return "#5BE0A4";
        }
    }
    public string MembershipSurface => MembershipBrush switch
    {
        "#5BE0A4" => "#235BE0A4",
        "#FFB45E" => "#24FFB45E",
        _ => "#24FF6B72"
    };

    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (_isActive == value) return;
            _isActive = value;
            Notify();
            Notify(nameof(CanSwitch));
            Notify(nameof(SwitchLabel));
        }
    }

    public bool IsRefreshing
    {
        get => _isRefreshing;
        set
        {
            if (_isRefreshing == value) return;
            _isRefreshing = value;
            Notify();
            Notify(nameof(UsageCaption));
            Notify(nameof(FreshnessText));
        }
    }

    public int ResetOrder
    {
        get => _resetOrder;
        set
        {
            if (_resetOrder == value) return;
            _resetOrder = value;
            Notify();
            Notify(nameof(ResetOrderText));
        }
    }

    public bool IsTopChoice
    {
        get => _isTopChoice;
        set
        {
            if (_isTopChoice == value) return;
            _isTopChoice = value;
            Notify();
            Notify(nameof(PriorityText));
            Notify(nameof(CardBackground));
            Notify(nameof(CardBorderBrush));
        }
    }

    public bool CanSwitch => !IsActive;
    public string SwitchLabel => IsActive ? "Active" : "Switch";
    public string ResetOrderText => ResetOrder > 0 ? ResetOrder.ToString(CultureInfo.InvariantCulture) : "—";

    public double ShortTermValue => Math.Clamp(Profile.Usage?.ShortTerm?.RemainingPercent ?? 0, 0, 100);
    public double WeeklyValue => Math.Clamp(Profile.Usage?.Weekly?.RemainingPercent ?? 0, 0, 100);
    public string ShortTermPercentText => Profile.Usage?.ShortTerm is { } shortTerm
        ? $"{Math.Round(shortTerm.RemainingPercent):0}%"
        : "—";
    public string WeeklyPercentText => Profile.Usage?.Weekly is { } weekly
        ? $"{Math.Round(weekly.RemainingPercent):0}%"
        : "—";

    public string UsageCaption
    {
        get
        {
            if (IsRefreshing) return "Loading…";
            if (Profile.Usage?.Weekly?.ResetsAt is { } reset)
                return $"Weekly • resets {reset.ToLocalTime().ToString("MMM d, HH:mm", CultureInfo.InvariantCulture)}";
            if (Profile.Usage?.Status == "unavailable") return "Weekly usage unavailable";
            return "Weekly usage • not checked";
        }
    }

    public string ShortTermText => Profile.Usage?.ShortTerm is { } window
        ? $"5-hour  {Math.Round(window.RemainingPercent):0}% left"
        : "5-hour  unavailable";
    public string WeeklyDetailText => Profile.Usage?.Weekly is { } window
        ? $"Weekly  {Math.Round(window.RemainingPercent):0}% left"
        : "Weekly  unavailable";
    public bool HasShortTermReset => Profile.Usage?.ShortTerm?.ResetsAt is not null;
    public bool HasWeeklyReset => Profile.Usage?.Weekly?.ResetsAt is not null;
    public string ShortTermResetText => ResetLabel(Profile.Usage?.ShortTerm?.ResetsAt);
    public string WeeklyResetText => ResetLabel(Profile.Usage?.Weekly?.ResetsAt);

    public bool IsStale => Profile.Usage is null
        || Profile.Usage.Status != "available"
        || DateTimeOffset.UtcNow - Profile.Usage.CheckedAt > TimeSpan.FromMinutes(FreshForMinutes);

    public bool HasCurrentUsage => !IsStale && KnownWindows().Count > 0;
    public bool IsAvailableNow => HasCurrentUsage && KnownWindows().All(window => window.RemainingPercent > 0);
    public bool IsWaitingForReset => HasCurrentUsage && KnownWindows().Any(window => window.RemainingPercent <= 0);

    public DateTimeOffset? AvailableAt
    {
        get
        {
            if (!IsWaitingForReset) return null;
            var blocking = KnownWindows().Where(window => window.RemainingPercent <= 0).ToArray();
            if (blocking.Length == 0 || blocking.Any(window => window.ResetsAt is null)) return null;
            return blocking.Max(window => window.ResetsAt!.Value);
        }
    }

    public int AvailabilitySortGroup => IsAvailableNow ? 0 : IsWaitingForReset && AvailableAt is not null ? 1 : IsWaitingForReset ? 2 : 3;
    public double AvailabilityCapacityScore => HasCurrentUsage
        ? KnownWindows().Min(window => window.RemainingPercent)
        : -1;

    public string AvailabilityStatusText
    {
        get
        {
            if (IsAvailableNow) return "AVAILABLE NOW";
            if (IsWaitingForReset) return "WAITING FOR RESET";
            if (Profile.Usage?.CheckedAt is null || Profile.Usage.CheckedAt == DateTimeOffset.MinValue) return "CHECK USAGE";
            return IsStale ? "STALE READING" : "STATUS UNKNOWN";
        }
    }

    public string AvailabilityClockText
    {
        get
        {
            if (IsAvailableNow) return "NOW";
            if (AvailableAt is not { } availableAt) return "—";
            var remaining = availableAt - DateTimeOffset.UtcNow;
            return remaining <= TimeSpan.Zero ? "REFRESH" : FormatDuration(remaining);
        }
    }

    public string AvailabilityClockCaption => IsAvailableNow
        ? "READY TO USE"
        : IsWaitingForReset ? "AVAILABLE IN" : "NO CURRENT DATA";

    public string AvailabilityExactText => AvailableAt is { } availableAt
        ? $"Usable {availableAt.ToLocalTime().ToString("MMM d  •  HH:mm", CultureInfo.InvariantCulture)}"
        : IsAvailableNow ? $"{Math.Round(AvailabilityCapacityScore):0}% minimum left" : "Press Refresh";

    public string PriorityText => IsTopChoice
        ? IsAvailableNow ? "USE FIRST" : IsWaitingForReset ? "NEXT RESET" : "CHECK FIRST"
        : IsAvailableNow ? "READY" : IsWaitingForReset ? "RESET ORDER" : "UNRANKED";

    public string ClockBrush
    {
        get
        {
            if (IsAvailableNow) return "#5BE0A4";
            if (AvailableAt is not { } availableAt) return "#778091";
            var remaining = availableAt - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.FromHours(1)) return "#5BE0A4";
            if (remaining <= TimeSpan.FromHours(4)) return "#FFB45E";
            return "#FF6B72";
        }
    }

    public string ClockSurface => ClockBrush switch
    {
        "#5BE0A4" => "#235BE0A4",
        "#FFB45E" => "#24FFB45E",
        "#FF6B72" => "#24FF6B72",
        _ => "#20778091"
    };

    public string StatusBackground => IsAvailableNow ? "#2B5BE0A4" : IsWaitingForReset ? ClockSurface : "#20778091";
    public string StatusForeground => ClockBrush;
    public string RankBackground => ClockSurface;
    public string CardBackground => IsAvailableNow
        ? IsTopChoice ? "#E11B3028" : "#D7192924"
        : IsTopChoice ? "#DD2D2922" : "#D120232B";
    public string CardBorderBrush => IsTopChoice || IsAvailableNow ? ClockBrush : "#35FFFFFF";

    public string FreshnessText
    {
        get
        {
            if (IsRefreshing) return "Refreshing...";
            if (Profile.Usage?.CheckedAt is not { } checkedAt || checkedAt == DateTimeOffset.MinValue)
                return "Not checked";
            var age = DateTimeOffset.UtcNow - checkedAt;
            var prefix = IsStale ? "STALE · " : "Updated ";
            return age.TotalMinutes < 1 ? prefix + "just now" : prefix + $"{Math.Max(1, Math.Floor(age.TotalMinutes)):0} min ago";
        }
    }

    public string UsageHint => Profile.Usage?.Message ?? "Usage is read through an isolated local Codex App Server.";
    public string LastCheckedText => Profile.Usage?.CheckedAt is { } checkedAt
        ? $"Updated {checkedAt.ToLocalTime().ToString("MMM d, HH:mm", CultureInfo.InvariantCulture)}"
        : "";

    public void RefreshBindings()
    {
        Notify(nameof(DisplayName));
        Notify(nameof(ColorHex));
        Notify(nameof(Initial));
        Notify(nameof(PlanText));
        Notify(nameof(MembershipEndsAt));
        Notify(nameof(AddedAtText));
        Notify(nameof(MembershipEndsText));
        Notify(nameof(MembershipExpired));
        Notify(nameof(MembershipRemainingText));
        Notify(nameof(MembershipBrush));
        Notify(nameof(MembershipSurface));
        Notify(nameof(ShortTermValue));
        Notify(nameof(WeeklyValue));
        Notify(nameof(ShortTermPercentText));
        Notify(nameof(WeeklyPercentText));
        Notify(nameof(UsageCaption));
        Notify(nameof(ShortTermText));
        Notify(nameof(WeeklyDetailText));
        Notify(nameof(HasShortTermReset));
        Notify(nameof(HasWeeklyReset));
        Notify(nameof(ShortTermResetText));
        Notify(nameof(WeeklyResetText));
        Notify(nameof(IsStale));
        Notify(nameof(HasCurrentUsage));
        Notify(nameof(IsAvailableNow));
        Notify(nameof(IsWaitingForReset));
        Notify(nameof(AvailableAt));
        Notify(nameof(AvailabilitySortGroup));
        Notify(nameof(AvailabilityCapacityScore));
        Notify(nameof(AvailabilityStatusText));
        Notify(nameof(AvailabilityClockText));
        Notify(nameof(AvailabilityClockCaption));
        Notify(nameof(AvailabilityExactText));
        Notify(nameof(PriorityText));
        Notify(nameof(ClockBrush));
        Notify(nameof(ClockSurface));
        Notify(nameof(StatusBackground));
        Notify(nameof(StatusForeground));
        Notify(nameof(RankBackground));
        Notify(nameof(CardBackground));
        Notify(nameof(CardBorderBrush));
        Notify(nameof(FreshnessText));
        Notify(nameof(UsageHint));
        Notify(nameof(LastCheckedText));
        Notify(nameof(SwitchLabel));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Notify([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private List<UsageWindow> KnownWindows()
    {
        var windows = new List<UsageWindow>(2);
        if (Profile.Usage?.ShortTerm is { } shortTerm) windows.Add(shortTerm);
        if (Profile.Usage?.Weekly is { } weekly) windows.Add(weekly);
        return windows;
    }

    private static string ResetLabel(DateTimeOffset? reset) => reset is null
        ? "Reset unavailable"
        : $"Resets {reset.Value.ToLocalTime().ToString("MMM d  •  HH:mm", CultureInfo.InvariantCulture)}";

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalDays >= 1)
            return $"{Math.Floor(duration.TotalDays):0}d {duration.Hours}h";
        if (duration.TotalHours >= 1)
            return $"{Math.Floor(duration.TotalHours):0}h {duration.Minutes}m";
        return $"{Math.Max(1, Math.Ceiling(duration.TotalMinutes)):0}m";
    }

    internal static string FormatMembershipRemaining(DateTimeOffset endsAt, DateTimeOffset now)
    {
        var remaining = endsAt - now;
        if (remaining <= TimeSpan.Zero)
        {
            var elapsed = -remaining;
            if (elapsed.TotalDays >= 1)
                return $"Expired {Math.Floor(elapsed.TotalDays):0} days ago";
            return "Expired today";
        }

        if (remaining.TotalDays >= 1)
            return $"{Math.Ceiling(remaining.TotalDays):0} days left";
        if (remaining.TotalHours >= 1)
            return $"{Math.Ceiling(remaining.TotalHours):0} hours left";
        return $"{Math.Max(1, Math.Ceiling(remaining.TotalMinutes)):0} min left";
    }
}
