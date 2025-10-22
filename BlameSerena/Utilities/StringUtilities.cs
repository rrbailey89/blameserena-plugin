using System.Linq;

namespace BlameSerena.Utilities;

/// <summary>
/// String manipulation and sanitization utilities
/// </summary>
public static class StringUtilities
{
    /// <summary>
    /// Remove control characters from string, keeping newlines
    /// </summary>
    public static string Sanitize(string input)
    {
        return new string(input.Where(c => c >= 0x20 || c == '\n').ToArray()).Trim();
    }

    /// <summary>
    /// Check if a string has meaningful content
    /// </summary>
    public static bool HasText(string? s) => !string.IsNullOrEmpty(s);
}
