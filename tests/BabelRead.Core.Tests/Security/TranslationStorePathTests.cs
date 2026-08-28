using BabelRead.Core.Translation;
using Xunit;

namespace BabelRead.Core.Tests.Security;

/// <summary>The translation store names files after the document id, and a document id is just the path
/// of a file the reader opened — attacker-influenced whenever the book came from outside. Every derived
/// path must stay inside the store directory, or opening a crafted book writes wherever it likes.</summary>
[Trait("Category", "Security")]
public sealed class TranslationStorePathTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("babelread-store-paths").FullName;

    public static TheoryData<string> HostileDocumentIds =>
    [
        "../../../etc/passwd",
        "..\\..\\..\\Windows\\System32\\drivers\\etc\\hosts",
        "..",
        ".",
        "/etc/shadow",
        "C:\\Windows\\..\\Windows\\win.ini",
        "~/.ssh/authorized_keys",
        "book\n../../escape",
        "book/../../../escape",
        "....//....//escape",
        new string('a', 4000) + "/../../escape",
    ];

    [Theory]
    [MemberData(nameof(HostileDocumentIds))]
    public void Derived_path_never_escapes_the_store_directory(string documentId)
    {
        var store = new JsonTranslationStore(_dir);

        var path = Path.GetFullPath(store.FilePathFor(documentId));
        var root = Path.GetFullPath(_dir) + Path.DirectorySeparatorChar;

        Assert.StartsWith(root, path, StringComparison.Ordinal);
        Assert.Equal(Path.GetFullPath(_dir), Path.GetDirectoryName(path));
    }

    [Theory]
    [MemberData(nameof(HostileDocumentIds))]
    public void Derived_file_name_carries_no_separator_or_traversal_segment(string documentId)
    {
        var store = new JsonTranslationStore(_dir);

        var name = Path.GetFileName(store.FilePathFor(documentId));

        Assert.DoesNotContain("..", name, StringComparison.Ordinal);
        Assert.DoesNotContain('/', name);
        Assert.DoesNotContain('\\', name);
        Assert.EndsWith(".json", name, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_different_documents_never_collide_on_one_file()
    {
        var store = new JsonTranslationStore(_dir);

        // Both sanitize to the same visible name; only the id hash keeps them apart.
        var a = store.FilePathFor("../../../etc/passwd");
        var b = store.FilePathFor("/var/tmp/passwd");

        Assert.NotEqual(a, b);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);
}
