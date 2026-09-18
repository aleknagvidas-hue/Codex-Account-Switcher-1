namespace CodexAccountSwitcher.Services;

internal interface ICodexDesktopAuthLifecycle
{
    Task LogoutAsync(CancellationToken cancellationToken);
    Task<byte[]> PrepareAsync(byte[] auth, Func<byte[], Task> persist, CancellationToken cancellationToken);
    Task VerifyAsync(string expectedFingerprint, CancellationToken cancellationToken);
}

internal sealed class CodexDesktopAuthLifecycle : ICodexDesktopAuthLifecycle
{
    private readonly CodexAppServerClient _client = new();

    public Task LogoutAsync(CancellationToken cancellationToken) =>
        _client.LogoutDesktopAccountAsync(cancellationToken);

    public Task<byte[]> PrepareAsync(byte[] auth, Func<byte[], Task> persist, CancellationToken cancellationToken) =>
        _client.PrepareManagedAuthAsync(auth, persist, cancellationToken);

    public Task VerifyAsync(string expectedFingerprint, CancellationToken cancellationToken) =>
        _client.VerifyDesktopAccountAsync(expectedFingerprint, cancellationToken);
}
