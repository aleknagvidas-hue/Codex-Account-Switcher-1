using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using CodexAccountSwitcher.Models;

namespace CodexAccountSwitcher.ViewModels;

public sealed class AccountProfileViewModel : INotifyPropertyChanged
{
    private bool _isActive;
    private bool _isRefreshing;

    public AccountProfileViewModel(AccountProfile profile)
    {
        Profile = profile;
    }

    public AccountProfile Profile { get; }
    public int FreshForMinutes { get; set; } = 10;
    public string DisplayName => Profile.DisplayName;
    public string ColorHex => Profile.ColorHex;
    public string Initial => string.IsNullOrWhiteSpace(DisplayName) ? "?" : DisplayName.Trim()[0].ToString().ToUpperInvariant();
    public string PlanText => string.IsNullOrWhiteSpace(Profile.Usage?.PlanType)
        ? "Codex account"
        : $"Codex • {Profile.Usage.PlanType!.ToUpperInvariant()}";

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

    public bool CanSwitch => !IsActive;
    public string SwitchLabel => IsActive ? "Active" : "Switch";

    public double WeeklyValue => Profile.Usage?.Weekly?.RemainingPercent ?? 0;
    public string WeeklyPercentText => Profile.Usage?.Weekly is { } weekly
        ? $"{Math.Round(weekly.RemainingPercent):0}%"
        : "—";

    public string UsageCaption
    {
        get
        {
            if (IsRefreshing) return "Loading…";
            if (Profile.Usage?.Weekly?.ResetsAt is { } reset)
            {
                return $"Weekly • resets {reset.ToLocalTime().ToString("MMM d, HH:mm", CultureInfo.InvariantCulture)}";
            }

            if (Profile.Usage?.Status == "unavailable")
            {
                return "Weekly usage unavailable";
            }

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
        Notify(nameof(WeeklyValue));
        Notify(nameof(WeeklyPercentText));
        Notify(nameof(UsageCaption));
        Notify(nameof(ShortTermText));
        Notify(nameof(WeeklyDetailText));
        Notify(nameof(HasShortTermReset));
        Notify(nameof(HasWeeklyReset));
        Notify(nameof(ShortTermResetText));
        Notify(nameof(WeeklyResetText));
        Notify(nameof(IsStale));
        Notify(nameof(FreshnessText));
        Notify(nameof(UsageHint));
        Notify(nameof(LastCheckedText));
        Notify(nameof(SwitchLabel));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Notify([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private static string ResetLabel(DateTimeOffset? reset) => reset is null
        ? string.Empty
        : $"RESETS {reset.Value.ToLocalTime().ToString("MMM d  •  HH:mm", CultureInfo.InvariantCulture)}";
}
