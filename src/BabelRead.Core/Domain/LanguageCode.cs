using System.Globalization;

namespace BabelRead.Core.Domain;

/// <summary>
/// A BCP-47 language code (e.g. "en", "fr", "ar"). A default/empty value means "unknown".
/// </summary>
public readonly record struct LanguageCode(string Code)
{
    /// <summary>Unknown / not-yet-detected language.</summary>
    public static readonly LanguageCode Unknown = new(string.Empty);

    public bool IsUnknown => string.IsNullOrWhiteSpace(Code);

    /// <summary>True when the language is written right-to-left (Arabic, Hebrew, Persian, Urdu, ...).</summary>
    public bool IsRightToLeft
    {
        get
        {
            if (IsUnknown)
            {
                return false;
            }

            try
            {
                return CultureInfo.GetCultureInfo(Code).TextInfo.IsRightToLeft;
            }
            catch (CultureNotFoundException)
            {
                var primary = Code.Split('-')[0].ToLowerInvariant();
                return primary is "ar" or "he" or "fa" or "ur" or "ps" or "sd" or "yi";
            }
        }
    }

    public override string ToString() => IsUnknown ? "(unknown)" : Code;
}
