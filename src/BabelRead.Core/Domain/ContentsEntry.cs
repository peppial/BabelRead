namespace BabelRead.Core.Domain;

/// <summary>One line of a document's table of contents. <see cref="TargetKey"/> looks the destination up
/// in <see cref="Document.Anchors"/> — the same table internal hyperlinks resolve through, so jumping to a
/// chapter and following a link are the same journey. <see cref="Depth"/> is the nesting level in the
/// book's navigation tree (0 for a top-level entry), which the reader shows as indentation.</summary>
public sealed record ContentsEntry(string Title, string TargetKey, int Depth);
