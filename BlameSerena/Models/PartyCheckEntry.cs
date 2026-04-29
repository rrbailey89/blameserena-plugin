using System;

namespace BlameSerena.Models;

[Serializable]
public class PartyCheckEntry
{
    public string CharacterName { get; set; } = string.Empty;
    public string DiscordUserId { get; set; } = string.Empty;
}
