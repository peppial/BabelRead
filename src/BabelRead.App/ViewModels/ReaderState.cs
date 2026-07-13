namespace BabelRead.App.ViewModels;

/// <summary>High-level state of the reader surface, driving which view is shown.</summary>
public enum ReaderState
{
    /// <summary>No document open yet.</summary>
    NoDocument,

    /// <summary>A page translation is in progress.</summary>
    Loading,

    /// <summary>Content is available to read.</summary>
    Content,

    /// <summary>The page has no extractable text (image-only / illustration).</summary>
    NoText,

    /// <summary>The last operation failed; a retry is offered.</summary>
    Error,
}
