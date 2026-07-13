namespace BabelRead.Core.Documents;

internal static class DocumentIdentity
{
    public static string FromPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var full = Path.GetFullPath(path).Replace('\\', '/');
        return full.ToLowerInvariant();
    }
}
