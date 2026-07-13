using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using BabelRead.Core.Domain;

namespace BabelRead.Core.Preferences;

/// <summary>Thread-safe in-memory secret store — the default on platforms without a wired native
/// backend, and the fake used by tests. Not persisted across process restarts.</summary>
public sealed class InMemorySecretStore : ISecretStore
{
    private readonly ConcurrentDictionary<string, string> _store = new(StringComparer.Ordinal);

    public Task<SecretRef> SetAsync(string name, string secret, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _store[name] = secret;
        return Task.FromResult(new SecretRef(name));
    }

    public Task<string?> GetAsync(SecretRef reference, CancellationToken ct = default) =>
        Task.FromResult(reference.HasValue && _store.TryGetValue(reference.Value, out var v) ? v : null);

    public Task RemoveAsync(SecretRef reference, CancellationToken ct = default)
    {
        if (reference.HasValue)
        {
            _store.TryRemove(reference.Value, out _);
        }

        return Task.CompletedTask;
    }
}

/// <summary>macOS Keychain-backed secret store using the <c>security</c> CLI (generic passwords under
/// the "BabelRead" service). Secrets never touch the preferences file (FR-012).</summary>
public sealed class MacOsKeychainSecretStore : ISecretStore
{
    private const string Service = "BabelRead";

    public static bool IsSupported => RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

    public async Task<SecretRef> SetAsync(string name, string secret, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        // -U updates the item if it already exists.
        await RunAsync(["add-generic-password", "-s", Service, "-a", name, "-w", secret, "-U"], ct).ConfigureAwait(false);
        return new SecretRef(name);
    }

    public async Task<string?> GetAsync(SecretRef reference, CancellationToken ct = default)
    {
        if (!reference.HasValue)
        {
            return null;
        }

        var (exit, stdout) = await RunAsync(["find-generic-password", "-s", Service, "-a", reference.Value, "-w"], ct).ConfigureAwait(false);
        return exit == 0 ? stdout.TrimEnd('\n') : null;
    }

    public async Task RemoveAsync(SecretRef reference, CancellationToken ct = default)
    {
        if (reference.HasValue)
        {
            await RunAsync(["delete-generic-password", "-s", Service, "-a", reference.Value], ct).ConfigureAwait(false);
        }
    }

    private static async Task<(int Exit, string Stdout)> RunAsync(string[] args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("security")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start 'security'.");
        var stdout = await process.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        return (process.ExitCode, stdout);
    }
}
