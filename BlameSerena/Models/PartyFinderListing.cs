namespace BlameSerena.Models;

/// <summary>
/// Represents a Party Finder listing data
/// </summary>
public class PartyFinderListing
{
    public string PlayerName { get; set; } = string.Empty;
    public string DutyName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public byte DutyFinderSettings { get; set; }
}
