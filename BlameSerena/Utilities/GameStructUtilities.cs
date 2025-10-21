namespace BlameSerena.Utilities;

/// <summary>
/// Utilities for working with FFXIV client structs
/// </summary>
public static class GameStructUtilities
{
    /// <summary>
    /// Safe check if Utf8String has text content
    /// </summary>
    public static unsafe bool HasText(FFXIVClientStructs.FFXIV.Client.System.String.Utf8String u)
    {
        return u.StringPtr != (byte*)null && u.BufUsed > 1;
    }
}
