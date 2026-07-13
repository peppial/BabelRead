using BabelRead.Core.Translation;

namespace BabelRead.TestSupport;

/// <summary>A prefetch coordinator that does nothing — keeps model call counts deterministic in tests
/// that focus on navigation/caching rather than prefetch.</summary>
public sealed class NoOpPrefetchCoordinator : IPrefetchCoordinator
{
    public void OnPageSettled(PrefetchContext context, int currentIndex, ReadingDirection direction)
    {
    }

    public void CancelPending()
    {
    }
}
