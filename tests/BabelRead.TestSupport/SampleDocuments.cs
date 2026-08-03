using System.IO.Compression;
using System.Text;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace BabelRead.TestSupport;

/// <summary>Generates small, deterministic PDF/EPUB fixtures at runtime so reader tests need no
/// committed binary files.</summary>
public static class SampleDocuments
{
    /// <summary>
    /// Writes a PDF with one <em>reader</em> page per entry in <paramref name="pageTexts"/>; an
    /// empty/whitespace entry produces a text-less page (to exercise the image-only edge case).
    /// The reader paginates by content rather than by physical PDF page, so each entry is padded past the
    /// largest page budget the paginator can pick — that keeps one entry = one reader page whatever the
    /// viewport, which is the contract navigation tests rely on.
    /// </summary>
    public static string CreatePdf(string path, params string[] pageTexts)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        foreach (var text in pageTexts)
        {
            var page = builder.AddPage(595, 842); // A4 in points
            if (!string.IsNullOrWhiteSpace(text))
            {
                page.AddText(PadToOwnReaderPage(text), 12, new PdfPoint(50, 780), font);
            }
        }

        File.WriteAllBytes(path, builder.Build());
        return path;
    }

    /// <summary>Comfortably above the paginator's maximum page budget, so the entry never shares a page.</summary>
    private const int OwnReaderPageChars = 5200;

    private static string PadToOwnReaderPage(string text)
    {
        // Neutral filler: it must not contain any word a test asserts on ("one", "two", "page", ...).
        var padded = new StringBuilder(text.Trim());
        while (padded.Length < OwnReaderPageChars)
        {
            padded.Append(" Filler sentence that keeps this entry by itself.");
        }

        return padded.ToString();
    }

    /// <summary>Writes a single-page PDF placing each line at an explicit (left, baseline) point, so tests
    /// can reproduce paragraph layouts — first-line indents, blank-line gaps — for the paragraph extractor.</summary>
    public static string CreatePdfWithLines(string path, IEnumerable<(string Text, double Left, double Baseline)> lines)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var page = builder.AddPage(595, 842);
        foreach (var (text, left, baseline) in lines)
        {
            page.AddText(text, 12, new PdfPoint(left, baseline), font);
        }

        File.WriteAllBytes(path, builder.Build());
        return path;
    }

    /// <summary>Writes a minimal valid EPUB with one XHTML file per chapter body.</summary>
    public static string CreateEpub(string path, string title, string language, params string[] chapterBodies)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);

        // mimetype must be the first entry and stored uncompressed.
        WriteEntry(archive, "mimetype", "application/epub+zip", CompressionLevel.NoCompression);

        WriteEntry(archive, "META-INF/container.xml",
            """
            <?xml version="1.0"?>
            <container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
              <rootfiles>
                <rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/>
              </rootfiles>
            </container>
            """);

        // EPUB 3 requires a navigation document referenced by the manifest with properties="nav".
        WriteEntry(archive, "OEBPS/nav.xhtml",
            """
            <?xml version="1.0" encoding="utf-8"?>
            <html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops">
            <head><title>Contents</title></head>
            <body><nav epub:type="toc"><ol><li><a href="ch0.xhtml">Start</a></li></ol></nav></body>
            </html>
            """);

        var manifest = new StringBuilder();
        manifest.Append("<item id=\"nav\" href=\"nav.xhtml\" media-type=\"application/xhtml+xml\" properties=\"nav\"/>");
        var spine = new StringBuilder();
        for (var i = 0; i < chapterBodies.Length; i++)
        {
            var file = $"ch{i}.xhtml";
            manifest.Append($"<item id=\"ch{i}\" href=\"{file}\" media-type=\"application/xhtml+xml\"/>");
            spine.Append($"<itemref idref=\"ch{i}\"/>");
            WriteEntry(archive, $"OEBPS/{file}",
                $"""
                 <?xml version="1.0" encoding="utf-8"?>
                 <html xmlns="http://www.w3.org/1999/xhtml"><head><title>{title}</title></head>
                 <body><p>{chapterBodies[i]}</p></body></html>
                 """);
        }

        WriteEntry(archive, "OEBPS/content.opf",
            $"""
             <?xml version="1.0" encoding="utf-8"?>
             <package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="bookid">
               <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
                 <dc:identifier id="bookid">urn:uuid:test</dc:identifier>
                 <dc:title>{title}</dc:title>
                 <dc:language>{language}</dc:language>
               </metadata>
               <manifest>{manifest}</manifest>
               <spine>{spine}</spine>
             </package>
             """);

        return path;
    }

    /// <summary>A 2-chapter book where chapter 0 links to an id anchor inside chapter 1
    /// (<c>ch1.xhtml</c> — chapters are zero-based, see <see cref="CreateEpub"/>). The anchor is an inline
    /// <c>&lt;a id&gt;</c> rather than a nested <c>&lt;p id&gt;</c> so the chapter body stays a single clean
    /// <c>&lt;p&gt;</c> once <see cref="CreateEpub"/> wraps it, instead of producing invalid nested
    /// <c>&lt;p&gt;&lt;p&gt;...&lt;/p&gt;&lt;/p&gt;</c> markup.</summary>
    public static string CreateEpubWithInternalLink(string path) => CreateEpub(path, "Linked", "en",
        "See <a href=\"ch1.xhtml#note\">the note</a>.",
        "The note body is here with enough words to form a segment.<a id=\"note\"></a>");

    /// <summary>A single-chapter book with one external link (dropped as out-of-document) and one
    /// dangling internal link to a nonexistent anchor (dropped as unresolved).</summary>
    public static string CreateEpubWithExternalAndDanglingLinks(string path) => CreateEpub(path, "Ext", "en",
        "<a href=\"https://example.com\">out</a> and <a href=\"#missing\">nowhere</a>.");

    /// <summary>A 2-chapter book whose <c>ch0</c> body triggers <c>EpubLinkExtractor</c>/<c>HtmlToText</c>
    /// divergence: an id-only <c>&lt;a&gt;</c> immediately followed by text at a block start splits a
    /// whitespace run the extractor's sentinel can't rejoin, leaving a spurious leading space that
    /// <c>HtmlToText</c> never produces. <c>ch1</c> links back to <c>ch0</c>'s (untrustworthy) anchor, so if
    /// the compare-and-drop guard ever failed to protect <c>ch0</c>, that link would resolve.</summary>
    public static string CreateEpubWithDivergentAnchor(string path) => CreateEpub(path, "Divergent", "en",
        "<a id=\"note\"></a>The note text here with enough words to form a segment for testing purposes indeed.",
        "See <a href=\"ch0.xhtml#note\">the note</a> in chapter one for details, and here is filler text to " +
        "make this segment long enough to stand on its own without being coalesced awkwardly into adjacent " +
        "content in a strange way for this particular test fixture to behave reliably as intended.");

    /// <summary>A 2-chapter book whose <c>ch1</c> ends with a content-less <c>&lt;a id&gt;</c> anchor right
    /// after its only paragraph — a common EPUB footnote/endnote marker. Its offset lands past the end of
    /// every tracked segment range (the whitespace trimmed away between the paragraph and the chapter's
    /// end), exercising <c>MapOffsetToSegment</c>'s edge clamp. <c>ch0</c> links to it.</summary>
    public static string CreateEpubWithTrailingAnchor(string path) => CreateEpub(path, "Trailing", "en",
        "See <a href=\"ch1.xhtml#mark\">the mark</a> for more, and here is filler text padding this segment " +
        "out to a reasonable length so it stands on its own as a proper paragraph in this test fixture.",
        "Second chapter paragraph with enough words of its own to form a proper standalone segment for this " +
        "particular test fixture to exercise reliably.<a id=\"mark\"></a>");

    /// <summary>A 2-chapter book whose <c>ch1</c> holds exactly <em>two</em> tracked segment ranges (two
    /// paragraphs each comfortably over <c>MinSegmentChars</c>, so neither is coalesced into the other) with
    /// a self-closing, content-less <c>&lt;a id&gt;</c> anchor sitting on the single-character gap between
    /// them (<c>"\n&lt;a id=\"mark\"/&gt;\n"</c>) rather than past every range. Resolving it exercises
    /// <c>MapOffsetToSegment</c>'s mid-loop two-edge ternary -- distinct from <see cref="CreateEpubWithTrailingAnchor"/>'s
    /// post-loop clamp -- and lands nearer the first paragraph's end. <c>ch0</c> links to it.
    /// <para>Placing the anchor directly against a <c>&lt;p&gt;</c>/<c>&lt;p&gt;</c> block-tag boundary (or
    /// as a paired <c>&lt;a id&gt;&lt;/a&gt;</c>) was tried first and always tripped the compare-and-drop
    /// guard: <c>EpubLinkExtractor</c>'s sentinel characters sit between a stripped-tag's leftover space and
    /// the adjacent newline on one side, blocking the same whitespace-collapse <c>HtmlToText</c> performs
    /// unobstructed, so the two texts diverged by a stray space or an uncollapsed newline run every time. A
    /// bare, self-closing anchor between two literal single newlines (no <c>&lt;p&gt;</c> tags at all inside
    /// the chapter body) is the one arrangement found where the sentinel's neighboring leftover space is
    /// consumed identically on both paths, so the extracted text matches exactly.</para></summary>
    public static string CreateEpubWithAnchorBetweenTwoSegments(string path) => CreateEpub(path, "Between", "en",
        "See <a href=\"ch1.xhtml#mark\">the mark</a> for more, and here is filler text padding this segment " +
        "out to a reasonable length so it stands on its own as a proper paragraph in this test fixture.",
        "AlphaMarker paragraph with enough words of its own to comfortably clear the four hundred character " +
        "minimum segment threshold so that it forms a standalone segment without being merged into any " +
        "neighboring block of text within this chapter, keeping the paragraph long enough on its own merits " +
        "for this particular test fixture to behave reliably and deterministically every time the suite runs " +
        "from now on.\n<a id=\"mark\"/>\n" +
        "BetaMarker paragraph with enough words of its own to comfortably clear the four hundred character " +
        "minimum segment threshold so that it, too, forms a standalone segment without being merged into any " +
        "neighboring block of text within this chapter, keeping this second paragraph long enough on its own " +
        "merits for this particular test fixture to behave reliably and deterministically every time the " +
        "suite runs from now on as well.");

    private static void WriteEntry(ZipArchive archive, string name, string content, CompressionLevel level = CompressionLevel.Optimal)
    {
        var entry = archive.CreateEntry(name, level);
        using var stream = entry.Open();
        var bytes = Encoding.UTF8.GetBytes(content);
        stream.Write(bytes, 0, bytes.Length);
    }
}
