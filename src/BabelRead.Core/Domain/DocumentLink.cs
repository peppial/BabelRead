namespace BabelRead.Core.Domain;

/// <summary>An internal hyperlink found in the document's text, anchored to the segment/offset it
/// starts at. <see cref="TargetKey"/> looks up the destination in <see cref="Document.Anchors"/>.</summary>
public sealed record DocumentLink(int SegmentIndex, int Start, int Length, string TargetKey);

/// <summary>Where a <see cref="DocumentLink"/> (or a whole-file reference) resolves to: a segment and
/// an offset within that segment's text.</summary>
public readonly record struct LinkTarget(int SegmentIndex, int Offset);
