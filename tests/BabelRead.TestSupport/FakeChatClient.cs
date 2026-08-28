using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace BabelRead.TestSupport;

/// <summary>
/// Deterministic <see cref="IChatClient"/> test double. Returns a canned transform of the last user
/// message (default: a "[translated] " prefix), optionally after a delay to simulate a slow local
/// model, and can be told to throw to exercise failure paths. Records how many calls it received.
/// </summary>
public sealed class FakeChatClient : IChatClient
{
    private readonly Func<string, string> _transform;
    private readonly TimeSpan _delay;
    private readonly Exception? _throw;
    private int _callCount;
    private IReadOnlyList<ChatMessage> _lastMessages = [];

    public FakeChatClient(Func<string, string>? transform = null, TimeSpan? delay = null, Exception? throwOnCall = null)
    {
        _transform = transform ?? (s => "[translated] " + s);
        _delay = delay ?? TimeSpan.Zero;
        _throw = throwOnCall;
    }

    /// <summary>Number of completion calls received — lets tests assert cache reuse / prefetch behaviour.</summary>
    public int CallCount => Volatile.Read(ref _callCount);

    /// <summary>The messages of the most recent call, exactly as the service built them — lets security
    /// tests assert the system/user split that keeps untrusted document text out of the instructions.</summary>
    public IReadOnlyList<ChatMessage> LastMessages => Volatile.Read(ref _lastMessages);

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _callCount);
        Volatile.Write(ref _lastMessages, messages.ToList());

        if (_delay > TimeSpan.Zero)
        {
            await Task.Delay(_delay, cancellationToken).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (_throw is not null)
        {
            throw _throw;
        }

        var lastUser = messages.LastOrDefault(m => m.Role == ChatRole.User)?.Text ?? string.Empty;
        return new ChatResponse(new ChatMessage(ChatRole.Assistant, _transform(lastUser)));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
        yield return new ChatResponseUpdate(ChatRole.Assistant, response.Text);
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}
