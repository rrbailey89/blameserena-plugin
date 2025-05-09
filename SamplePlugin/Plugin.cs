using System;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using System.IO;
using System.Threading.Tasks;
using System.Net.Http;
using System.Text.Json;
using System.Text;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using SamplePlugin.Windows;
using Dalamud.Game.Gui.PartyFinder;
using Dalamud.Game.Gui.PartyFinder.Types;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI.Info;

namespace SamplePlugin;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ITextureProvider TextureProvider { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IGameGui GameGui { get; private set; } = null!;
    [PluginService] internal static IAddonLifecycle AddonLifecycle { get; private set; } = null!;
    [PluginService] internal static IPartyFinderGui PartyFinderGui { get; private set; } = null!;

    private const string CommandName = "/blameserena";
    private const string PartyFinderAddonName = "LookingForGroup";
    private const string PartyFinderDetailAddonName = "LookingForGroupDetail";
    private const string PartyFinderConditionAddonName = "LookingForGroupCondition";

    public Configuration Configuration { get; init; }
    public readonly WindowSystem WindowSystem = new("SamplePlugin");
    private ConfigWindow ConfigWindow { get; init; }

    // New state fields for direct agent reading
    private bool isPluginPausedDueToManualPFOpen = false;
    private DateTime lastDirectReadAttempt = DateTime.MinValue;
    private static readonly TimeSpan DirectReadInterval = TimeSpan.FromSeconds(3);

    private uint lastNotifiedDutyId = 0;
    private string lastNotifiedCommentHash = string.Empty;
    private bool playerHasActivePFListing = false;
    private bool notificationSentForCurrentListing = false;
    // New flag to distinguish programmatic vs manual PF window closure
    private bool isCleaningUpProgrammaticCheck = false;

    // Snapshot + wait-for-change fields for LastViewedListing race condition
    private uint snapshotListingId = 0;
    private uint snapshotDutyId = 0;
    private int snapshotCommentHashCode = 0;
    private bool waitingForDataChange = false;
    private int dataChangeCheckFrames = 0;
    private const int MAX_DATA_CHANGE_FRAMES = 60; // ~1 second at 60fps

    // New fields for refined pause logic
    private bool isLookingForGroupOpen = false;
    private bool isLookingForGroupDetailOpen = false;
    private bool isLookingForGroupConditionOpen = false;

    // State machine for background PF check
    private enum PfCheckState
    {
        Idle,
        WaitingForMainPostSetup,
        WaitingForNode,
        WaitingForDetailPostSetup,
        WaitingForData,
        Closing
    }
    private PfCheckState currentPfCheckState = PfCheckState.Idle;
    private DateTime lastPfCheck = DateTime.MinValue;
    private static readonly TimeSpan PfCheckInterval = TimeSpan.FromSeconds(10);
    private int checkRetryCount = 0;
    private const int MAX_CHECK_RETRIES = 10;
    private bool isOpeningPfProgrammatically = false;

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        ConfigWindow = new ConfigWindow(this);
        WindowSystem.AddWindow(ConfigWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Opens the configuration window for BlameSerena PF Notifier."
        });

        PluginInterface.UiBuilder.Draw += DrawUI;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUI;

        Framework.Update += OnFrameworkUpdate;

        AddonLifecycle.RegisterListener(AddonEvent.PostSetup, PartyFinderAddonName, OnPartyFinderAddonLifecycleChange);
        AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, PartyFinderAddonName, OnPartyFinderAddonLifecycleChange);
        AddonLifecycle.RegisterListener(AddonEvent.PostSetup, PartyFinderDetailAddonName, OnPartyFinderDetailAddonLifecycleChange);
        AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, PartyFinderDetailAddonName, OnPartyFinderDetailAddonLifecycleChange);
        AddonLifecycle.RegisterListener(AddonEvent.PostSetup, PartyFinderConditionAddonName, OnPartyFinderConditionAddonLifecycleChange);
        AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, PartyFinderConditionAddonName, OnPartyFinderConditionAddonLifecycleChange);

        Log.Information($"=== {PluginInterface.Manifest.Name} Loaded ===");
        Log.Information($"Monitoring Party Finder agent for own listing.");
    }

    public void Dispose()
    {
        Framework.Update -= OnFrameworkUpdate;
        AddonLifecycle.UnregisterListener(OnPartyFinderAddonLifecycleChange);
        AddonLifecycle.UnregisterListener(OnPartyFinderDetailAddonLifecycleChange);
        AddonLifecycle.UnregisterListener(OnPartyFinderConditionAddonLifecycleChange);

        WindowSystem.RemoveAllWindows();
        ConfigWindow.Dispose();
        CommandManager.RemoveHandler(CommandName);

        Log.Information($"=== {PluginInterface.Manifest.Name} Unloaded ===");
    }

    private void OnCommand(string command, string args) => ToggleConfigUI();
    private void DrawUI() => WindowSystem.Draw();
    public void ToggleConfigUI() => ConfigWindow.Toggle();

    private void OnPartyFinderAddonLifecycleChange(AddonEvent type, AddonArgs args)
    {
        if (type == AddonEvent.PostSetup)
        {
            if (isOpeningPfProgrammatically && currentPfCheckState == PfCheckState.WaitingForMainPostSetup)
            {
                Log.Debug("[PF ADDON] PartyFinder main window opened programmatically (PostSetup). Transitioning to WaitingForNode.");
                currentPfCheckState = PfCheckState.WaitingForNode;
                checkRetryCount = 0;
            }
            else
            {
                isLookingForGroupOpen = true;
                Log.Debug($"[PF ADDON] PartyFinder main window state changed. IsOpen: {isLookingForGroupOpen}");
                UpdatePauseState();
                if (!isLookingForGroupOpen && !isPluginPausedDueToManualPFOpen)
                {
                    notificationSentForCurrentListing = false;
                }
            }
        }
else if (type == AddonEvent.PreFinalize)
{
    Log.Debug($"[PF PREFINALIZE {PartyFinderAddonName}] Start. isOpeningPfProg: {isOpeningPfProgrammatically}, isCleaningUp: {isCleaningUpProgrammaticCheck}, isPausedManualBefore: {isPluginPausedDueToManualPFOpen}");
    isLookingForGroupOpen = false;
    Log.Debug($"[PF ADDON] PartyFinder main window state changed. IsOpen: {isLookingForGroupOpen}");
    UpdatePauseState();
    Log.Debug($"[PF PREFINALIZE {PartyFinderAddonName}] After UpdatePauseState. isPausedManualAfter: {isPluginPausedDueToManualPFOpen}");
    if (isCleaningUpProgrammaticCheck)
    {
        Log.Debug($"[PF PREFINALIZE {PartyFinderAddonName}] Programmatic cleanup detected. Ignoring ResetListingState for this addon.");
        if (currentPfCheckState == PfCheckState.Closing &&
            !isLookingForGroupOpen && !isLookingForGroupDetailOpen && !isLookingForGroupConditionOpen)
        {
            Log.Debug($"[PF PREFINALIZE {PartyFinderAddonName}] All relevant PF windows confirmed closed during programmatic cleanup. Finalizing state.");
            isCleaningUpProgrammaticCheck = false;
            currentPfCheckState = PfCheckState.Idle;
            lastPfCheck = DateTime.UtcNow;
            Log.Debug("[PF CHECK] Global state set to Idle and lastPfCheck updated by " + PartyFinderAddonName);
        }
        else if (currentPfCheckState == PfCheckState.Closing)
        {
            Log.Debug($"[PF PREFINALIZE {PartyFinderAddonName}] Programmatic cleanup ongoing, other windows might still be open or state already Idle. LFG: {isLookingForGroupOpen}, Detail: {isLookingForGroupDetailOpen}, Cond: {isLookingForGroupConditionOpen}");
        }
    }
    else
    {
        if (!isPluginPausedDueToManualPFOpen)
        {
            Log.Information("[PF STATE] Last manually opened PF window closed by user. Resetting full listing state.");
            ResetListingState("User closed last manual PF window");
        }
        else
        {
            Log.Debug($"[PF PREFINALIZE {PartyFinderAddonName}] User closed one manual window, but another is still open.");
        }
    }
}
    }

    private unsafe void OnPartyFinderDetailAddonLifecycleChange(AddonEvent type, AddonArgs args)
    {
        if (type == AddonEvent.PostSetup)
        {
            if (isOpeningPfProgrammatically && currentPfCheckState == PfCheckState.WaitingForDetailPostSetup)
            {
                Log.Debug("[PF ADDON] PartyFinder detail window opened programmatically (PostSetup).");
                
                // Attempt to refresh the addon to get fresh data
                var addonDetail = GameGui.GetAddonByName("LookingForGroupDetail", 1);
                if (addonDetail != IntPtr.Zero)
                {
                    Log.Debug("[PF ADDON DETAIL] Attempting to refresh LookingForGroupDetail.");
                    AtkStage.Instance()->RaptureAtkUnitManager->RefreshAddon((AtkUnitBase*)addonDetail, 0, null);
                }
                else
                {
                    Log.Warning("[PF ADDON DETAIL] Could not get LookingForGroupDetail to refresh.");
                }

                currentPfCheckState = PfCheckState.WaitingForData;
                checkRetryCount = 0;
                waitingForDataChange = true;
                dataChangeCheckFrames = 0;
                Log.Debug("[PF ADDON DETAIL] Transitioning to WaitingForData. Will poll for actual data change from snapshot.");
            }
            else
            {
                isLookingForGroupDetailOpen = true;
                Log.Debug($"[PF ADDON] PartyFinder detail window state changed. IsOpen: {isLookingForGroupDetailOpen}");
                UpdatePauseState();
                if (!isLookingForGroupDetailOpen && !isPluginPausedDueToManualPFOpen)
                {
                    notificationSentForCurrentListing = false;
                }
            }
        }
else if (type == AddonEvent.PreFinalize)
{
    Log.Debug($"[PF PREFINALIZE {PartyFinderDetailAddonName}] Start. isOpeningPfProg: {isOpeningPfProgrammatically}, isCleaningUp: {isCleaningUpProgrammaticCheck}, isPausedManualBefore: {isPluginPausedDueToManualPFOpen}");
    isLookingForGroupDetailOpen = false;
    Log.Debug($"[PF ADDON] PartyFinder detail window state changed. IsOpen: {isLookingForGroupDetailOpen}");
    UpdatePauseState();
    Log.Debug($"[PF PREFINALIZE {PartyFinderDetailAddonName}] After UpdatePauseState. isPausedManualAfter: {isPluginPausedDueToManualPFOpen}");
    if (isCleaningUpProgrammaticCheck)
    {
        Log.Debug($"[PF PREFINALIZE {PartyFinderDetailAddonName}] Programmatic cleanup detected. Ignoring ResetListingState for this addon.");
        if (currentPfCheckState == PfCheckState.Closing &&
            !isLookingForGroupOpen && !isLookingForGroupDetailOpen && !isLookingForGroupConditionOpen)
        {
            Log.Debug($"[PF PREFINALIZE {PartyFinderDetailAddonName}] All relevant PF windows confirmed closed during programmatic cleanup. Finalizing state.");
            isCleaningUpProgrammaticCheck = false;
            currentPfCheckState = PfCheckState.Idle;
            lastPfCheck = DateTime.UtcNow;
            Log.Debug("[PF CHECK] Global state set to Idle and lastPfCheck updated by " + PartyFinderDetailAddonName);
        }
        else if (currentPfCheckState == PfCheckState.Closing)
        {
            Log.Debug($"[PF PREFINALIZE {PartyFinderDetailAddonName}] Programmatic cleanup ongoing, other windows might still be open or state already Idle. LFG: {isLookingForGroupOpen}, Detail: {isLookingForGroupDetailOpen}, Cond: {isLookingForGroupConditionOpen}");
        }
    }
    else
    {
        if (!isPluginPausedDueToManualPFOpen)
        {
            Log.Information("[PF STATE] Last manually opened PF detail window closed by user. Resetting full listing state.");
            ResetListingState("User closed last manual PF detail window");
        }
        else
        {
            Log.Debug($"[PF PREFINALIZE {PartyFinderDetailAddonName}] User closed one manual detail window, but another is still open.");
        }
    }
}
    }

    private void OnPartyFinderConditionAddonLifecycleChange(AddonEvent type, AddonArgs args)
    {
        if (type == AddonEvent.PostSetup)
        {
            if (isOpeningPfProgrammatically)
            {
                Log.Debug("[PF ADDON] PartyFinder condition window opened programmatically. Ignoring for pause state.");
            }
            else
            {
                isLookingForGroupConditionOpen = true;
                Log.Debug($"[PF ADDON] PartyFinder condition window state changed. IsOpen: {isLookingForGroupConditionOpen}");
                UpdatePauseState();
                if (!isLookingForGroupConditionOpen && !isPluginPausedDueToManualPFOpen)
                {
                    notificationSentForCurrentListing = false;
                }
            }
        }
else if (type == AddonEvent.PreFinalize)
{
    Log.Debug($"[PF PREFINALIZE {PartyFinderConditionAddonName}] Start. isOpeningPfProg: {isOpeningPfProgrammatically}, isCleaningUp: {isCleaningUpProgrammaticCheck}, isPausedManualBefore: {isPluginPausedDueToManualPFOpen}");
    isLookingForGroupConditionOpen = false;
    Log.Debug($"[PF ADDON] PartyFinder condition window state changed. IsOpen: {isLookingForGroupConditionOpen}");
    UpdatePauseState();
    Log.Debug($"[PF PREFINALIZE {PartyFinderConditionAddonName}] After UpdatePauseState. isPausedManualAfter: {isPluginPausedDueToManualPFOpen}");
    if (isCleaningUpProgrammaticCheck)
    {
        Log.Debug($"[PF PREFINALIZE {PartyFinderConditionAddonName}] Programmatic cleanup detected. Ignoring ResetListingState for this addon.");
        if (currentPfCheckState == PfCheckState.Closing &&
            !isLookingForGroupOpen && !isLookingForGroupDetailOpen && !isLookingForGroupConditionOpen)
        {
            Log.Debug($"[PF PREFINALIZE {PartyFinderConditionAddonName}] All relevant PF windows confirmed closed during programmatic cleanup. Finalizing state.");
            isCleaningUpProgrammaticCheck = false;
            currentPfCheckState = PfCheckState.Idle;
            lastPfCheck = DateTime.UtcNow;
            Log.Debug("[PF CHECK] Global state set to Idle and lastPfCheck updated by " + PartyFinderConditionAddonName);
        }
        else if (currentPfCheckState == PfCheckState.Closing)
        {
            Log.Debug($"[PF PREFINALIZE {PartyFinderConditionAddonName}] Programmatic cleanup ongoing, other windows might still be open or state already Idle. LFG: {isLookingForGroupOpen}, Detail: {isLookingForGroupDetailOpen}, Cond: {isLookingForGroupConditionOpen}");
        }
    }
    else
    {
        if (!isPluginPausedDueToManualPFOpen)
        {
            Log.Information("[PF STATE] Last manually opened PF condition window closed by user. Resetting full listing state.");
            ResetListingState("User closed last manual PF condition window");
        }
        else
        {
            Log.Debug($"[PF PREFINALIZE {PartyFinderConditionAddonName}] User closed one manual condition window, but another is still open.");
        }
    }
}
    }

    private void UpdatePauseState()
    {
        bool previouslyPaused = isPluginPausedDueToManualPFOpen;
        isPluginPausedDueToManualPFOpen = isLookingForGroupOpen || isLookingForGroupDetailOpen || isLookingForGroupConditionOpen;

        if (isPluginPausedDueToManualPFOpen && !previouslyPaused)
        {
            Log.Debug($"[PF PAUSE] Plugin PAUSED. Main: {isLookingForGroupOpen}, Detail: {isLookingForGroupDetailOpen}, Condition: {isLookingForGroupConditionOpen}");
        }
        else if (!isPluginPausedDueToManualPFOpen && previouslyPaused)
        {
            Log.Debug($"[PF PAUSE] Plugin RESUMED. Main: {isLookingForGroupOpen}, Detail: {isLookingForGroupDetailOpen}, Condition: {isLookingForGroupConditionOpen}");
            notificationSentForCurrentListing = false;
        }
    }

    private unsafe void OnFrameworkUpdate(IFramework framework)
    {
        if (ClientState.LocalPlayer == null || ClientState.LocalContentId == 0)
            return;

        if (isPluginPausedDueToManualPFOpen)
        {
            if (currentPfCheckState != PfCheckState.Idle)
            {
                Log.Debug("[PF CHECK] User opened PF window, cancelling background check.");
                CancelPfCheck();
            }
            return;
        }

        switch (currentPfCheckState)
        {
            case PfCheckState.Idle:
                if ((DateTime.UtcNow - lastPfCheck) >= PfCheckInterval)
                {
                    Log.Debug("[PF CHECK] Starting background check.");
                    var agentLfg = AgentLookingForGroup.Instance();
                    if (agentLfg == null)
                    {
                        Log.Warning("[PF CHECK] Agent not found.");
                        return;
                    }
                    isOpeningPfProgrammatically = true;
                    agentLfg->Show();
                    Log.Debug("[PF CHECK] Agent->Show() called. Waiting for AddonLifecycle PostSetup event.");
                    currentPfCheckState = PfCheckState.WaitingForMainPostSetup;
                }
                break;

            case PfCheckState.WaitingForMainPostSetup:
                // Do nothing here; wait for AddonLifecycle event to transition state.
                break;

            case PfCheckState.WaitingForNode:
                var addonPtr = GameGui.GetAddonByName(PartyFinderAddonName, 1);
                if (addonPtr == IntPtr.Zero)
                {
                    checkRetryCount++;
                    Log.Debug($"[PF CHECK] Waiting for addon... Retry {checkRetryCount}/{MAX_CHECK_RETRIES}");
                    if (checkRetryCount > MAX_CHECK_RETRIES) CancelPfCheck("Addon never appeared");
                    return;
                }

                var unitBase = (AtkUnitBase*)addonPtr;
                var node46 = unitBase->GetNodeById(46);
                if (node46 != null && node46->IsVisible())
                {
                    var componentNode46 = (AtkComponentNode*)node46;
                    AtkTextNode* targetTextNode = null;
                    var buttonComponent = componentNode46->Component;
                    var atkComponentButton = (AtkComponentButton*)buttonComponent;
                    if (atkComponentButton != null)
                    {
                        var uldManager = &atkComponentButton->UldManager;
                        if (uldManager->LoadedState == AtkLoadState.Loaded)
                        {
                            var foundNode = uldManager->SearchNodeById(2);
                            if (foundNode != null && foundNode->Type == NodeType.Text)
                            {
                                targetTextNode = (AtkTextNode*)foundNode;
                                Log.Debug($"[PF CHECK] Found child Text Node with ID 2 via Component UldManager.");
                            }
                            else
                            {
                                Log.Debug($"[PF CHECK] Child Text Node 2 not found in Component UldManager (ID: 2, Type: Text). FoundNode Type: {(foundNode != null ? foundNode->Type : NodeType.Res)}");
                            }
                        }
                        else
                        {
                            Log.Debug($"[PF CHECK] Component UldManager not loaded yet (State: {uldManager->LoadedState}). Retrying.");
                        }
                    }
                    if (targetTextNode != null)
                    {
                        string nodeText = targetTextNode->NodeText.ToString();
                        Log.Debug($"[PF CHECK] Child Text Node 2 text: '{nodeText}'");
                        bool uiIndicatesActive = nodeText.Contains("Criteria");
                        if (uiIndicatesActive)
                        {
                            Log.Debug("[PF CHECK] Child Text Node 2 indicates active PF. Clicking Node 46...");
                            AtkValue arg;
                            arg.Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int;
                            arg.Int = 14;
                            unitBase->FireCallback(1, &arg);
                            Log.Debug("[PF CHECK] Clicked Node 46 (Callback 14). Waiting for PartyFinderDetail PostSetup event.");
                            currentPfCheckState = PfCheckState.WaitingForDetailPostSetup;
                            checkRetryCount = 0;
                        }
                        else
                        {
                            Log.Debug("[PF CHECK] Child Text Node 2 indicates INACTIVE PF. Cleaning up.");
                            if (playerHasActivePFListing) ResetListingState("PF Check found inactive text node 2");
                            CancelPfCheck();
                        }
                    }
                    else
                    {
                        checkRetryCount++;
                        Log.Debug($"[PF CHECK] Waiting for Child Text Node 2 in Component UldManager... Retry {checkRetryCount}/{MAX_CHECK_RETRIES}");
                        if (checkRetryCount > MAX_CHECK_RETRIES) CancelPfCheck("Child Text Node 2 (Component Uld) timeout");
                    }
                }
                else
                {
                    checkRetryCount++;
                    Log.Debug($"[PF CHECK] Waiting for Node 46 visibility... Retry {checkRetryCount}/{MAX_CHECK_RETRIES}");
                    if (checkRetryCount > MAX_CHECK_RETRIES) CancelPfCheck("Node 46 timeout");
                }
                break;

            case PfCheckState.WaitingForDetailPostSetup:
                // No polling here; OnPartyFinderDetailAddonLifecycleChange will handle the transition.
                break;

            case PfCheckState.WaitingForData:
                if (!waitingForDataChange) // This case should only be active if we are waiting for data change
                {
                    // This might happen if something went wrong and we entered this state without setting the flag
                    // or if CancelPfCheck was called and reset waitingForDataChange, but OnFrameworkUpdate ran again for this state.
                    Log.Warning("[PF CHECK WD] Entered WaitingForData but not actively waiting for data change. Resetting to Idle or handling legacy.");
                    // Attempt legacy check once as a fallback, then cancel.
                    var agentLfgLegacy = AgentLookingForGroup.Instance();
                    if (agentLfgLegacy != null) {
                        var localContentIdLegacy = ClientState.LocalContentId;
                        bool isOurListingLegacy = agentLfgLegacy->LastViewedListing.LeaderContentId == localContentIdLegacy && localContentIdLegacy != 0;
                        uint currentListingIdLegacy = agentLfgLegacy->LastViewedListing.ListingId;
                        if (currentListingIdLegacy != 0 && isOurListingLegacy) {
                            Log.Debug($"[PF CHECK WD] (Legacy Fallback) Processing LastViewed match: DutyId={agentLfgLegacy->LastViewedListing.DutyId}");
                            ProcessListingDataFromLastViewed(agentLfgLegacy);
                        }
                    }
                    CancelPfCheck("WaitingForData unexpected entry");
                    return; // Important to exit to prevent further processing in this state for this frame
                }

                dataChangeCheckFrames++;
                var agentLfgDataCurrent = AgentLookingForGroup.Instance();

                if (agentLfgDataCurrent == null)
                {
                    Log.Warning("[PF CHECK WD] Agent disappeared while waiting for data change.");
                    CancelPfCheck("Agent disappeared in WaitingForData poll");
                    waitingForDataChange = false;
                    return;
                }

                var currentLocalContentId = ClientState.LocalContentId;
                bool currentIsOurListing = agentLfgDataCurrent->LastViewedListing.LeaderContentId == currentLocalContentId && currentLocalContentId != 0;
                uint currentListingIdNow = agentLfgDataCurrent->LastViewedListing.ListingId;
                uint currentDutyIdNow = agentLfgDataCurrent->LastViewedListing.DutyId;
                int currentCommentHashCodeNow = agentLfgDataCurrent->LastViewedListing.CommentString.ToString().GetHashCode();

                bool actualDataHasChanged = (currentListingIdNow != snapshotListingId ||
                                           currentDutyIdNow != snapshotDutyId ||
                                           currentCommentHashCodeNow != snapshotCommentHashCode);
                
                Log.Debug($"[PF CHECK WD] Poll frame {dataChangeCheckFrames}. OurListing: {currentIsOurListing}. SnapLID: {snapshotListingId}, CurLID: {currentListingIdNow}. SnapDID: {snapshotDutyId}, CurDID: {currentDutyIdNow}. SnapCHash: {snapshotCommentHashCode}, CurCHash: {currentCommentHashCodeNow}. Changed: {actualDataHasChanged}");

                if (currentIsOurListing && ((snapshotListingId == 0 && currentListingIdNow != 0) || actualDataHasChanged))
                {
                    Log.Debug($"[PF CHECK WD] Data changed/updated from snapshot. Processing.");
                    ProcessListingDataFromLastViewed(agentLfgDataCurrent);
                    CancelPfCheck("Data changed/updated from snapshot");
                    waitingForDataChange = false;
                }
                else if (dataChangeCheckFrames > MAX_DATA_CHANGE_FRAMES)
                {
                    Log.Warning($"[PF CHECK WD] Timed out waiting for data to change from snapshot after {dataChangeCheckFrames} frames.");
                    if (currentIsOurListing) // If it's still our listing, even if unchanged from snapshot
                    {
                        Log.Debug("[PF CHECK WD] Data matches snapshot after timeout, or was initially our listing. Processing current data.");
                        ProcessListingDataFromLastViewed(agentLfgDataCurrent);
                    }
                    else if (snapshotListingId == 0 && currentListingIdNow == 0) // Started with no listing, ended with no listing
                    {
                        Log.Debug("[PF CHECK WD] Snapshot and current listing ID are both 0 after timeout. No active listing.");
                        if (playerHasActivePFListing) ResetListingState("PF Check found inactive after timeout (LID 0)");
                    }
                    CancelPfCheck("Data change poll timeout");
                    waitingForDataChange = false;
                }
                // If no conditions met, continue polling on the next OnFrameworkUpdate tick.
                break;

            case PfCheckState.Closing:
                var agentLfgClose = AgentLookingForGroup.Instance();
                if (agentLfgClose != null) agentLfgClose->Hide();
                Log.Debug("[PF CHECK] Agent->Hide() called.");
                currentPfCheckState = PfCheckState.Idle;
                lastPfCheck = DateTime.UtcNow;
                isOpeningPfProgrammatically = false;
                Log.Debug("[PF CHECK] Cleared programmatic open flag.");
                break;
        }
    }

    private unsafe void CancelPfCheck(string reason = "Normal cleanup")
    {
        Log.Debug($"[PF CHECK] Cancelling check ({reason}). Setting state to Closing.");
        isCleaningUpProgrammaticCheck = true; // Signal that the upcoming PreFinalize events are due to us
        currentPfCheckState = PfCheckState.Closing;
        isOpeningPfProgrammatically = false;
        waitingForDataChange = false; // Reset this flag whenever a check is cancelled
        dataChangeCheckFrames = 0;    // And its counter

        var agentLfgClose = AgentLookingForGroup.Instance();
        if (agentLfgClose != null) agentLfgClose->Hide();
        Log.Debug("[PF CHECK] Agent->Hide() called during cancel.");
        // Do NOT set currentPfCheckState = Idle or lastPfCheck here; let PreFinalize handle it
        Log.Debug("[PF CHECK] Programmatic cleanup flag set. Waiting for PreFinalize to finalize state.");
    }

    private unsafe void ProcessListingDataFromLastViewed(AgentLookingForGroup* agentLfg)
    {
        uint dutyId = agentLfg->LastViewedListing.DutyId;
        uint listingId = agentLfg->LastViewedListing.ListingId;
        string commentString = agentLfg->LastViewedListing.CommentString;

        Log.Debug($"[PROCESS DATA] Entry. Current DutyId: {dutyId}, ListingId: {listingId}, Comment: '{commentString}'.");
        Log.Debug($"[PROCESS DATA] State BEFORE notify check: hasActiveListing={playerHasActivePFListing}, lastDutyId={lastNotifiedDutyId}, lastCommentHash='{lastNotifiedCommentHash}', sentForCurrent={notificationSentForCurrentListing}");

        string currentDescription = !string.IsNullOrWhiteSpace(Configuration.Description)
            ? Configuration.Description
            : (!string.IsNullOrWhiteSpace(commentString) ? commentString : string.Empty);

        string currentCommentHash = currentDescription ?? string.Empty;

        if (!playerHasActivePFListing ||
            dutyId != lastNotifiedDutyId ||
            currentCommentHash != lastNotifiedCommentHash ||
            !notificationSentForCurrentListing)
        {
            string reason = "";
            if (!playerHasActivePFListing) reason += "No active listing. ";
            if (dutyId != lastNotifiedDutyId) reason += $"DutyId changed ({lastNotifiedDutyId} -> {dutyId}). ";
            if (currentCommentHash != lastNotifiedCommentHash) reason += "Comment changed. ";
            if (!notificationSentForCurrentListing) reason += "Notification not yet sent for this listing. ";
            Log.Debug($"[PROCESS DATA] Notify condition MET. Reason: {reason.Trim()}");

            string dutyName = "";
            var dutySheet = DataManager.GetExcelSheet<ContentFinderCondition>();
            if (dutySheet != null)
            {
                var entry = dutySheet.GetRow(dutyId);
                if (entry.RowId == dutyId) dutyName = entry.Name.ToString();
            }
            var passwordState = agentLfg->StoredRecruitmentInfo.Password;
            var dutyFinderSettings = (byte)agentLfg->LastViewedListing.DutyFinderSettingFlags;

            ProcessAndNotifyStoredListing(dutyName, currentDescription, passwordState, dutyFinderSettings);

            lastNotifiedDutyId = dutyId;
            lastNotifiedCommentHash = currentCommentHash;
            playerHasActivePFListing = true;
            notificationSentForCurrentListing = true;

            Log.Debug($"[PROCESS DATA] State AFTER notify: lastDutyId={lastNotifiedDutyId}, lastCommentHash='{lastNotifiedCommentHash}', sentForCurrent={notificationSentForCurrentListing}, hasActiveListing={playerHasActivePFListing}");
        }
        else
        {
            Log.Debug($"[PROCESS DATA] Condition NOT MET for notification.");
            playerHasActivePFListing = true;
        }
    }

    private void ResetListingState(string reason)
    {
        if (playerHasActivePFListing || notificationSentForCurrentListing)
        {
            Log.Information($"[PF STATE] Resetting listing state ({reason}).");
            playerHasActivePFListing = false;
            notificationSentForCurrentListing = false;
            lastNotifiedDutyId = 0;
            lastNotifiedCommentHash = string.Empty;
        }
    }

    // ... rest of the code (ProcessAndNotifyStoredListing, SendPartyFinderNotificationAsync) unchanged ...
    private void ProcessAndNotifyStoredListing(string dutyName, string description, ushort gamePasswordState, byte dutyFinderSettings)
    {
        Log.Debug($"[DEBUG PROCESS] ProcessAndNotifyStoredListing called. Duty: '{dutyName}', Desc: '{description}', PassState: {gamePasswordState}, Settings: {dutyFinderSettings}");

        if (!Configuration.EnableNotifications || Configuration.TargetChannelId == 0 || string.IsNullOrEmpty(Configuration.BotApiEndpoint))
        {
            Log.Debug("[DEBUG PROCESS] Notifications disabled or config missing. Skipping.");
            return;
        }

        bool minIlvlFlagSet = (dutyFinderSettings & 0x2) != 0;
        bool silenceEchoFlagSet = (dutyFinderSettings & 0x4) != 0;
        bool passesMinIlvlFlag = !Configuration.FilterRequireMinIlvl || minIlvlFlagSet;
        bool passesSilenceEchoFlag = !Configuration.FilterRequireSilenceEcho || silenceEchoFlagSet;

        Log.Debug($"[DEBUG PROCESS] Filter results: MinIlvlOK={passesMinIlvlFlag}, SilenceEchoOK={passesSilenceEchoFlag}");

        if (!passesMinIlvlFlag || !passesSilenceEchoFlag)
        {
            Log.Debug("[DEBUG PROCESS] Listing does not meet filter criteria. Notification skipped.");
            return;
        }

        var playerName = ClientState.LocalPlayer?.Name.TextValue ?? "Unknown Player";
        var partyFinderPassword = Configuration.PartyFinderPassword ?? string.Empty;

        bool isPasswordProtectedInGame = gamePasswordState != 0 && gamePasswordState != 10000;

        if (isPasswordProtectedInGame && string.IsNullOrEmpty(partyFinderPassword))
        {
            Log.Debug("[DEBUG PROCESS] Listing is password protected in-game, but no password is set in plugin config. Sending without password in payload.");
        }
        else if (!isPasswordProtectedInGame && !string.IsNullOrEmpty(partyFinderPassword))
        {
            Log.Debug("[DEBUG PROCESS] Listing is NOT password protected in-game, but a password IS set in plugin config. Sending with configured password.");
        }

        Log.Debug($"[DEBUG PROCESS] Preparing to send notification. Player: {playerName}, Duty: {dutyName}, Desc: {description}");
        _ = SendPartyFinderNotificationAsync(playerName, dutyName, description, partyFinderPassword, Configuration.TargetChannelId, Configuration.RoleId, Configuration.BotApiEndpoint);
    }

    private async Task SendPartyFinderNotificationAsync(string playerName, string dutyName, string description, string partyFinderPassword, ulong channelId, ulong roleId, string apiEndpoint)
    {
        try
        {
            using var httpClient = new HttpClient();
            httpClient.Timeout = TimeSpan.FromSeconds(15);
            var payload = new { PlayerName = playerName, DutyName = dutyName, Description = description, PartyFinderPassword = partyFinderPassword, ChannelId = channelId, RoleId = roleId };
            var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = false });
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            Log.Information($"[HTTP SEND] Attempting to send PF notification. Payload: {json}");
            var response = await httpClient.PostAsync(apiEndpoint, content);
            if (response.IsSuccessStatusCode)
            {
                Log.Information($"[HTTP SEND] Party Finder notification sent successfully for: {playerName} - {dutyName}");
            }
            else
            {
                string r = await response.Content.ReadAsStringAsync();
                Log.Error($"[HTTP SEND] Failed to send PF notification. Status: {response.StatusCode} ({response.ReasonPhrase}). Endpoint: {apiEndpoint}. Response: {r}");
            }
        }
        catch (HttpRequestException ex) { Log.Error(ex, $"[HTTP SEND] HTTP Request Exception to {apiEndpoint}."); }
        catch (TaskCanceledException ex) { Log.Error(ex, $"[HTTP SEND] Task Canceled (Timeout?) to {apiEndpoint}."); }
        catch (Exception ex) { Log.Error(ex, $"[HTTP SEND] General Exception to {apiEndpoint}."); }
    }
}
