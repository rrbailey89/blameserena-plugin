using System;
using System.Linq;
using Dalamud.Plugin.Services;

namespace BlameSerena.Services;

public interface IPartyCheckService
{
    void Update();
}

public class PartyCheckService : IPartyCheckService
{
    private readonly IPluginLog _log;
    private readonly IPartyList _partyList;
    private readonly INotificationService _notificationService;
    private readonly IPlayerState _playerState;
    private readonly Configuration _configuration;
    private bool _hasFired;

    public PartyCheckService(
        IPluginLog log,
        IPartyList partyList,
        INotificationService notificationService,
        IPlayerState playerState,
        Configuration configuration)
    {
        _log = log;
        _partyList = partyList;
        _notificationService = notificationService;
        _playerState = playerState;
        _configuration = configuration;
    }

    public void Update()
    {
        if (!_configuration.EnablePartyCheck || _configuration.ScheduledCheckTime == null || _hasFired)
            return;

        if (DateTime.Now < _configuration.ScheduledCheckTime.Value)
            return;

        _hasFired = true;
        _log.Information("[PartyCheck] Scheduled time reached. Checking party members.");

        var playerName = _playerState.IsLoaded ? _playerState.CharacterName : "Unknown Player";
        var partyMemberNames = _partyList
            .Select(m => m.Name.TextValue)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in _configuration.PartyCheckEntries)
        {
            if (string.IsNullOrEmpty(entry.CharacterName) || string.IsNullOrEmpty(entry.DiscordUserId))
                continue;

            if (!partyMemberNames.Contains(entry.CharacterName))
            {
                _log.Information($"[PartyCheck] {entry.CharacterName} not in party. Sending Discord mention.");
                var partyCheckEndpoint = _configuration.BotApiEndpoint.Replace("/notify/partyfinder", "/notify/partycheck");
                _ = _notificationService.SendPartyCheckNotificationAsync(
                    entry.DiscordUserId,
                    entry.CharacterName,
                    playerName,
                    partyCheckEndpoint,
                    _configuration.TargetChannelId).ConfigureAwait(false);
            }
            else
            {
                _log.Debug($"[PartyCheck] {entry.CharacterName} is in party. No notification needed.");
            }
        }

        // One-shot: disable after firing
        _configuration.EnablePartyCheck = false;
        _configuration.ScheduledCheckTime = null;
        _configuration.Save();
        _log.Information("[PartyCheck] Party check complete. Disabled (one-shot).");
    }
}
