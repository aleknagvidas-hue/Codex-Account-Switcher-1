using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CodexAccountSwitcher.Models;

namespace CodexAccountSwitcher.Services;

public sealed class CodexAppServerClient
{
    private readonly string? _codexExecutable;
    private readonly string _tempRoot;
    private readonly BrowserProfileService _browserProfiles;

    public CodexAppServerClient(
        string? codexExecutable = null,
        string? tempRoot = null,
        BrowserProfileService? browserProfiles = null)
    {
        _codexExecutable = codexExecutable;
        _tempRoot = Path.GetFullPath(tempRoot ?? Path.Combine(AppPaths.DataRoot, "temp"));
        _browserProfiles = browserProfiles ?? new BrowserProfileService();
    }

    public async Task ProbeAsync(CancellationToken cancellationToken = default)
    {
        await RunInIsolatedHomeAsync(
            null,
            async (session, _, token) =>
            {
                await session.RequestAsync(
                    2,
                    "account/read",
                    new { refreshToken = false },
                    TimeSpan.FromSeconds(20),
                    token);
                return true;
            },
            cancellationToken);
    }

    public async Task LogoutDesktopAccountAsync(CancellationToken cancellationToken = default)
    {
        var executable = ResolveExecutable();
        await using var session = await AppServerSession.StartAsync(executable, AppPaths.CurrentCodexHome, cancellationToken);
        await session.RequestAsync(2, "account/logout", null, TimeSpan.FromSeconds(20), cancellationToken);
    }

    public async Task VerifyDesktopAccountAsync(string expectedFingerprint, CancellationToken cancellationToken = default)
    {
        var executable = ResolveExecutable();
        await using var session = await AppServerSession.StartAsync(executable, AppPaths.CurrentCodexHome, cancellationToken);
        var result = await session.RequestAsync(
            2, "account/read", new { refreshToken = false }, TimeSpan.FromSeconds(30), cancellationToken);
        if (!TryReadString(result, out var accountType, "account", "type") || accountType != "chatgpt")
            throw new InvalidOperationException("Codex did not accept the selected ChatGPT sign-in.");

        var auth = await File.ReadAllBytesAsync(AppPaths.CurrentAuthPath, cancellationToken);
        try
        {
            if (!string.Equals(AuthIdentity.ValidateAndFingerprint(auth), expectedFingerprint, StringComparison.Ordinal))
                throw new InvalidOperationException("Codex opened a different account than the one selected.");
        }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(auth); }
    }

    public async Task<AppServerUsageResult> ReadUsageAsync(byte[] authJson, CancellationToken cancellationToken = default)
    {
        var loginParameters = CreateReadOnlyLoginParameters(authJson);
        // Never provide a refresh token or write a managed auth.json during monitoring.
        return await RunInIsolatedHomeAsync(null, async (session, _, token) =>
        {
            await session.RequestAsync(2, "account/login/start", loginParameters, TimeSpan.FromSeconds(20), token);
            var result = await session.RequestAsync(3, "account/rateLimits/read", null, TimeSpan.FromSeconds(30), token);
            return new AppServerUsageResult(ParseRateLimits(result, null, DateTimeOffset.UtcNow), null);
        }, cancellationToken);
    }

    internal static object CreateReadOnlyLoginParameters(byte[] authJson)
    {
        AuthIdentity.ValidateAndFingerprint(authJson);
        using var document = JsonDocument.Parse(authJson);
        var tokens = document.RootElement.GetProperty("tokens");
        var accessToken = tokens.GetProperty("access_token").GetString();
        var accountId = tokens.GetProperty("account_id").GetString();
        if (string.IsNullOrWhiteSpace(accessToken) || string.IsNullOrWhiteSpace(accountId))
            throw new InvalidDataException("A ChatGPT access token and account identity are required.");
        return new { type = "chatgptAuthTokens", accessToken, chatgptAccountId = accountId };
    }

    public async Task ProbeExternalTokenModeAsync(CancellationToken cancellationToken = default)
    {
        await RunInIsolatedHomeAsync(null, async (session, home, token) =>
        {
            var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"sub\":\"synthetic-probe\",\"exp\":4102444800,\"https://api.openai.com/auth\":{\"chatgpt_account_id\":\"synthetic-account\",\"chatgpt_plan_type\":\"plus\"}}"))
                .TrimEnd('=').Replace('+', '-').Replace('/', '_');
            var result = await session.RequestAsync(2, "account/login/start", new
            {
                type = "chatgptAuthTokens", accessToken = "eyJhbGciOiJIUzI1NiJ9." + payload + ".synthetic",
                chatgptAccountId = "synthetic-account", chatgptPlanType = "plus"
            }, TimeSpan.FromSeconds(20), token);
            if (!TryReadString(result, out var mode, "type") || mode != "chatgptAuthTokens")
                throw new InvalidOperationException("This Codex version does not support read-only token monitoring.");
            if (File.Exists(Path.Combine(home, "auth.json")))
                throw new InvalidOperationException("External-token probe unexpectedly persisted credentials.");
            return true;
        }, cancellationToken);
    }

    public async Task<byte[]> RenewManagedAuthAsync(byte[] authJson, CancellationToken cancellationToken = default)
    {
        var expectedFingerprint = AuthIdentity.ValidateAndFingerprint(authJson);
        return await RunInIsolatedHomeAsync(authJson, async (session, home, token) =>
        {
            var account = await session.RequestAsync(
                2, "account/read", new { refreshToken = true }, TimeSpan.FromSeconds(30), token);
            if (!TryReadString(account, out var accountType, "account", "type") || accountType != "chatgpt")
                throw new InvalidOperationException("The saved ChatGPT sign-in has expired.");

            var renewed = await WaitForAuthAsync(home, token);
            if (!string.Equals(AuthIdentity.ValidateAndFingerprint(renewed), expectedFingerprint, StringComparison.Ordinal))
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(renewed);
                throw new InvalidOperationException("Codex renewed a different account than the selected profile.");
            }
            return renewed;
        }, cancellationToken);
    }

    internal async Task<byte[]> PrepareManagedAuthAsync(
        byte[] authJson, Func<byte[], Task> persist, CancellationToken cancellationToken)
    {
        var expected = AuthIdentity.ValidateAndFingerprint(authJson);
        return await RunInIsolatedHomeAsync(authJson, async (session, home, token) =>
        {
            try
            {
                var account = await session.RequestAsync(
                    2, "account/read", new { refreshToken = false }, TimeSpan.FromSeconds(30), token);
                if (!TryReadString(account, out var kind, "account", "type") || kind != "chatgpt")
                    throw CodexRequestException.FromServerMessage("not authenticated");
                // A real authenticated read lets Codex renew only if needed. A cached account/read alone is insufficient.
                await session.RequestAsync(3, "account/rateLimits/read", null, TimeSpan.FromSeconds(30), token);
            }
            finally
            {
                // Renewal may have happened before a later request failed. Do not discard a rotated refresh token.
                var authPath = Path.Combine(home, "auth.json");
                if (File.Exists(authPath))
                {
                    var latest = await File.ReadAllBytesAsync(authPath, CancellationToken.None);
                    try
                    {
                        string? fingerprint = null;
                        try { fingerprint = AuthIdentity.ValidateAndFingerprint(latest); }
                        catch (InvalidDataException) { /* A rejected login may be cleared; preserve the original RPC error. */ }
                        if (fingerprint is not null && fingerprint != expected)
                            throw new InvalidDataException("Codex returned a different account during sign-in validation.");
                        if (fingerprint == expected && !latest.AsSpan().SequenceEqual(authJson)) await persist(latest);
                    }
                    finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(latest); }
                }
            }
            var accepted = await File.ReadAllBytesAsync(Path.Combine(home, "auth.json"), token);
            if (AuthIdentity.ValidateAndFingerprint(accepted) != expected)
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(accepted);
                throw new InvalidDataException("The validated login belongs to a different account.");
            }
            return accepted;
        }, cancellationToken);
    }

    public async Task<AppServerAuthResult> LoginAsync(
        string? browserProfileKey = null,
        CancellationToken cancellationToken = default)
    {
        return await RunInIsolatedHomeAsync(
            null,
            async (session, home, token) =>
            {
                var login = await session.RequestAsync(
                    2,
                    "account/login/start",
                    new { type = "chatgpt", useHostedLoginSuccessPage = true, appBrand = "chatgpt" },
                    TimeSpan.FromSeconds(30),
                    token);

                if (!TryReadString(login, out var authUrl, "authUrl") || string.IsNullOrWhiteSpace(authUrl))
                {
                    throw new InvalidOperationException("Codex did not return a sign-in URL.");
                }

                var loginId = TryReadString(login, out var id, "loginId") ? id : null;
                if (string.IsNullOrWhiteSpace(browserProfileKey))
                    Process.Start(new ProcessStartInfo(authUrl) { UseShellExecute = true });
                else
                    _browserProfiles.OpenAuthenticationUrl(authUrl, browserProfileKey);

                var completed = await session.WaitForNotificationAsync(
                    "account/login/completed",
                    TimeSpan.FromMinutes(10),
                    token);
                var completedLoginId = TryReadString(completed, out var notifiedId, "loginId") ? notifiedId : null;
                if (loginId is not null && completedLoginId is not null && !string.Equals(loginId, completedLoginId, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("A completion notification for a different sign-in request was received.");
                }

                if (!TryReadBoolean(completed, out var success, "success") || !success)
                {
                    var error = TryReadString(completed, out var errorText, "error") ? errorText : null;
                    throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? "Sign-in did not complete." : error);
                }

                string? planType = null;
                try
                {
                    var account = await session.RequestAsync(
                        3,
                        "account/read",
                        new { refreshToken = false },
                        TimeSpan.FromSeconds(20),
                        token);
                    planType = ReadString(account, "account", "planType");
                }
                catch
                {
                    // Plan type is optional profile metadata.
                }

                var auth = await WaitForAuthAsync(home, token);
                return new AppServerAuthResult(auth, planType);
            },
            cancellationToken);
    }

    public static UsageSnapshot ParseRateLimits(JsonElement result, string? planType, DateTimeOffset checkedAt)
    {
        JsonElement bucket = default;
        var found = false;

        if (result.TryGetProperty("rateLimitsByLimitId", out var byId) && byId.ValueKind == JsonValueKind.Object)
        {
            if (byId.TryGetProperty("codex", out bucket) && bucket.ValueKind == JsonValueKind.Object)
            {
                found = true;
            }
            else
            {
                foreach (var property in byId.EnumerateObject())
                {
                    if (property.Value.ValueKind == JsonValueKind.Object)
                    {
                        bucket = property.Value;
                        found = true;
                        break;
                    }
                }
            }
        }

        if (!found && result.TryGetProperty("rateLimits", out var legacy) && legacy.ValueKind == JsonValueKind.Object)
        {
            bucket = legacy;
            found = true;
        }

        if (!found)
        {
            return new UsageSnapshot
            {
                Status = "unavailable",
                Message = "No usage data was returned.",
                PlanType = planType,
                CheckedAt = checkedAt
            };
        }

        var effectivePlan = TryReadString(bucket, out var bucketPlan, "planType") ? bucketPlan : planType;
        var primary = bucket.TryGetProperty("primary", out var primaryElement)
            ? ParseWindow(primaryElement)
            : null;
        var secondary = bucket.TryGetProperty("secondary", out var secondaryElement)
            ? ParseWindow(secondaryElement)
            : null;

        UsageWindow? weekly = null;
        UsageWindow? shortTerm = null;
        foreach (var window in new[] { primary, secondary }.Where(item => item is not null).Cast<UsageWindow>())
        {
            if (window.WindowDurationMinutes == 7 * 24 * 60)
            {
                weekly ??= window;
            }
            else if (window.WindowDurationMinutes == 5 * 60)
            {
                shortTerm ??= window;
            }
        }

        return new UsageSnapshot
        {
            Status = weekly is null && shortTerm is null ? "unavailable" : "available",
            Message = weekly is null && shortTerm is null ? "No usage windows were returned." : null,
            PlanType = effectivePlan,
            CheckedAt = checkedAt,
            Weekly = weekly,
            ShortTerm = shortTerm
        };
    }

    private async Task<T> RunInIsolatedHomeAsync<T>(
        byte[]? initialAuth,
        Func<AppServerSession, string, CancellationToken, Task<T>> action,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_tempRoot);
        var home = Path.Combine(_tempRoot, Guid.NewGuid().ToString("N"));
        if (!AppPaths.IsPathInside(home, _tempRoot))
        {
            throw new InvalidOperationException("The temporary-directory boundary check failed.");
        }

        Directory.CreateDirectory(home);
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(home, "config.toml"),
                "cli_auth_credentials_store = \"file\"\n",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken);

            if (initialAuth is not null)
            {
                await File.WriteAllBytesAsync(Path.Combine(home, "auth.json"), initialAuth, cancellationToken);
            }

            var executable = ResolveExecutable();

            await using var session = await AppServerSession.StartAsync(executable, home, cancellationToken);
            return await action(session, home, cancellationToken);
        }
        finally
        {
            await TryDeleteTemporaryHomeAsync(home, _tempRoot);
        }
    }

    internal static async Task<bool> TryDeleteTemporaryHomeAsync(
        string home,
        string tempRoot,
        Action<string, bool>? deleteDirectory = null,
        int retryCount = 4,
        TimeSpan? retryDelay = null)
    {
        if (!Directory.Exists(home) || !AppPaths.IsPathInside(home, tempRoot))
            return true;

        deleteDirectory ??= Directory.Delete;
        var delay = retryDelay ?? TimeSpan.FromMilliseconds(250);
        Exception? lastError = null;

        for (var attempt = 0; attempt <= Math.Max(0, retryCount); attempt++)
        {
            try
            {
                deleteDirectory(home, true);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                lastError = ex;
                if (attempt < retryCount && delay > TimeSpan.Zero)
                    await Task.Delay(delay);
            }
        }

        DiagnosticTrace.Write(
            $"temporary Codex home cleanup deferred: {lastError?.GetType().Name ?? "unknown error"}");
        return false;
    }

    private string ResolveExecutable()
    {
        var executable = _codexExecutable ?? AppPaths.FindCodexExecutable();
        if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
            throw new FileNotFoundException("The installed Codex App Server executable was not found.");
        return executable;
    }

    private static UsageWindow? ParseWindow(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !TryReadDouble(element, out var usedPercent, "usedPercent", "used_percent"))
        {
            return null;
        }

        var duration = TryReadInt(element, out var minutes, "windowDurationMins", "window_duration_mins")
            ? minutes
            : (int?)null;
        DateTimeOffset? resetsAt = null;
        if (TryReadLong(element, out var timestamp, "resetsAt", "resets_at") && timestamp > 0)
        {
            try
            {
                resetsAt = DateTimeOffset.FromUnixTimeSeconds(timestamp);
            }
            catch
            {
                resetsAt = null;
            }
        }

        var used = Math.Clamp(usedPercent, 0, 100);
        return new UsageWindow
        {
            UsedPercent = used,
            RemainingPercent = Math.Clamp(100 - used, 0, 100),
            WindowDurationMinutes = duration,
            ResetsAt = resetsAt
        };
    }

    private static async Task<byte[]> WaitForAuthAsync(string home, CancellationToken cancellationToken)
    {
        var path = Path.Combine(home, "auth.json");
        for (var attempt = 0; attempt < 50; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(path))
            {
                var auth = await File.ReadAllBytesAsync(path, cancellationToken);
                AuthIdentity.ValidateAndFingerprint(auth);
                return auth;
            }

            await Task.Delay(100, cancellationToken);
        }

        throw new FileNotFoundException("The authentication file was not created after sign-in.", path);
    }

    private static async Task<byte[]?> ReadAuthIfPresentAsync(string home, CancellationToken cancellationToken)
    {
        var path = Path.Combine(home, "auth.json");
        if (!File.Exists(path))
        {
            return null;
        }

        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        AuthIdentity.ValidateAndFingerprint(bytes);
        return bytes;
    }

    private static string SafeMessage(Exception exception)
    {
        var message = exception.Message;
        return message.Length > 160 ? message[..160] : message;
    }

    private static string? ReadString(JsonElement element, params string[] path) =>
        TryReadString(element, out var value, path) ? value : null;

    private static bool TryReadString(JsonElement element, out string value, params string[] path)
    {
        value = string.Empty;
        foreach (var segment in path)
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(segment, out element))
            {
                return false;
            }
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = element.GetString() ?? string.Empty;
        return true;
    }

    private static bool TryReadBoolean(JsonElement element, out bool value, params string[] path)
    {
        value = false;
        foreach (var segment in path)
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(segment, out element))
            {
                return false;
            }
        }

        if (element.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return false;
        }

        value = element.GetBoolean();
        return true;
    }

    private static bool TryReadDouble(JsonElement element, out double value, params string[] names)
    {
        value = default;
        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var property)
                && property.ValueKind == JsonValueKind.Number
                && property.TryGetDouble(out value))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryReadInt(JsonElement element, out int value, params string[] names)
    {
        value = default;
        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var property)
                && property.ValueKind == JsonValueKind.Number
                && property.TryGetInt32(out value))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryReadLong(JsonElement element, out long value, params string[] names)
    {
        value = default;
        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var property)
                && property.ValueKind == JsonValueKind.Number
                && property.TryGetInt64(out value))
            {
                return true;
            }
        }

        return false;
    }

    private sealed class AppServerSession : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly Task<string> _stderr;
        private readonly List<(string Method, JsonElement Parameters)> _pendingNotifications = new();

        private AppServerSession(Process process)
        {
            _process = process;
            _stderr = process.StandardError.ReadToEndAsync();
        }

        public static async Task<AppServerSession> StartAsync(
            string executable,
            string codexHome,
            CancellationToken cancellationToken)
        {
            var startInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = codexHome
            };
            startInfo.ArgumentList.Add("app-server");
            startInfo.Environment["CODEX_HOME"] = codexHome;
            foreach (var key in new[] { "OPENAI_API_KEY", "CODEX_API_KEY", "CODEX_AUTH_JWT", "CODEX_SQLITE_HOME", "CODEX_ACCESS_TOKEN" })
                startInfo.Environment.Remove(key);

            var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Codex App Server could not be started.");
            var session = new AppServerSession(process);
            try
            {
                await session.SendAsync(new Dictionary<string, object?>
                {
                    ["method"] = "initialize",
                    ["id"] = 1,
                    ["params"] = new
                    {
                        capabilities = new { experimentalApi = true },
                        clientInfo = new
                        {
                            name = "codex_account_switcher",
                            title = "Codex Account Switcher",
                            version = "1.0.0"
                        }
                    }
                }, cancellationToken);
                await session.ReadResponseAsync(1, TimeSpan.FromSeconds(20), cancellationToken);
                await session.SendAsync(new Dictionary<string, object?>
                {
                    ["method"] = "initialized",
                    ["params"] = new { }
                }, cancellationToken);
                return session;
            }
            catch
            {
                await session.DisposeAsync();
                throw;
            }
        }

        public async Task<JsonElement> RequestAsync(
            int id,
            string method,
            object? parameters,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            var message = new Dictionary<string, object?>
            {
                ["method"] = method,
                ["id"] = id
            };
            if (parameters is not null)
            {
                message["params"] = parameters;
            }

            await SendAsync(message, cancellationToken);
            try { return await ReadResponseAsync(id, timeout, cancellationToken); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new IOException("Codex timed out responding to " + method + ". Retry the operation.");
            }
        }

        public async Task<JsonElement> WaitForNotificationAsync(
            string method,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            var pending = _pendingNotifications.FindIndex(item => string.Equals(item.Method, method, StringComparison.Ordinal));
            if (pending >= 0)
            {
                var parameters = _pendingNotifications[pending].Parameters;
                _pendingNotifications.RemoveAt(pending);
                return parameters;
            }

            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(timeout);
            while (true)
            {
                var message = await ReadMessageAsync(timeoutSource.Token);
                if (message.TryGetProperty("method", out var methodElement)
                    && methodElement.ValueKind == JsonValueKind.String
                    && string.Equals(methodElement.GetString(), method, StringComparison.Ordinal)
                    && message.TryGetProperty("params", out var parameters))
                {
                    return parameters.Clone();
                }
            }
        }

        private async Task SendAsync(object message, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var json = JsonSerializer.Serialize(message);
            await _process.StandardInput.WriteLineAsync(json.AsMemory(), cancellationToken);
            await _process.StandardInput.FlushAsync(cancellationToken);
        }

        private async Task<JsonElement> ReadResponseAsync(int expectedId, TimeSpan timeout, CancellationToken cancellationToken)
        {
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(timeout);
            while (true)
            {
                var message = await ReadMessageAsync(timeoutSource.Token);
                if (message.TryGetProperty("method", out var requestMethod) &&
                    requestMethod.GetString() == "account/chatgptAuthTokens/refresh" &&
                    message.TryGetProperty("id", out var requestId))
                {
                    await SendAsync(new { id = requestId.Clone(), error = new { code = -32000,
                        message = "Read-only monitor does not refresh credentials." } }, timeoutSource.Token);
                    throw CodexRequestException.FromServerMessage("not authenticated");
                }
                if (!message.TryGetProperty("id", out var idElement)
                    || idElement.ValueKind != JsonValueKind.Number
                    || !idElement.TryGetInt32(out var id)
                    || id != expectedId)
                {
                    CaptureNotification(message);
                    continue;
                }

                if (message.TryGetProperty("error", out var error))
                {
                    var errorMessage = TryReadString(error, out var text, "message") ? text : "Codex App Server error";
                    var code = error.TryGetProperty("code", out var codeElement) && codeElement.TryGetInt32(out var number) ? number : (int?)null;
                    throw CodexRequestException.FromServerMessage(errorMessage, code);
                }

                if (!message.TryGetProperty("result", out var result))
                {
                    throw new InvalidDataException("The Codex App Server response did not contain a result.");
                }

                return result.Clone();
            }
        }

        private void CaptureNotification(JsonElement message)
        {
            if (message.TryGetProperty("method", out var methodElement)
                && methodElement.ValueKind == JsonValueKind.String
                && message.TryGetProperty("params", out var parameters))
            {
                _pendingNotifications.Add((methodElement.GetString() ?? string.Empty, parameters.Clone()));
            }
        }

        private async Task<JsonElement> ReadMessageAsync(CancellationToken cancellationToken)
        {
            while (true)
            {
                var line = await _process.StandardOutput.ReadLineAsync(cancellationToken);
                if (line is null)
                {
                    await _stderr;
                    throw new InvalidOperationException("The isolated Codex App Server exited before completing the request.");
                }

                try
                {
                    using var document = JsonDocument.Parse(line);
                    return document.RootElement.Clone();
                }
                catch (JsonException)
                {
                    // Ignore non-protocol diagnostic lines.
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                _process.StandardInput.Close();
                if (!_process.HasExited)
                {
                    try { await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(1)); }
                    catch (TimeoutException) { if (!_process.HasExited) _process.Kill(entireProcessTree: true); }
                }

                await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                await _stderr.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch
            {
                // Disposal must not hide the original operation result.
            }
            finally
            {
                _process.Dispose();
            }
        }
    }
}
