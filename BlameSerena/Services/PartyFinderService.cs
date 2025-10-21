using System;
using System.Threading.Tasks;
using BlameSerena.Constants;
using BlameSerena.Models;
using BlameSerena.Utilities;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace BlameSerena.Services;

/// <summary>
/// Manages Party Finder listing creation and notification logic
/// </summary>
public interface IPartyFinderService
{
    PartyFinderState State { get; }
    void CaptureStoredRecruitmentInfo();
    Task ProcessAndNotifyAsync(Configuration config, string playerName);
}

public unsafe class PartyFinderService : IPartyFinderService
{
    private readonly IPluginLog _log;
    private readonly IDutyDataService _dutyDataService;
    private readonly INotificationService _notificationService;

    public PartyFinderState State { get; } = new();

    public PartyFinderService(
        IPluginLog log,
        IDutyDataService dutyDataService,
        INotificationService notificationService)
    {
        _log = log;
        _dutyDataService = dutyDataService;
        _notificationService = notificationService;
    }

    /// <summary>
    /// Capture PF data from StoredRecruitmentInfo
    /// </summary>
    public void CaptureStoredRecruitmentInfo()
    {
        var agent = AgentLookingForGroup.Instance();
        if (agent == null)
            return;

        var r = agent->StoredRecruitmentInfo;
        State.TempDutyId = r.SelectedDutyId;
        State.TempComment = GameStructUtilities.HasText(r.CommentString) ? r.CommentString.ToString() : string.Empty;
        State.TempPwdState = r.Password;
        State.TempFlags = (byte)r.DutyFinderSettingFlags;

        var categoryName = _dutyDataService.GetDutyCategory(State.TempDutyId);
        _log.Debug($"Category = '{categoryName}'");
        _log.Debug($"[CaptureStoredRecruitmentInfo] Stored PF data: DutyId={State.TempDutyId}, Comment='{State.TempComment}', PwdState={State.TempPwdState}, Flags={State.TempFlags}");
    }

    /// <summary>
    /// Process the captured listing and send notification if applicable
    /// </summary>
    public async Task ProcessAndNotifyAsync(Configuration config, string playerName)
    {
        var agent = AgentLookingForGroup.Instance();
        ulong currentOwnListingId = (agent != null) ? agent->OwnListingId : 0;

        // Duplicate guard
        if (State.IsDuplicateListing(currentOwnListingId))
        {
            _log.Debug("[ProcessAndNotifyAsync] Duplicate recruit click detected. Skipping send.");
            return;
        }

        string dutyName = _dutyDataService.GetDutyName(State.TempDutyId);
        if (string.IsNullOrEmpty(dutyName))
        {
            _log.Warning($"[ProcessAndNotifyAsync] Could not get duty name for Duty ID {State.TempDutyId}. Using raw ID as fallback.");
            dutyName = $"Duty ID {State.TempDutyId}";
        }

        var category = _dutyDataService.GetDutyCategory(State.TempDutyId);
        _log.Debug($"Category = '{category}'");
        _log.Debug($"[ProcessAndNotifyAsync] Processing listing. DutyName: '{dutyName}', Comment: '{State.TempComment}', PwdState: {State.TempPwdState}, Flags: {State.TempFlags}");

        // Check configuration
        if (!config.EnableNotifications || config.TargetChannelId == 0 || string.IsNullOrEmpty(config.BotApiEndpoint))
        {
            _log.Debug("[ProcessAndNotifyAsync] Notifications disabled or config missing. Skipping.");
            return;
        }

        // Prepare password
        string finalPasswordToSend = string.Empty;
        if (State.TempPwdState != GameConstants.PasswordDisabled)
        {
            finalPasswordToSend = State.TempPwdState.ToString("D4"); // Always 4 digits
            _log.Debug("[ProcessAndNotifyAsync] Using password from PF UI: '{0}'", finalPasswordToSend);
        }
        else
        {
            _log.Debug("[ProcessAndNotifyAsync] PF has password disabled in UI. Sending blank password.");
        }

        _log.Debug($"[ProcessAndNotifyAsync] Preparing to send notification. Player: {playerName}, Duty: {dutyName}, Desc: {State.TempComment}, Category: {category}, PasswordToSend: '{finalPasswordToSend}'");

        await _notificationService.SendPartyFinderNotificationAsync(
            playerName,
            dutyName,
            State.TempComment,
            finalPasswordToSend,
            config.TargetChannelId,
            config.RoleId,
            config.BotApiEndpoint,
            category);

        State.UpdateLastSent(currentOwnListingId);
        _log.Debug($"[ProcessAndNotifyAsync] Updated last sent values. LastLID: {State.LastListingId}, LastDutyID: {State.LastDutyId}, LastCommentHash: {State.LastCommentHash}");
    }
}
