using System.Globalization;
using System.Text;
using System.Text.Json;
using CodexAccountSwitcher.Models;
using CodexAccountSwitcher.Services;
using CodexAccountSwitcher.ViewModels;

if (args.Length == 1 && args[0] == "app-server" && Environment.GetEnvironmentVariable("CODEX_SWITCHER_TEST_SERVER_MODE") is { } fakeMode)
    return await RunFakeServer(fakeMode);

if (args.Length == 3 && args[0] == "--diagnose-saved-profiles")
{
    var store = new ProfileStore(args[1]);
    var client = new CodexAppServerClient(tempRoot: args[2]);
    foreach (var profile in await store.LoadAsync())
    {
        byte[]? auth = null;
        try
        {
            auth = await store.GetAuthAsync(profile);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            var result = await client.ReadUsageAsync(auth, timeout.Token);
            Console.WriteLine($"{profile.DisplayName}: authenticated, usage={result.Snapshot.Status}");
        }
        catch (CodexRequestException ex) { Console.WriteLine($"{profile.DisplayName}: {ex.Reason}"); }
        catch (Exception ex) { Console.WriteLine($"{profile.DisplayName}: diagnostic unavailable ({ex.GetType().Name})"); }
        finally { if (auth is not null) System.Security.Cryptography.CryptographicOperations.ZeroMemory(auth); }
    }
    return 0;
}

if (args.Length >= 2 && string.Equals(args[0], "--seed-demo", StringComparison.OrdinalIgnoreCase))
{
    var count = args.Length >= 3 && int.TryParse(args[2], out var requestedCount) ? requestedCount : 3;
    await SeedDemo(args[1], count);
    return 0;
}

var tests = new (string Name, Func<Task> Run)[]
{
    ("repeated switching preserves renewed departing credentials", RepeatedSwitchPreservesRenewal),
    ("target validation failure never closes the current desktop", PreflightFailureKeepsDesktop),
    ("launch failure restores prior account and permits retry", LaunchFailureAllowsRetry),
    ("rapid repeated operations are rejected instead of queued", DuplicateOperationsAreIgnored),
    ("authentication errors are classified without exposing server secrets", RequestErrorIsSanitized),
    ("preflight preserves rotated credentials even after server failure", PreflightRetainsRotatedCredentials),
    ("fingerprint stays stable across token refresh", FingerprintIsStable),
    ("encrypted profile auth round-trips through the credential protector", ProfileRoundTrip),
    ("account browser profile is stored with its saved sign-in", BrowserProfileIsStored),
    ("browser profile rejects path traversal", BrowserProfileRejectsTraversal),
    ("duplicate account is rejected", DuplicateRejected),
    ("invalid and API-key auth is rejected", InvalidAuthIsRejected),
    ("startup command safely quotes the executable path", AutostartCommandIsQuoted),
    ("current sign-in renews a full panel without changing labels", CurrentSignInRenewsFullPanel),
    ("saved account collection has no artificial limit", ManyAccountsAreAccepted),
    ("unknown usage duration is not labeled five hours", UnknownUsageWindowIsUnavailable),
    ("recovery preserves a later auth writer", RecoveryPreservesLaterWriter),
    ("failed backup recovery never relaunches Codex", FailedRecoveryDoesNotRelaunch),
    ("official rate-limit response is parsed", UsageResponseIsParsed),
    ("short-only limit is not mislabeled weekly", ShortOnlyUsageIsNotWeekly),
    ("usage dates remain English across OS locales", UsageDatesStayEnglish),
    ("isolated switch preserves departing auth and backup", SwitchIsAtomicAndPreservesState),
    ("Codex file credential store is placed at TOML root", CredentialStoreSettingIsRooted),
    ("failed account verification restores the previous sign-in", VerificationFailureRestoresPreviousSignIn),
    ("failed replacement restores only the current transaction", ReplacementFailureRestoresCurrentTransaction),
    ("cancellation after replacement restores the previous sign-in", ReplacementCancellationRestoresPreviousSignIn),
    ("read-only monitoring excludes refresh tokens", ReadOnlyMonitoringExcludesRefreshTokens),
    ("refresh coordinator serializes credential operations", RefreshCoordinatorSerializesOperations),
    ("refresh schedule backs off and recovers", RefreshScheduleBacksOffAndRecovers),
    ("failed refresh preserves the last reading and marks it stale", FailedRefreshPreservesLastReading),
    ("shutdown collects late Codex arrivals before confirming exit", ShutdownCollectsLateArrivals),
    ("shutdown force-kills an unresponsive Codex tree", ShutdownForceKillsUnresponsiveCodexTree),
    ("shutdown stops the detached desktop App Server before switching", ShutdownStopsDetachedAppServer),
    ("shutdown ignores a process that exits during path inspection", ShutdownIgnoresExitedProbeRace),
    ("shutdown rejects a persistent ambiguous ChatGPT process", ShutdownRejectsPersistentAmbiguity),
    ("shutdown bounds a continuously respawning Codex", ShutdownBoundsContinuousRespawn),
    ("temporary path boundary rejects siblings", PathBoundaryWorks),
    ("locked temporary Codex files do not cancel a completed login", LockedTemporaryCleanupIsNonFatal),
    ("Codex GUI path matcher excludes unrelated ChatGPT", ProcessPathMatcherWorks),
    ("account and confirmation dialogs load their XAML", DialogsLoad)
};

if (args.Contains("--integration", StringComparer.OrdinalIgnoreCase))
{
    tests = tests
        .Append<(string Name, Func<Task> Run)>(
            ("installed Codex App Server initializes in isolated home", AppServerProbe))
        .Append<(string Name, Func<Task> Run)>(
            ("installed Codex App Server accepts isolated read-only token mode", ExternalTokenModeProbe))
        .Append<(string Name, Func<Task> Run)>(
            ("running Codex desktop process is identifiable", RunningDesktopIsIdentifiable))
        .ToArray();
}

var failures = new List<string>();
foreach (var test in tests)
{
    try
    {
        await test.Run();
        Console.WriteLine($"PASS  {test.Name}");
    }
    catch (Exception ex)
    {
        failures.Add($"{test.Name}: {ex.Message}");
        Console.WriteLine($"FAIL  {test.Name}: {ex.Message}");
    }
}

Console.WriteLine($"\n{tests.Length - failures.Count}/{tests.Length} tests passed");
return failures.Count == 0 ? 0 : 1;

static Task FingerprintIsStable()
{
    var first = Auth("acct_same", "access-one", "refresh-one");
    var second = Auth("acct_same", "access-two", "refresh-two");
    Equal(AuthIdentity.ValidateAndFingerprint(first), AuthIdentity.ValidateAndFingerprint(second), "fingerprint");
    NotEqual(
        AuthIdentity.ValidateAndFingerprint(first),
        AuthIdentity.ValidateAndFingerprint(Auth("acct_other", "access-one", "refresh-one")),
        "different account fingerprint");
    return Task.CompletedTask;
}

static async Task ProfileRoundTrip()
{
    await WithTempDirectory(async root =>
    {
        var store = TestStore(root);
        var source = Auth("acct_roundtrip", "access-roundtrip", "refresh-roundtrip");
        var profile = await store.AddAsync("Private label", "#46C7B4", source);
        var restored = await store.GetAuthAsync(profile);
        Equal(Encoding.UTF8.GetString(source), Encoding.UTF8.GetString(restored), "decrypted auth");

        var metadata = await File.ReadAllTextAsync(Path.Combine(root, "profiles.json"));
        True(!metadata.Contains("access-roundtrip", StringComparison.Ordinal), "metadata must not contain access token");
        True(!metadata.Contains("refresh-roundtrip", StringComparison.Ordinal), "metadata must not contain refresh token");
        using var metadataDocument = JsonDocument.Parse(metadata);
        Equal(
            "Private label",
            metadataDocument.RootElement[0].GetProperty("displayName").GetString(),
            "metadata alias");
    });
}

static async Task DuplicateRejected()
{
    await WithTempDirectory(async root =>
    {
        var store = TestStore(root);
        await store.AddAsync("one", "#7C8CFF", Auth("acct_duplicate", "token-1", "refresh-1"));
        await Throws<InvalidOperationException>(() =>
            store.AddAsync("two", "#E881A6", Auth("acct_duplicate", "token-2", "refresh-2")));
    });
}

static Task UsageResponseIsParsed()
{
    const string json = """
    {
      "rateLimitsByLimitId": {
        "codex": {
          "limitId": "codex",
          "planType": "pro",
          "primary": { "usedPercent": 22, "windowDurationMins": 300, "resetsAt": 1787100000 },
          "secondary": { "usedPercent": 37, "windowDurationMins": 10080, "resetsAt": 1787500000 }
        }
      }
    }
    """;
    using var document = JsonDocument.Parse(json);
    var snapshot = CodexAppServerClient.ParseRateLimits(
        document.RootElement,
        null,
        DateTimeOffset.Parse("2026-08-19T00:00:00Z"));

    Equal("available", snapshot.Status, "usage status");
    Equal("pro", snapshot.PlanType, "plan type");
    Equal(78d, snapshot.ShortTerm?.RemainingPercent, "short remaining");
    Equal(63d, snapshot.Weekly?.RemainingPercent, "weekly remaining");
    Equal(10080, snapshot.Weekly?.WindowDurationMinutes, "weekly duration");
    True(snapshot.Weekly?.ResetsAt is not null, "weekly reset time");
    return Task.CompletedTask;
}

static Task ShortOnlyUsageIsNotWeekly()
{
    const string json = """
    {
      "rateLimits": {
        "limitId": "codex",
        "primary": { "usedPercent": 40, "windowDurationMins": 300, "resetsAt": 1787100000 },
        "secondary": null
      }
    }
    """;
    using var document = JsonDocument.Parse(json);
    var snapshot = CodexAppServerClient.ParseRateLimits(document.RootElement, null, DateTimeOffset.UtcNow);
    True(snapshot.Weekly is null, "short limit must not become weekly");
    Equal(60d, snapshot.ShortTerm?.RemainingPercent, "short-only remaining");
    return Task.CompletedTask;
}

static Task UsageDatesStayEnglish()
{
    var originalCulture = CultureInfo.CurrentCulture;
    var originalUiCulture = CultureInfo.CurrentUICulture;
    try
    {
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ja-JP");
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("ja-JP");
        var timestamp = new DateTimeOffset(2026, 8, 19, 12, 34, 0, TimeSpan.Zero);
        var viewModel = new AccountProfileViewModel(new AccountProfile
        {
            Usage = new UsageSnapshot
            {
                CheckedAt = timestamp,
                Weekly = new UsageWindow { ResetsAt = timestamp }
            }
        });

        True(viewModel.UsageCaption.Contains("Aug", StringComparison.Ordinal), "weekly reset month remains English");
        True(viewModel.LastCheckedText.Contains("Aug", StringComparison.Ordinal), "updated month remains English");
        True(!viewModel.UsageCaption.Contains("\u6708", StringComparison.Ordinal), "weekly reset excludes localized month text");
        return Task.CompletedTask;
    }
    finally
    {
        CultureInfo.CurrentCulture = originalCulture;
        CultureInfo.CurrentUICulture = originalUiCulture;
    }
}

static async Task<int> RunFakeServer(string mode)
{
    while (await Console.In.ReadLineAsync() is { } line)
    {
        using var doc = JsonDocument.Parse(line);
        var request = doc.RootElement;
        if (!request.TryGetProperty("id", out var id)) continue;
        var method = request.GetProperty("method").GetString();
        object result = new { };
        if (method == "account/read")
        {
            if (request.GetProperty("params").GetProperty("refreshToken").GetBoolean())
                throw new InvalidOperationException("Preflight must not force token refresh.");
            result = new { account = new { type = "chatgpt" } };
        }
        if (method == "account/rateLimits/read")
        {
            var path = Path.Combine(Environment.GetEnvironmentVariable("CODEX_HOME")!, "auth.json");
            var auth = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(path))!;
            auth["tokens"]!["refresh_token"] = "rotated-by-fake-server";
            await File.WriteAllTextAsync(path, auth.ToJsonString());
            if (mode == "fail-after-renewal")
            {
                Console.WriteLine(JsonSerializer.Serialize(new { id = id.GetInt32(), error = new { code = -32000, message = "error sending request" } }));
                continue;
            }
            result = new { rateLimits = new { } };
        }
        Console.WriteLine(JsonSerializer.Serialize(new { id = id.GetInt32(), result }));
    }
    return 0;
}

static async Task PreflightRetainsRotatedCredentials()
{
    var oldMode = Environment.GetEnvironmentVariable("CODEX_SWITCHER_TEST_SERVER_MODE");
    try
    {
        foreach (var mode in new[] { "success", "fail-after-renewal" })
        {
            Environment.SetEnvironmentVariable("CODEX_SWITCHER_TEST_SERVER_MODE", mode);
            await WithTempDirectory(async root =>
            {
                var executable = Path.Combine(AppContext.BaseDirectory, "CodexAccountSwitcher.Tests.exe");
                var client = new CodexAppServerClient(executable, root);
                byte[]? persisted = null;
                Task Persist(byte[] bytes) { persisted = bytes.ToArray(); return Task.CompletedTask; }
                var auth = Auth("fixture", "access", "original-refresh");
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                if (mode == "success")
                {
                    var prepared = await client.PrepareManagedAuthAsync(auth, Persist, timeout.Token);
                    True(Encoding.UTF8.GetString(prepared).Contains("rotated-by-fake-server"), "successful preflight returns the new token");
                }
                else await Throws<CodexRequestException>(() => client.PrepareManagedAuthAsync(auth, Persist, timeout.Token));
                True(persisted is not null && Encoding.UTF8.GetString(persisted).Contains("rotated-by-fake-server"), "rotated token survives success or failure");
            });
        }
    }
    finally { Environment.SetEnvironmentVariable("CODEX_SWITCHER_TEST_SERVER_MODE", oldMode); }
}

static async Task RepeatedSwitchPreservesRenewal()
{
    await WithTempDirectory(async root =>
    {
        var protector = new TestCredentialProtector();
        var store = new ProfileStore(Path.Combine(root, "store"), protector);
        var path = Path.Combine(root, "auth.json");
        var a = await store.AddAsync("A", "#7C8CFF", Auth("a", "a-initial", "r1"));
        var b = await store.AddAsync("B", "#7C8CFF", Auth("b", "b-initial", "r2"));
        await File.WriteAllBytesAsync(path, Auth("a", "a-rotated", "r3"));
        var process = new FakeProcessService();
        var lifecycle = new FakeDesktopAuthLifecycle();
        var service = new AuthSwitchService(store, process, path, protector, authLifecycle: lifecycle);
        await service.SwitchAsync(b);
        await File.WriteAllBytesAsync(path, Auth("b", "b-rotated", "r4"));
        await service.SwitchAsync(a);
        True(Encoding.UTF8.GetString(await File.ReadAllBytesAsync(path)).Contains("a-rotated"), "A retains the latest desktop renewal");
        await service.SwitchAsync(b);
        True(Encoding.UTF8.GetString(await File.ReadAllBytesAsync(path)).Contains("b-rotated"), "B retains the latest desktop renewal");
        Equal(3, process.LaunchCallCount, "each completed switch launches once");
    });
}

static async Task PreflightFailureKeepsDesktop()
{
    await WithTempDirectory(async root =>
    {
        var protector = new TestCredentialProtector();
        var store = new ProfileStore(Path.Combine(root, "store"), protector);
        var path = Path.Combine(root, "auth.json");
        var original = Auth("a", "a", "r1");
        await File.WriteAllBytesAsync(path, original);
        var target = await store.AddAsync("B", "#7C8CFF", Auth("b", "b", "r2"));
        var process = new FakeProcessService();
        var lifecycle = new FakeDesktopAuthLifecycle { PreparationError = CodexRequestException.FromServerMessage("refresh_token_reused") };
        var service = new AuthSwitchService(store, process, path, protector, authLifecycle: lifecycle);
        await Throws<CodexRequestException>(() => service.SwitchAsync(target));
        Equal(0, process.CloseCallCount, "invalid target must not close Codex");
        Equal(0, process.LaunchCallCount, "invalid target must not restart Codex");
        var retained = await File.ReadAllBytesAsync(path);
        True(original.AsSpan().SequenceEqual(retained), "active auth is untouched");
        lifecycle.PreparationError = null;
        await service.SwitchAsync(target);
        Equal(target.Fingerprint, AuthIdentity.ValidateAndFingerprint(await File.ReadAllBytesAsync(path)), "retry succeeds after renewal");
    });
}

static async Task LaunchFailureAllowsRetry()
{
    await WithTempDirectory(async root =>
    {
        var protector = new TestCredentialProtector();
        var store = new ProfileStore(Path.Combine(root, "store"), protector);
        var path = Path.Combine(root, "auth.json");
        var original = Auth("a", "a", "r1");
        await File.WriteAllBytesAsync(path, original);
        var target = await store.AddAsync("B", "#7C8CFF", Auth("b", "b", "r2"));
        var process = new FakeProcessService { FailNextLaunch = true };
        var service = new AuthSwitchService(store, process, path, protector, authLifecycle: new FakeDesktopAuthLifecycle());
        await Throws<InvalidOperationException>(() => service.SwitchAsync(target));
        Equal(AuthIdentity.ValidateAndFingerprint(original), AuthIdentity.ValidateAndFingerprint(await File.ReadAllBytesAsync(path)), "launch failure restores A");
        Equal(2, process.LaunchCallCount, "recovery relaunch is attempted");
        var result = await service.SwitchAsync(target);
        True(result.Relaunched, "another switch is possible after failure");
    });
}

static async Task DuplicateOperationsAreIgnored()
{
    var coordinator = new OperationCoordinator();
    var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var calls = 0;
    var first = coordinator.TryRunAsync(async _ => { calls++; entered.SetResult(); await release.Task; });
    await entered.Task;
    var second = await coordinator.TryRunAsync(_ => { calls++; return Task.CompletedTask; });
    True(!second, "second click is discarded");
    release.SetResult();
    await first;
    Equal(1, calls, "discarded click never runs later");
    True(await coordinator.TryRunAsync(_ => Task.CompletedTask), "gate opens after completion");
}

static Task RequestErrorIsSanitized()
{
    var auth = CodexRequestException.FromServerMessage("refresh_token_reused synthetic-secret@example.test token=secret");
    True(auth.RequiresLogin, "revoked token requires login");
    True(!auth.Message.Contains("secret"), "raw error is never echoed");
    True(!CodexRequestException.FromServerMessage("error sending request token=secret").RequiresLogin, "network failure does not trigger browser login");
    return Task.CompletedTask;
}

static async Task SwitchIsAtomicAndPreservesState()
{
    await WithTempDirectory(async root =>
    {
        var storeRoot = Path.Combine(root, "store");
        var codexHome = Path.Combine(root, "codex-home");
        Directory.CreateDirectory(codexHome);
        var activePath = Path.Combine(codexHome, "auth.json");

        var protector = new TestCredentialProtector();
        var store = new ProfileStore(storeRoot, protector);
        var oldActive = Auth("acct_active", "old-access", "old-refresh");
        var refreshedActive = Auth("acct_active", "new-access", "new-refresh");
        var targetAuth = Auth("acct_target", "target-access", "target-refresh");
        var activeProfile = await store.AddAsync("active", "#7C8CFF", oldActive);
        var targetProfile = await store.AddAsync("target", "#46C7B4", targetAuth);
        await File.WriteAllBytesAsync(activePath, refreshedActive);

        var fakeProcess = new FakeProcessService();
        var lifecycle = new FakeDesktopAuthLifecycle();
        var switcher = new AuthSwitchService(store, fakeProcess, activePath, protector, authLifecycle: lifecycle);
        var result = await switcher.SwitchAsync(targetProfile);

        True(result.Relaunched, "relaunch result");
        Equal(2, fakeProcess.CloseCallCount, "desktop close rechecked before auth replacement");
        True(fakeProcess.Launched, "desktop launch called");
        True(!lifecycle.LogoutCalled, "switch does not call account-wide logout");
        True(lifecycle.VerifyCalled, "selected sign-in is verified before desktop relaunch");
        Equal(
            targetProfile.Fingerprint,
            AuthIdentity.ValidateAndFingerprint(await File.ReadAllBytesAsync(activePath)),
            "active auth after switch");

        var backup = Directory.GetFiles(Path.Combine(codexHome, "switcher-backups"), "*.dpapi").Single();
        True(File.Exists(backup), "backup exists");
        var backupAuth = protector.Unprotect(await File.ReadAllBytesAsync(backup));
        Equal(
            activeProfile.Fingerprint,
            AuthIdentity.ValidateAndFingerprint(backupAuth),
            "backup identity");

        var storedDeparting = Encoding.UTF8.GetString(await store.GetAuthAsync(activeProfile));
        True(storedDeparting.Contains("new-access", StringComparison.Ordinal), "departing refreshed auth saved");
    });
}

static async Task ShutdownCollectsLateArrivals()
{
    var initial = FakeCodexProcessHandle.Codex(1, hasMainWindow: true, exitOnClose: true);
    var lateArrival = FakeCodexProcessHandle.Codex(2, hasMainWindow: true, exitOnClose: true);
    var runtime = new FakeCodexProcessRuntime(
        new ICodexProcessHandle[] { initial },
        new ICodexProcessHandle[] { lateArrival },
        Array.Empty<ICodexProcessHandle>(),
        Array.Empty<ICodexProcessHandle>(),
        Array.Empty<ICodexProcessHandle>());

    await TestProcessService(runtime).CloseDesktopAsync();

    True(initial.CloseRequested, "main window receives graceful close");
    True(lateArrival.CloseRequested, "late Codex process receives only a graceful close");
    True(!lateArrival.KillRequested, "late Codex process is not force-killed");
    True(runtime.ScanCount >= 5, "exit requires stable consecutive empty scans");
}

static async Task ShutdownIgnoresExitedProbeRace()
{
    var initial = FakeCodexProcessHandle.Codex(1, hasMainWindow: true, exitOnClose: true);
    var exitedDuringProbe = FakeCodexProcessHandle.Ambiguous(2, hasExited: true);
    var runtime = new FakeCodexProcessRuntime(
        new ICodexProcessHandle[] { initial },
        new ICodexProcessHandle[] { exitedDuringProbe },
        Array.Empty<ICodexProcessHandle>(),
        Array.Empty<ICodexProcessHandle>());

    await TestProcessService(runtime).CloseDesktopAsync();

    True(exitedDuringProbe.Disposed, "exited probe-race process is discarded");
}

static async Task ShutdownForceKillsUnresponsiveCodexTree()
{
    var initial = FakeCodexProcessHandle.Codex(
        1,
        hasMainWindow: true,
        exitOnClose: false,
        exitOnKill: true);
    var runtime = new FakeCodexProcessRuntime(
        new ICodexProcessHandle[] { initial },
        Array.Empty<ICodexProcessHandle>(),
        Array.Empty<ICodexProcessHandle>(),
        Array.Empty<ICodexProcessHandle>());

    await TestProcessService(runtime).CloseDesktopAsync();

    True(initial.CloseRequested, "unresponsive main window first receives graceful close");
    True(initial.KillRequested, "unresponsive process tree is force-killed after graceful timeout");
}

static async Task ShutdownStopsDetachedAppServer()
{
    var gui = FakeCodexProcessHandle.Codex(1, hasMainWindow: true, exitOnClose: true);
    var daemon = FakeCodexProcessHandle.AppServer(2, exitOnKill: true);
    var runtime = new FakeCodexProcessRuntime(
        new ICodexProcessHandle[] { gui, daemon },
        Array.Empty<ICodexProcessHandle>(),
        Array.Empty<ICodexProcessHandle>(),
        Array.Empty<ICodexProcessHandle>());

    await TestProcessService(runtime).CloseDesktopAsync();

    True(gui.CloseRequested, "desktop window receives graceful close");
    True(daemon.KillRequested, "detached desktop App Server is stopped before auth replacement");
}

static async Task BrowserProfileIsStored()
{
    await WithTempDirectory(async root =>
    {
        var store = new ProfileStore(root, new TestCredentialProtector());
        var key = Guid.NewGuid().ToString("N");
        var added = await store.AddAsync("Private account", "#7C8CFF", Auth("browser-profile", "x", "y"), key);
        var loaded = (await store.LoadAsync()).Single();
        Equal(key, added.BrowserProfileKey, "browser profile on added account");
        Equal(key, loaded.BrowserProfileKey, "browser profile in metadata");
    });
}

static async Task BrowserProfileRejectsTraversal()
{
    await Throws<ArgumentException>(() =>
    {
        BrowserProfileService.ValidateProfileKey("..\\outside");
        return Task.CompletedTask;
    });
}

static Task CredentialStoreSettingIsRooted()
{
    var original = "[windows]\r\nfoo = true\r\n";
    var updated = CodexCredentialStoreService.ForceFileStore(original);
    True(updated.StartsWith("cli_auth_credentials_store = \"file\"", StringComparison.Ordinal), "credential setting is at TOML root");
    Equal(1, updated.Split("cli_auth_credentials_store", StringSplitOptions.None).Length - 1, "credential setting is unique");

    var replaced = CodexCredentialStoreService.ForceFileStore("cli_auth_credentials_store = \"keyring\"\n[windows]\n");
    True(replaced.StartsWith("cli_auth_credentials_store = \"file\"", StringComparison.Ordinal), "existing credential setting is replaced");
    return Task.CompletedTask;
}

static async Task VerificationFailureRestoresPreviousSignIn()
{
    await WithTempDirectory(async root =>
    {
        var active = Path.Combine(root, "auth.json");
        var original = Auth("verification-original", "old", "old-refresh");
        await File.WriteAllBytesAsync(active, original);
        var protector = new TestCredentialProtector();
        var store = new ProfileStore(Path.Combine(root, "store"), protector);
        await store.AddAsync("Original", "#7C8CFF", original);
        var target = await store.AddAsync("Target", "#46C7B4", Auth("verification-target", "new", "new-refresh"));
        var processes = new FakeProcessService();
        var lifecycle = new FakeDesktopAuthLifecycle
        {
            VerificationError = new InvalidOperationException("synthetic verification failure")
        };

        await Throws<InvalidOperationException>(() =>
            new AuthSwitchService(store, processes, active, protector, authLifecycle: lifecycle).SwitchAsync(target));

        Equal(AuthIdentity.ValidateAndFingerprint(original),
            AuthIdentity.ValidateAndFingerprint(await File.ReadAllBytesAsync(active)), "previous sign-in is restored");
        True(processes.Launched, "desktop reopens with the restored sign-in");
        True(lifecycle.VerifyCalled, "failed verification was attempted");
    });
}

static async Task InvalidAuthIsRejected()
{
    foreach (var json in new[] { "{}", "[]", "{\"OPENAI_API_KEY\":\"synthetic\"}",
        "{\"tokens\":{\"account_id\":\"a\"}}",
        "{\"tokens\":{\"access_token\":\"opaque\"}}",
        "{\"auth_mode\":17,\"tokens\":{\"account_id\":\"a\",\"access_token\":\"b\"}}" })
        await Throws<InvalidDataException>(() =>
        {
            AuthIdentity.ValidateAndFingerprint(Encoding.UTF8.GetBytes(json));
            return Task.CompletedTask;
        });
}

static Task AutostartCommandIsQuoted()
{
    var command = AutostartService.BuildCommand(@"C:\Users\Test User\Codex Account Switcher\CodexAccountSwitcher.exe");
    True(command.StartsWith("\"C:\\Users\\Test User\\Codex Account Switcher\\CodexAccountSwitcher.exe\"", StringComparison.Ordinal), "startup path quoted");
    True(command.EndsWith(" --background", StringComparison.Ordinal), "startup is hidden");
    return Task.CompletedTask;
}

static async Task CurrentSignInRenewsFullPanel()
{
    await WithTempDirectory(async root =>
    {
        var store = TestStore(root);
        var original = await store.AddAsync("Original label", "#46C7B4", Auth("renew-0", "old", "old-refresh"));
        for (var i = 1; i < 4; i++) await store.AddAsync($"Account {i}", "#7C8CFF", Auth($"renew-{i}", "x", "y"));
        var fresh = Auth("renew-0", "new", "new-refresh");
        var saved = await store.SaveCurrentAsync("Should not replace label", "#FFFFFF", fresh);
        Equal(original.Id, saved.Id, "profile ID retained");
        Equal(original.DisplayName, saved.DisplayName, "label retained");
        Equal(original.ColorHex, saved.ColorHex, "color retained");
        Equal(4, (await store.LoadAsync()).Count, "still exactly four profiles");
        var stored = await store.GetAuthAsync(saved);
        True(fresh.AsSpan().SequenceEqual(stored), "renewed credentials persisted");
    });
}

static async Task ManyAccountsAreAccepted()
{
    await WithTempDirectory(async root =>
    {
        var store = TestStore(root);
        const int accountCount = 25;
        for (var i = 0; i < accountCount; i++)
            await store.AddAsync($"Account {i + 1}", "#7C8CFF", Auth($"unlimited-{i}", "x", "y"));
        Equal(accountCount, (await store.LoadAsync()).Count, "all distinct accounts are retained");
        await Throws<InvalidOperationException>(() =>
            store.AddAsync("Duplicate", "#7C8CFF", Auth("unlimited-0", "x", "y")));
        Equal(accountCount, (await store.LoadAsync()).Count, "duplicate protection remains active");
    });
}

static Task UnknownUsageWindowIsUnavailable()
{
    using var document = JsonDocument.Parse("""
        {"rateLimits":{"limitId":"codex","primary":{"usedPercent":40,"windowDurationMins":60}}}
        """);
    var snapshot = CodexAppServerClient.ParseRateLimits(document.RootElement, null, DateTimeOffset.UtcNow);
    True(snapshot.ShortTerm is null && snapshot.Weekly is null, "unknown window is not relabeled");
    True(snapshot.Status != "available", "unknown window is unavailable");
    return Task.CompletedTask;
}

static async Task RecoveryPreservesLaterWriter()
{
    foreach (var previousExists in new[] { true, false })
    await WithTempDirectory(async root =>
    {
        var path = Path.Combine(root, "auth.json");
        if (previousExists) await File.WriteAllBytesAsync(path, Auth("previous", "x", "y"));
        var later = Auth("later-writer", "x", "y");
        var transaction = new AuthFileTransaction(stage =>
        {
            if (stage == AuthWriteStage.Replaced)
            {
                File.WriteAllBytes(path, later);
                throw new IOException("synthetic post-replacement failure");
            }
        }, new TestCredentialProtector());
        await Throws<AuthRecoveryException>(() => transaction.ReplaceAsync(path, Auth("target", "x", "y"), CancellationToken.None));
        var stored = await File.ReadAllBytesAsync(path);
        True(later.AsSpan().SequenceEqual(stored), "later writer is preserved");
    });
}

static async Task FailedRecoveryDoesNotRelaunch()
{
    await WithTempDirectory(async root =>
    {
        var store = TestStore(Path.Combine(root, "store"));
        var target = await store.AddAsync("Target", "#7C8CFF", Auth("target", "x", "y"));
        var active = Path.Combine(root, "auth.json");
        await File.WriteAllBytesAsync(active, Auth("previous", "x", "y"));
        var protector = new FailsDuringRecoveryProtector();
        var transaction = new AuthFileTransaction(stage =>
        {
            if (stage == AuthWriteStage.Replaced) throw new IOException("synthetic switch failure");
        }, protector);
        var processes = new FakeProcessService();
        await Throws<AuthRecoveryException>(() => new AuthSwitchService(
            store, processes, active, protector, transaction, new FakeDesktopAuthLifecycle()).SwitchAsync(target));
        True(!processes.Launched, "Codex stays closed when recovery cannot be verified");
        Equal(1, Directory.GetFiles(Path.Combine(root, "switcher-backups"), "*.dpapi").Length, "backup retained");
    });
}

static async Task ReplacementFailureRestoresCurrentTransaction()
{
    await WithTempDirectory(async root =>
    {
        var active = Path.Combine(root, "auth.json");
        var original = Auth("acct_original", "access-old", "refresh-old");
        var target = Auth("acct_target", "access-new", "refresh-new");
        await File.WriteAllBytesAsync(active, original);
        var oldBackupDirectory = Path.Combine(root, "switcher-backups");
        Directory.CreateDirectory(oldBackupDirectory);
        var protector = new TestCredentialProtector();
        await File.WriteAllBytesAsync(Path.Combine(oldBackupDirectory, "older.dpapi"), protector.Protect(Auth("acct_unrelated", "x", "y")));

        var transaction = new AuthFileTransaction(stage =>
        {
            if (stage == AuthWriteStage.Replaced) throw new IOException("synthetic post-replacement failure");
        }, protector);
        await Throws<IOException>(() => transaction.ReplaceAsync(active, target, CancellationToken.None));
        Equal(AuthIdentity.ValidateAndFingerprint(original), AuthIdentity.ValidateAndFingerprint(await File.ReadAllBytesAsync(active)), "current transaction restores original");
    });
}

static async Task ReplacementCancellationRestoresPreviousSignIn()
{
    await WithTempDirectory(async root =>
    {
        var active = Path.Combine(root, "auth.json");
        var original = Auth("acct_original_cancel", "access-old", "refresh-old");
        var target = Auth("acct_target_cancel", "access-new", "refresh-new");
        await File.WriteAllBytesAsync(active, original);
        using var cancellation = new CancellationTokenSource();
        var transaction = new AuthFileTransaction(stage =>
        {
            if (stage == AuthWriteStage.Replaced) cancellation.Cancel();
        }, new TestCredentialProtector());
        await Throws<OperationCanceledException>(() => transaction.ReplaceAsync(active, target, cancellation.Token));
        Equal(AuthIdentity.ValidateAndFingerprint(original), AuthIdentity.ValidateAndFingerprint(await File.ReadAllBytesAsync(active)), "cancellation restores original");
    });
}

static Task ReadOnlyMonitoringExcludesRefreshTokens()
{
    var parameters = CodexAppServerClient.CreateReadOnlyLoginParameters(Auth("acct_read_only", "access-visible", "refresh-must-not-leave"));
    var json = JsonSerializer.Serialize(parameters);
    True(json.Contains("chatgptAuthTokens", StringComparison.Ordinal), "external token mode");
    True(json.Contains("access-visible", StringComparison.Ordinal), "access token provided");
    True(!json.Contains("refresh-must-not-leave", StringComparison.Ordinal), "refresh token excluded");
    return Task.CompletedTask;
}

static async Task RefreshCoordinatorSerializesOperations()
{
    var coordinator = new OperationCoordinator();
    var running = 0;
    var maximum = 0;
    async Task Action(CancellationToken token)
    {
        var current = Interlocked.Increment(ref running);
        maximum = Math.Max(maximum, current);
        await Task.Delay(20, token);
        Interlocked.Decrement(ref running);
    }
    await Task.WhenAll(coordinator.RunAsync(Action), coordinator.RunAsync(Action), coordinator.RunAsync(Action));
    Equal(1, maximum, "only one credential operation runs at a time");
}

static Task RefreshScheduleBacksOffAndRecovers()
{
    var now = DateTimeOffset.Parse("2026-08-30T12:00:00Z");
    var schedule = new RefreshSchedule();
    schedule.Complete(false, 5, now);
    Equal(now.AddMinutes(10), schedule.NextAttempt, "first failure doubles interval");
    schedule.Complete(false, 5, now);
    Equal(now.AddMinutes(20), schedule.NextAttempt, "second failure backs off further");
    schedule.Complete(true, 5, now);
    Equal(0, schedule.Failures, "success resets failures");
    Equal(now.AddMinutes(5), schedule.NextAttempt, "success restores normal interval");
    return Task.CompletedTask;
}

static Task FailedRefreshPreservesLastReading()
{
    var checkedAt = DateTimeOffset.Parse("2026-08-30T12:00:00Z");
    var profile = new AccountProfile
    {
        Usage = new UsageSnapshot
        {
            Status = "available",
            CheckedAt = checkedAt,
            ShortTerm = new UsageWindow { RemainingPercent = 73, WindowDurationMinutes = 300 },
            Weekly = new UsageWindow { RemainingPercent = 41, WindowDurationMinutes = 10080 }
        }
    };
    var viewModel = new AccountProfileViewModel(profile);

    PanelController.MarkUnavailable(viewModel, "Synthetic refresh failure.");

    Equal("stale", profile.Usage.Status, "failed refresh status");
    Equal(checkedAt, profile.Usage.CheckedAt, "last successful timestamp");
    Equal(73d, profile.Usage.ShortTerm?.RemainingPercent, "last five-hour reading");
    Equal(41d, profile.Usage.Weekly?.RemainingPercent, "last weekly reading");
    Equal("Synthetic refresh failure.", profile.Usage.Message, "failure message");
    return Task.CompletedTask;
}

static async Task ShutdownRejectsPersistentAmbiguity()
{
    var initial = FakeCodexProcessHandle.Codex(1, hasMainWindow: true, exitOnClose: true);
    var scans = new List<ICodexProcessHandle[]> { new ICodexProcessHandle[] { initial } };
    for (var i = 0; i < 10; i++)
    {
        scans.Add(new ICodexProcessHandle[] { FakeCodexProcessHandle.Ambiguous(100 + i, hasExited: false) });
    }

    var runtime = new FakeCodexProcessRuntime(scans.ToArray());
    await Throws<InvalidOperationException>(() => TestProcessService(runtime).CloseDesktopAsync());
}

static async Task ShutdownBoundsContinuousRespawn()
{
    var initial = FakeCodexProcessHandle.Codex(1, hasMainWindow: true, exitOnClose: true);
    var scans = new List<ICodexProcessHandle[]> { new ICodexProcessHandle[] { initial } };
    for (var i = 0; i < 10; i++)
    {
        scans.Add(new ICodexProcessHandle[] { FakeCodexProcessHandle.Codex(200 + i, hasMainWindow: true, exitOnKill: true) });
    }

    var runtime = new FakeCodexProcessRuntime(scans.ToArray());
    await Throws<InvalidOperationException>(() => TestProcessService(runtime).CloseDesktopAsync());
    True(runtime.ScanCount < 10, "respawn handling stops at the bounded timeout");
}

static CodexProcessService TestProcessService(FakeCodexProcessRuntime runtime) =>
    new(
        runtime,
        gracefulCloseTimeout: TimeSpan.FromSeconds(1),
        forcedCloseTimeout: TimeSpan.FromSeconds(1),
        quiescenceTimeout: TimeSpan.FromMilliseconds(500),
        pollInterval: TimeSpan.FromMilliseconds(100));

static Task PathBoundaryWorks()
{
    var root = Path.Combine(Path.GetTempPath(), "switcher-root");
    True(AppPaths.IsPathInside(Path.Combine(root, "child"), root), "child path accepted");
    True(!AppPaths.IsPathInside(root + "-sibling", root), "sibling path rejected");
    return Task.CompletedTask;
}

static Task ProcessPathMatcherWorks()
{
    True(
        AppPaths.IsPackagedCodexGui(@"C:\Program Files\WindowsApps\OpenAI.Codex_26.814.5167.0_x64__2p2nqsd0c76g0\app\ChatGPT.exe"),
        "Codex GUI path");
    True(
        !AppPaths.IsPackagedCodexGui(@"C:\Program Files\WindowsApps\OpenAI.ChatGPT_1.0_x64__abc\app\ChatGPT.exe"),
        "unrelated ChatGPT path");
    var localCodex = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenAI", "Codex", "bin", "version", "codex.exe");
    True(AppPaths.IsLocalCodexExecutable(localCodex), "local Codex runtime path");
    True(!AppPaths.IsLocalCodexExecutable(Path.Combine(Path.GetTempPath(), "codex.exe")), "untrusted local codex path");
    return Task.CompletedTask;
}

static Task DialogsLoad()
{
    Exception? captured = null;
    var thread = new Thread(() =>
    {
        try
        {
            var app = new CodexAccountSwitcher.App();
            app.InitializeComponent();
            var accountDialog = new CodexAccountSwitcher.AccountDialog("test");
            var confirmDialog = new CodexAccountSwitcher.ConfirmDialog("test", "message", "ok");
            accountDialog.Close();
            confirmDialog.Close();
            using var tray = new TrayIconService(() => { }, () => { }, () => { });
            True(tray.IsVisible, "notification icon is registered");
            tray.SetBusy(true);
            tray.SetBusy(false);
            tray.Dispose();
            tray.Dispose();
        }
        catch (Exception ex)
        {
            captured = ex;
        }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    if (!thread.Join(TimeSpan.FromSeconds(10)))
    {
        throw new TimeoutException("WPF dialog construction timed out.");
    }

    if (captured is not null)
    {
        throw new Exception($"Dialog XAML failed: {captured.Message}", captured);
    }

    return Task.CompletedTask;
}

static async Task AppServerProbe()
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    await new CodexAppServerClient().ProbeAsync(timeout.Token);
}

static Task RunningDesktopIsIdentifiable()
{
    True(CodexProcessService.DetectRunningDesktopProcessIds().Count > 0, "running OpenAI.Codex GUI must be identifiable");
    return Task.CompletedTask;
}

static async Task SeedDemo(string root, int count)
{
    var store = TestStore(root);
    var samples = new[]
    {
        ("Primary", "#7C8CFF", "demo-main", 72d, 84d, "pro"),
        ("Development", "#46C7B4", "demo-dev", 48d, 61d, "plus"),
        ("Backup", "#E881A6", "demo-backup", 91d, 96d, "pro")
    };

    foreach (var sample in samples.Take(Math.Clamp(count, 1, samples.Length)))
    {
        var profile = await store.AddAsync(sample.Item1, sample.Item2, Auth(sample.Item3, "demo-access", "demo-refresh"));
        profile.Usage = new UsageSnapshot
        {
            Status = "available",
            PlanType = sample.Item6,
            CheckedAt = DateTimeOffset.UtcNow,
            ShortTerm = new UsageWindow
            {
                RemainingPercent = sample.Item4,
                UsedPercent = 100 - sample.Item4,
                WindowDurationMinutes = 300,
                ResetsAt = DateTimeOffset.UtcNow.AddHours(2)
            },
            Weekly = new UsageWindow
            {
                RemainingPercent = sample.Item5,
                UsedPercent = 100 - sample.Item5,
                WindowDurationMinutes = 10080,
                ResetsAt = DateTimeOffset.UtcNow.AddDays(4)
            }
        };
        await store.SaveProfileAsync(profile);
    }
}

static byte[] Auth(string accountId, string accessToken, string refreshToken) =>
    Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
    {
        auth_mode = "chatgpt",
        tokens = new
        {
            account_id = accountId,
            access_token = accessToken,
            refresh_token = refreshToken
        }
    }));

static async Task WithTempDirectory(Func<string, Task> action)
{
    var root = Path.Combine(Path.GetTempPath(), "CodexAccountSwitcherTests", Guid.NewGuid().ToString("N"));
    var baseRoot = Path.Combine(Path.GetTempPath(), "CodexAccountSwitcherTests");
    Directory.CreateDirectory(root);
    try
    {
        await action(root);
    }
    finally
    {
        if (Directory.Exists(root) && AppPaths.IsPathInside(root, baseRoot))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

static async Task Throws<T>(Func<Task> action) where T : Exception
{
    try
    {
        await action();
    }
    catch (T)
    {
        return;
    }

    throw new Exception($"Expected {typeof(T).Name}");
}

static void True(bool value, string name)
{
    if (!value) throw new Exception($"Assertion failed: {name}");
}

static void Equal<T>(T expected, T actual, string name)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new Exception($"{name}: expected {expected}, actual {actual}");
    }
}

static void NotEqual<T>(T left, T right, string name)
{
    if (EqualityComparer<T>.Default.Equals(left, right))
    {
        throw new Exception($"{name}: values should differ");
    }
}

static async Task LockedTemporaryCleanupIsNonFatal()
{
    await WithTempDirectory(async root =>
    {
        var home = Path.Combine(root, "isolated-home");
        Directory.CreateDirectory(home);
        var deleted = await CodexAppServerClient.TryDeleteTemporaryHomeAsync(
            home,
            root,
            (_, _) => throw new UnauthorizedAccessException("simulated locked Git pack"),
            retryCount: 0,
            retryDelay: TimeSpan.Zero);

        True(!deleted, "locked temporary cleanup must be deferred without throwing");
    });
}

static async Task ExternalTokenModeProbe()
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    await new CodexAppServerClient().ProbeExternalTokenModeAsync(timeout.Token);
}

static ProfileStore TestStore(string root) => new(root, new TestCredentialProtector());

sealed class TestCredentialProtector : ICredentialProtector
{
    private static readonly byte[] Header = Encoding.ASCII.GetBytes("SYNTHETIC-TEST-ONLY\0");
    public byte[] Protect(ReadOnlySpan<byte> plain)
    {
        var result = new byte[Header.Length + plain.Length];
        Header.CopyTo(result, 0);
        plain.CopyTo(result.AsSpan(Header.Length));
        return result;
    }
    public byte[] Unprotect(ReadOnlySpan<byte> encrypted)
    {
        if (encrypted.Length < Header.Length || !encrypted[..Header.Length].SequenceEqual(Header))
            throw new InvalidDataException("Synthetic test credential header missing.");
        return encrypted[Header.Length..].ToArray();
    }
}

sealed class FailsDuringRecoveryProtector : ICredentialProtector
{
    private readonly TestCredentialProtector _inner = new();
    private int _reads;
    public byte[] Protect(ReadOnlySpan<byte> plain) => _inner.Protect(plain);
    public byte[] Unprotect(ReadOnlySpan<byte> encrypted) => ++_reads == 1
        ? _inner.Unprotect(encrypted)
        : throw new IOException("Synthetic backup read failure.");
}

sealed class FakeProcessService : ICodexProcessService
{
    public int LaunchCallCount { get; private set; }
    public bool FailNextLaunch { get; set; }
    public int CloseCallCount { get; private set; }
    public bool Launched { get; private set; }

    public Task CloseDesktopAsync(CancellationToken cancellationToken = default)
    {
        CloseCallCount++;
        return Task.CompletedTask;
    }

    public Task LaunchDesktopAsync(CancellationToken cancellationToken = default)
    {
        LaunchCallCount++;
        if (FailNextLaunch) { FailNextLaunch = false; throw new IOException("Synthetic launch failure."); }
        Launched = true;
        return Task.CompletedTask;
    }
}

sealed class FakeDesktopAuthLifecycle : ICodexDesktopAuthLifecycle
{
    public Exception? PreparationError { get; set; }
    public Action<byte[]>? PrepareAction { get; set; }
    public Task<byte[]> PrepareAsync(byte[] auth, Func<byte[], Task> persist, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        PrepareAction?.Invoke(auth);
        return PreparationError is null ? Task.FromResult(auth.ToArray()) : Task.FromException<byte[]>(PreparationError);
    }
    public bool LogoutCalled { get; private set; }
    public bool VerifyCalled { get; private set; }
    public Exception? VerificationError { get; init; }
    public Action? LogoutAction { get; init; }

    public Task LogoutAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LogoutCalled = true;
        LogoutAction?.Invoke();
        return Task.CompletedTask;
    }

    public Task VerifyAsync(string expectedFingerprint, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        VerifyCalled = true;
        return VerificationError is null ? Task.CompletedTask : Task.FromException(VerificationError);
    }
}

sealed class FakeCodexProcessRuntime : ICodexProcessRuntime
{
    private readonly Queue<IReadOnlyList<ICodexProcessHandle>> _scans;

    public FakeCodexProcessRuntime(params ICodexProcessHandle[][] scans)
    {
        _scans = new Queue<IReadOnlyList<ICodexProcessHandle>>(scans);
        UtcNow = DateTimeOffset.Parse("2026-08-19T00:00:00Z");
    }

    public DateTimeOffset UtcNow { get; private set; }
    public int ScanCount { get; private set; }
    public bool Launched { get; private set; }

    public IReadOnlyList<ICodexProcessHandle> GetDesktopProcesses()
    {
        ScanCount++;
        return _scans.Count == 0 ? Array.Empty<ICodexProcessHandle>() : _scans.Dequeue();
    }

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        UtcNow += delay;
        return Task.CompletedTask;
    }

    public void LaunchDesktop() => Launched = true;
}

sealed class FakeCodexProcessHandle : ICodexProcessHandle
{
    private const string CodexPath = @"C:\Program Files\WindowsApps\OpenAI.Codex_26.814.5167.0_x64__2p2nqsd0c76g0\app\ChatGPT.exe";
    private static readonly string AppServerPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OpenAI", "Codex", "bin", "runtime", "codex.exe");
    private readonly string? _path;
    private readonly string _processName;
    private readonly bool _throwOnPath;
    private readonly bool _exitOnClose;
    private readonly bool _exitOnKill;

    private FakeCodexProcessHandle(
        int id,
        string processName,
        string? path,
        bool throwOnPath,
        bool hasExited,
        bool hasMainWindow,
        bool exitOnClose,
        bool exitOnKill)
    {
        Id = id;
        _processName = processName;
        _path = path;
        _throwOnPath = throwOnPath;
        HasExited = hasExited;
        MainWindowHandle = hasMainWindow ? new IntPtr(id) : IntPtr.Zero;
        _exitOnClose = exitOnClose;
        _exitOnKill = exitOnKill;
    }

    public int Id { get; }
    public string ProcessName => _processName;
    public string? ExecutablePath => _throwOnPath
        ? throw new InvalidOperationException("process exited during path inspection")
        : _path;
    public IntPtr MainWindowHandle { get; }
    public bool HasExited { get; private set; }
    public bool CloseRequested { get; private set; }
    public bool KillRequested { get; private set; }
    public bool Disposed { get; private set; }

    public static FakeCodexProcessHandle Codex(
        int id,
        bool hasMainWindow = false,
        bool exitOnClose = false,
        bool exitOnKill = false) =>
        new(id, "ChatGPT", CodexPath, false, false, hasMainWindow, exitOnClose, exitOnKill);

    public static FakeCodexProcessHandle AppServer(int id, bool exitOnKill = false) =>
        new(id, "codex", AppServerPath, false, false, false, false, exitOnKill);

    public static FakeCodexProcessHandle Ambiguous(int id, bool hasExited) =>
        new(id, "Unknown", null, true, hasExited, false, false, false);

    public bool CloseMainWindow()
    {
        CloseRequested = true;
        if (_exitOnClose) HasExited = true;
        return true;
    }

    public void KillProcessTree()
    {
        KillRequested = true;
        if (_exitOnKill) HasExited = true;
    }

    public void Dispose() => Disposed = true;
}
