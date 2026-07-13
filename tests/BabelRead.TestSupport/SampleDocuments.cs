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
    /// <summary>Writes a PDF with one page per entry in <paramref name="pageTexts"/>; an empty/whitespace
    /// entry produces a text-less page (to exercise the image-only edge case).</summary>
    public static string CreatePdf(string path, params string[] pageTexts)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        foreach (var text in pageTexts)
        {
            var page = builder.AddPage(595, 842); // A4 in points
            if (!string.IsNullOrWhiteSpace(text))
            {
                page.AddText(text, 12, new PdfPoint(50, 780), font);
            }
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

    private static void WriteEntry(ZipArchive archive, string name, string content, CompressionLevel level = CompressionLevel.Optimal)
    {
        var entry = archive.CreateEntry(name, level);
        using var stream = entry.Open();
        var bytes = Encoding.UTF8.GetBytes(content);
        stream.Write(bytes, 0, bytes.Length);
    }
}
