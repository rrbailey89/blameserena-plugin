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
using System.Linq;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using SamplePlugin.Windows;
using Dalamud.Game.Gui.PartyFinder;
using Dalamud.Game.Gui.PartyFinder.Types;
using FFXIVClientStructs.FFXIV.Component.GUI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI; // <-- Add this for AddonLookingForGroupDetail
using Lumina.Excel.Sheets;

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
    [PluginService] internal static IPartyFinderGui PartyFinderGui { get; private set; } = null!;
    [PluginService] internal static IGameGui GameGui { get; private set; } = null!;
    [PluginService] internal static IAddonLifecycle AddonLifecycle { get; private set; } = null!;

    private const string CommandName = "/blameserena";
    public Configuration Configuration { get; init; }
    public readonly WindowSystem WindowSystem = new("SamplePlugin");
    private ConfigWindow ConfigWindow { get; init; }

    private ulong? lastNotifiedPlayerListingGameId = null;
    private bool playerHadActiveListingRecently = false;
    private bool uiIndicatesPlayerPFActive = false;
    private bool isPartyFinderAddonOpen = false;
    private bool hasSentPayloadFlag = false;
    private bool hasAttemptedClickFlag = false;
    private DateTime lastPoll = DateTime.MinValue;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    private const uint LFG_RECRUIT_BUTTON_NODE_ID = 46; // The AtkComponentButton node ID
    private const string PartyFinderAddonName = "LookingForGroup";


    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        var goatImagePath = Path.Combine(PluginInterface.AssemblyLocation.Directory?.FullName!, "goat.png"); // Not used in ConfigWindow, but keep if MainWindow is used
        ConfigWindow = new ConfigWindow(this);
        WindowSystem.AddWindow(ConfigWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Opens the configuration window for BlameSerena PF Notifier."
        });

        PluginInterface.UiBuilder.Draw += DrawUI;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUI;

        PartyFinderGui.ReceiveListing += OnPartyFinderListingReceived;
        Framework.Update += OnFrameworkUpdate;

        AddonLifecycle.RegisterListener(AddonEvent.PostSetup, PartyFinderAddonName, OnPartyFinderAddonLifecycleChange);
        AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, PartyFinderAddonName, OnPartyFinderAddonLifecycleChange);


        Log.Information($"=== {PluginInterface.Manifest.Name} Loaded ===");
        Log.Information($"Monitoring Party Finder addon: {PartyFinderAddonName}, Button Node ID: {LFG_RECRUIT_BUTTON_NODE_ID}");
    }

    public void Dispose()
    {
        Framework.Update -= OnFrameworkUpdate;
        PartyFinderGui.ReceiveListing -= OnPartyFinderListingReceived;
        AddonLifecycle.UnregisterListener(OnPartyFinderAddonLifecycleChange); 

        WindowSystem.RemoveAllWindows();
        ConfigWindow.Dispose();
        CommandManager.RemoveHandler(CommandName);

        Log.Information($"=== {PluginInterface.Manifest.Name} Unloaded ===");
    }

private enum PfPollInternalState { Idle, WaitingForMainPFNodes, WaitingForDetailAddonOpen, FinalizeAndCleanup }
private PfPollInternalState pfPollState = PfPollInternalState.Idle;
private unsafe AgentInterface* pfBackgroundPollAgent = null;
private int mainPFNodeRetryCount = 0;
private int detailOpenRetryCount = 0;
private const int MAX_MAIN_PF_NODE_RETRIES = 5;
private const int MAX_DETAIL_OPEN_RETRIES = 5;

// Step 1: Show LFG agent and wait for button node to be available
private unsafe void Perform_ShowMainPFAndWaitForNodes()
{
    if (pfBackgroundPollAgent == null)
    {
        var mod = AgentModule.Instance();
        if (mod == null)
        {
            Log.Debug("[PF BG] AgentModule.Instance() returned null.");
            pfPollState = PfPollInternalState.FinalizeAndCleanup;
            return;
        }

        var agentPtr = mod->GetAgentByInternalId(AgentId.LookingForGroup);
        if (agentPtr == null)
        {
            Log.Debug("[PF BG] mod->GetAgentByInternalId returned null.");
            pfPollState = PfPollInternalState.FinalizeAndCleanup;
            return;
        }

        pfBackgroundPollAgent = (AgentInterface*)agentPtr;
        pfBackgroundPollAgent->Show();
        Log.Debug("[PF BG] agent->Show() called, waiting for main PF nodes.");
        mainPFNodeRetryCount = 0;
        lastPoll = DateTime.UtcNow;
    }

    var addonPtr = GameGui.GetAddonByName(PartyFinderAddonName, 1);
    Log.Debug($"[PF BG] (mainPF) GameGui.GetAddonByName returned: 0x{addonPtr.ToInt64():X}");

    if (addonPtr != IntPtr.Zero)
    {
        var unitBase = (AtkUnitBase*)addonPtr;
        Log.Debug($"[PF BG] (mainPF) unitBase->IsVisible: {unitBase->IsVisible}");

        var buttonNode = unitBase->GetNodeById(LFG_RECRUIT_BUTTON_NODE_ID);
        Log.Debug($"[PF BG] (mainPF) buttonNode: 0x{((ulong)buttonNode):X}");

        if (buttonNode != null)
        {
            Log.Debug($"[PF BG] (mainPF) buttonNode->IsVisible(): {buttonNode->IsVisible()}");
        }

        if (buttonNode != null && (ushort)buttonNode->Type >= 1000)
        {
            AtkValue param;
            param.Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int;
            param.Int  = 14;

            Log.Debug("[PF BG] (mainPF) About to call unitBase->FireCallback(1, &param, false)");
            try
            {
                unitBase->FireCallback(1, &param, false);
                Log.Information("[PF CLICK] (mainPF) Fired callback 14 to open detail.");
                hasAttemptedClickFlag = true;
                Log.Debug("[PF BG] (mainPF) unitBase->FireCallback completed successfully.");
                pfPollState = PfPollInternalState.WaitingForDetailAddonOpen;
                detailOpenRetryCount = 0;
                return;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[PF BG] (mainPF) Exception during unitBase->FireCallback.");
                pfPollState = PfPollInternalState.FinalizeAndCleanup;
                return;
            }
        }
        else
        {
            Log.Debug("[PF BG] (mainPF) buttonNode is null or not a component node.");
        }
    }
    else
    {
        Log.Debug("[PF BG] (mainPF) addonPtr is IntPtr.Zero.");
    }

    mainPFNodeRetryCount++;
    if (mainPFNodeRetryCount >= MAX_MAIN_PF_NODE_RETRIES)
    {
        Log.Warning("[PF BG] (mainPF) Timed out waiting for main PF button node.");
        pfPollState = PfPollInternalState.FinalizeAndCleanup;
    }
}

// Step 2: Wait for detail addon, extract data, notify, then cleanup
private bool isWaitingForDetailTextPopulation = false;

private unsafe void Perform_AttemptReadFromDetail()
{
    var detailPtr = GameGui.GetAddonByName("LookingForGroupDetail", 1);
    Log.Debug($"[PF BG] (detail) GameGui.GetAddonByName('LookingForGroupDetail') returned: 0x{detailPtr.ToInt64():X}");

    if (detailPtr != IntPtr.Zero)
    {
        var unitBase = (AtkUnitBase*)detailPtr;
        Log.Debug($"[PF BG] (detail) unitBase->IsVisible: {unitBase->IsVisible}");

        if (!isWaitingForDetailTextPopulation)
        {
            Log.Debug("[PF BG] (detail) LookingForGroupDetail found, waiting one frame for text to populate.");
            isWaitingForDetailTextPopulation = true;
            return;
        }

        isWaitingForDetailTextPopulation = false;
        string dutyName = "";
        string description = "";

        var addonDetailStruct = (AddonLookingForGroupDetail*)detailPtr;

        // Try direct text node pointers first
        if (addonDetailStruct->DutyNameTextNode != null)
        {
            dutyName = addonDetailStruct->DutyNameTextNode->NodeText.ToString();
            Log.Debug($"[PF BG] (detail) DutyName from DutyNameTextNode: '{dutyName}'");
        }
        else
        {
            Log.Debug("[PF BG] (detail) addonDetailStruct->DutyNameTextNode is null.");
        }

        if (addonDetailStruct->DescriptionTextNode != null)
        {
            description = addonDetailStruct->DescriptionTextNode->NodeText.ToString();
            Log.Debug($"[PF BG] (detail) Description from DescriptionTextNode: '{description}'");
        }
        else
        {
            Log.Debug("[PF BG] (detail) addonDetailStruct->DescriptionTextNode is null.");
        }

        // Fallback: Try AgentLookingForGroup.LastViewedListing if either is blank
        if ((string.IsNullOrWhiteSpace(dutyName) || string.IsNullOrWhiteSpace(description)) && pfBackgroundPollAgent != null)
        {
            var agentLFG = (AgentLookingForGroup*)pfBackgroundPollAgent;
            var lastViewed = agentLFG->LastViewedListing;
            Log.Debug($"[PF BG] (detail) Agent.LastViewedListing - DutyId: {lastViewed.DutyId}, ListingId: {lastViewed.ListingId}");

            if (string.IsNullOrWhiteSpace(dutyName) && lastViewed.DutyId > 0)
            {
                var dutySheet = DataManager.GetExcelSheet<ContentFinderCondition>();
                if (dutySheet != null)
                {
                    // GetRow always returns a ContentFinderCondition struct—even if it's "missing",
                    // you just get default(ContentFinderCondition) with RowId = 0.
                    var entry = dutySheet.GetRow(lastViewed.DutyId);

                    // If it actually existed in the sheet, entry.RowId will match:
                    if (entry.RowId == lastViewed.DutyId)
                    {
                        dutyName = entry.Name.ToString();
                        Log.Debug(
                            $"[PF BG] (detail) DutyName from Agent.LastViewedListing.DutyId ({lastViewed.DutyId}): '{dutyName}'");
                    }
                    else
                    {
                        Log.Debug(
                            $"[PF BG] (detail) Could not find duty name for DutyId {lastViewed.DutyId} from agent.");
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(description))
            {
                var commentSpan = new Span<byte>(lastViewed.Comment.ToArray());
                int commentLength = 0;
                while (commentLength < commentSpan.Length && commentSpan[commentLength] != 0)
                {
                    commentLength++;
                }
                if (commentLength > 0)
                {
                    description = Encoding.UTF8.GetString(commentSpan.Slice(0, commentLength));
                    Log.Debug($"[PF BG] (detail) Description from Agent.LastViewedListing.Comment: '{description}'");
                }
                else
                {
                    Log.Debug($"[PF BG] (detail) Agent.LastViewedListing.Comment is empty.");
                }
            }
        }

        Log.Information($"[PF BG] (detail) Final Extracted Duty: '{dutyName}', Description: '{description}'");

        ProcessAndNotifyExtractedData(dutyName, description);

        pfPollState = PfPollInternalState.FinalizeAndCleanup;
        return;
    }
    else
    {
        detailOpenRetryCount++;
        Log.Debug($"[PF BG] (detail) Detail addon not found, retry {detailOpenRetryCount}/{MAX_DETAIL_OPEN_RETRIES}");
        if (detailOpenRetryCount >= MAX_DETAIL_OPEN_RETRIES)
        {
            Log.Warning("[PF BG] (detail) Timed out waiting for LookingForGroupDetail to open.");
            pfPollState = PfPollInternalState.FinalizeAndCleanup;
        }
    }
}

// Step 3: Hide LFG agent and cleanup state
private unsafe void Perform_FinalizeAndCleanup()
{
    if (pfBackgroundPollAgent != null)
    {
        pfBackgroundPollAgent->Hide();
        Log.Debug("[PF BG] (cleanup) agent->Hide() called.");
        pfBackgroundPollAgent = null;
    }
    pfPollState = PfPollInternalState.Idle;
    detailOpenRetryCount = 0;
}

// New notification method for extracted data
private void ProcessAndNotifyExtractedData(string dutyName, string description)
{
    if (!Configuration.EnableNotifications || Configuration.TargetChannelId == 0 || string.IsNullOrEmpty(Configuration.BotApiEndpoint))
    {
        Log.Information("[PF PROCESS] Notifications disabled or configuration missing. Skipping.");
        return;
    }

    var playerName = ClientState.LocalPlayer?.Name.TextValue ?? "Unknown Player";
    var partyFinderPassword = Configuration.PartyFinderPassword ?? string.Empty;

    Log.Information($"[PF PROCESS] (detail) Details: Player={playerName}, Duty=\"{dutyName}\", Description=\"{description}\", Configured Password Used: {!string.IsNullOrEmpty(partyFinderPassword)}");

    _ = SendPartyFinderNotificationAsync(playerName, dutyName, description, partyFinderPassword, Configuration.TargetChannelId, Configuration.RoleId, Configuration.BotApiEndpoint);
    hasSentPayloadFlag = true;
}

    private void OnCommand(string command, string args) => ToggleConfigUI();
    private void DrawUI() => WindowSystem.Draw();
    public void ToggleConfigUI() => ConfigWindow.Toggle();
    
    private unsafe void OnPartyFinderAddonLifecycleChange(AddonEvent type, AddonArgs args) 
    {
        isPartyFinderAddonOpen = (type == AddonEvent.PostSetup);
        Log.Debug($"[PF ADDON] PartyFinder addon state changed. IsOpen: {isPartyFinderAddonOpen}");

        if (isPartyFinderAddonOpen)
        {
            hasSentPayloadFlag = false; 
            hasAttemptedClickFlag = false; 
        }

        if (!isPartyFinderAddonOpen && uiIndicatesPlayerPFActive)
        {
            Log.Information("[PF ADDON] PartyFinder window closed while UI indicated an active PF. Resetting states.");
            ResetListingState("AddonClosed");
            hasSentPayloadFlag = false;
            hasAttemptedClickFlag = false;
        }
    }

    private unsafe void OnFrameworkUpdate(IFramework framework)
    {
        if (ClientState.LocalPlayer == null || ClientState.LocalContentId == 0)
            return;
        
        CheckPartyFinderButtonState();

        if (hasSentPayloadFlag)
        {
            if (pfPollState != PfPollInternalState.Idle)
            {
                Perform_FinalizeAndCleanup();
            }
            return;
        }

        switch (pfPollState)
        {
            case PfPollInternalState.Idle:
                var now = DateTime.UtcNow;
                if ((now - lastPoll) >= PollInterval)
                {
                    // Start by showing the main PF agent and waiting for button node
                    pfPollState = PfPollInternalState.WaitingForMainPFNodes;
                    pfBackgroundPollAgent = null;
                    mainPFNodeRetryCount = 0;
                    Perform_ShowMainPFAndWaitForNodes();
                }
                break;
            case PfPollInternalState.WaitingForMainPFNodes:
                Perform_ShowMainPFAndWaitForNodes();
                break;
            case PfPollInternalState.WaitingForDetailAddonOpen:
                Perform_AttemptReadFromDetail();
                break;
            case PfPollInternalState.FinalizeAndCleanup:
                Perform_FinalizeAndCleanup();
                break;
        }
    }

    private unsafe void CheckPartyFinderButtonState()
    {
        if (!isPartyFinderAddonOpen)
        {
            if (uiIndicatesPlayerPFActive)
            {
                Log.Debug("[PF BUTTON] PF Addon not open, but UI previously indicated active. Resetting UI indicator.");
                uiIndicatesPlayerPFActive = false;
                hasSentPayloadFlag = false;
                hasAttemptedClickFlag = false;
                if (!playerHadActiveListingRecently) ResetListingState("ButtonCheckAddonClosedNoRecentData");
            }
            return;
        }

        var pfAddonPtr = GameGui.GetAddonByName(PartyFinderAddonName, 1);
        if (pfAddonPtr == IntPtr.Zero)
        {
            if (uiIndicatesPlayerPFActive)
            {
                Log.Warning($"[PF BUTTON] Addon '{PartyFinderAddonName}' pointer became null, but UI previously indicated active. Resetting UI indicator.");
                 uiIndicatesPlayerPFActive = false;
                            hasSentPayloadFlag = false;
                            hasAttemptedClickFlag = false;
                 if (!playerHadActiveListingRecently) ResetListingState("ButtonCheckAddonNullNoRecentData");
            }
            return;
        }

        try
        {
            var unitBase = (AtkUnitBase*)pfAddonPtr;
            var buttonNode = unitBase->GetNodeById(LFG_RECRUIT_BUTTON_NODE_ID);

            // Check if the node is a component node (NodeType >= 1000)
            if (buttonNode != null && buttonNode->IsVisible() && (ushort)buttonNode->Type >= 1000)
            {
                var componentNode = (AtkComponentNode*)buttonNode;
                // Assume this is a button node for this context
                var atkComponentButton = (AtkComponentButton*)componentNode->Component;
                if (atkComponentButton != null)
                {
                    var textNode = atkComponentButton->ButtonTextNode; // Access the button's own text node

                    if (textNode != null)
                    {
                        string buttonText = textNode->NodeText.ToString();
                        bool currentUiShowsActive = buttonText.Contains("Recruitment Criteria");

                        if (currentUiShowsActive && !uiIndicatesPlayerPFActive)
                        {
                            Log.Information("[PF BUTTON] UI indicates player PF is now active ('Recruitment Criteria' found).");
                            uiIndicatesPlayerPFActive = true; 
                            
                            // Re-typed to eliminate any hidden characters or editor artifacts
    if (!hasSentPayloadFlag && !hasAttemptedClickFlag)
    {
        Log.Information($"[PF BUTTON] Attempting to 'click' button NodeID {LFG_RECRUIT_BUTTON_NODE_ID} to ensure details are loaded.");
        ClickRecruitmentButton(pfAddonPtr);
    }
                        }
                        else if (!currentUiShowsActive && uiIndicatesPlayerPFActive)
                        {
                            Log.Information("[PF BUTTON] UI indicates player PF is no longer active (button text changed from 'Recruitment Criteria').");
                            uiIndicatesPlayerPFActive = false;
                            hasSentPayloadFlag = false;
                            hasAttemptedClickFlag = false;
                            if (!playerHadActiveListingRecently) ResetListingState("ButtonChangeInactiveNoRecentData");
                        }
                    }
                    else if (uiIndicatesPlayerPFActive) 
                    {
                        Log.Warning($"[PF BUTTON] TextNode of Button {LFG_RECRUIT_BUTTON_NODE_ID} is null, but UI previously indicated active. Resetting.");
                        uiIndicatesPlayerPFActive = false;
                        hasSentPayloadFlag = false;
                        hasAttemptedClickFlag = false;
                        if (!playerHadActiveListingRecently) ResetListingState("ButtonTextNodeNull");
                    }
                }
            }
            else if (uiIndicatesPlayerPFActive) 
            {
                Log.Warning($"[PF BUTTON] Button Node {LFG_RECRUIT_BUTTON_NODE_ID} not found or not visible, but UI previously indicated active. Resetting.");
                uiIndicatesPlayerPFActive = false;
                hasSentPayloadFlag = false;
                hasAttemptedClickFlag = false;
                if (!playerHadActiveListingRecently) ResetListingState("ButtonNodeNotFoundOrNotVisible");
            }
        }
        catch (Exception e)
        {
            Log.Error(e, $"[PF BUTTON] Error checking {PartyFinderAddonName} button state.");
        }
    }

    private unsafe void ClickRecruitmentButton(IntPtr addonPtr)
    {
        if (addonPtr == IntPtr.Zero)
        {
            Log.Warning("[PF CLICK] Addon pointer is null, cannot click button.");
            return;
        }
        var unitBase = (AtkUnitBase*)addonPtr;

        // From your debugger log of AtkUnitBase.FireCallback:
        // valueCount = 1 (implied by "0 [Int]: 14", index 0 contains an Int)
        // AtkValue[0].Type = AtkValueType.Int
        // AtkValue[0].Int = 14
        // close = false (implied by "Update visibility: 1")

        AtkValue atkValueParam;
        atkValueParam.Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int; // Use fully qualified enum to avoid ambiguity
        atkValueParam.Int = 14; // The crucial integer value from your log

        uint valueCount = 1;
        bool closeParam = false; // Based on "Update visibility: 1" likely meaning not closing

        Log.Information($"[PF CLICK] Calling AtkUnitBase.FireCallback with valueCount=1, AtkValue[0].Int={atkValueParam.Int}, close={closeParam}");
        
        try
        {
            // Use the 3-argument FireCallback
            unitBase->FireCallback(valueCount, &atkValueParam, closeParam);
            
            hasAttemptedClickFlag = true;
            Log.Information($"[PF CLICK] AtkUnitBase.FireCallback invoked successfully.");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[PF CLICK] Exception during AtkUnitBase.FireCallback call.");
        }
    }

    private void OnPartyFinderListingReceived(IPartyFinderListing listing, IPartyFinderListingEventArgs args)
    {
        if (ClientState.LocalPlayer == null || ClientState.LocalContentId == 0) return;

        if (hasSentPayloadFlag && !isPartyFinderAddonOpen) 
            return;

        if (listing.ContentId == ClientState.LocalContentId)
        {
            Log.Debug($"[PF EVENT] Received listing for local player. GameID: {listing.Id:X16}. Current Notified ID: {lastNotifiedPlayerListingGameId?.ToString("X16") ?? "None"}. UI Active: {uiIndicatesPlayerPFActive}. Clicked: {hasAttemptedClickFlag}");

            playerHadActiveListingRecently = true;

            if (lastNotifiedPlayerListingGameId == null || lastNotifiedPlayerListingGameId != listing.Id)
            {
                Log.Information($"[PF EVENT] New player listing data (ID: {listing.Id:X16}). Processing notification.");
                ProcessAndNotifyPlayerListing(listing);
                lastNotifiedPlayerListingGameId = listing.Id;
                if (!uiIndicatesPlayerPFActive) 
                {
                    Log.Warning($"[PF EVENT] Processed new listing ID {listing.Id:X16} from event, but UI did not (yet) indicate active. Forcing UI active state.");
                    uiIndicatesPlayerPFActive = true;
                }
            }
            else
            {
                Log.Debug($"[PF EVENT] Received listing for local player (ID: {listing.Id:X16}), but it's the same as the last notified one. Skipping.");
            }
        }
    }

    private void ResetListingState(string reason)
    {
        if (lastNotifiedPlayerListingGameId != null || playerHadActiveListingRecently || uiIndicatesPlayerPFActive)
        {
            Log.Information($"[PF STATE] Resetting listing-specific states. Reason: {reason}. WasNotifiedID: {lastNotifiedPlayerListingGameId?.ToString("X16") ?? "N/A"}, WasDataActive: {playerHadActiveListingRecently}, WasUIActive: {uiIndicatesPlayerPFActive}");
            lastNotifiedPlayerListingGameId = null;
            playerHadActiveListingRecently = false;
        }
    }

    private void ProcessAndNotifyPlayerListing(IPartyFinderListing listing)
    {
        if (!Configuration.EnableNotifications || Configuration.TargetChannelId == 0 || string.IsNullOrEmpty(Configuration.BotApiEndpoint))
        {
            Log.Information("[PF PROCESS] Notifications disabled or configuration missing. Skipping.");
            return;
        }

        bool minIlvlFlagSet = listing.DutyFinderSettings.HasFlag(DutyFinderSettingsFlags.MinimumItemLevel);
        bool silenceEchoFlagSet = listing.DutyFinderSettings.HasFlag(DutyFinderSettingsFlags.SilenceEcho);
        bool passesMinIlvlFlag = !Configuration.FilterRequireMinIlvl || minIlvlFlagSet;
        bool passesSilenceEchoFlag = !Configuration.FilterRequireSilenceEcho || silenceEchoFlagSet;

        Log.Information($"[PF PROCESS] Filters for ListingID {listing.Id:X16}: MinIlvlOK={passesMinIlvlFlag} (Req:{Configuration.FilterRequireMinIlvl}, Has:{minIlvlFlagSet}), SilenceEchoOK={passesSilenceEchoFlag} (Req:{Configuration.FilterRequireSilenceEcho}, Has:{silenceEchoFlagSet})");

        if (passesMinIlvlFlag && passesSilenceEchoFlag)
        {
            Log.Information($"[PF PROCESS] ListingID {listing.Id:X16} meets filter criteria. Preparing notification.");

            var playerName = ClientState.LocalPlayer?.Name.TextValue ?? "Unknown Player";
            var dutySheetRow = listing.Duty.Value; 
            var dutyName = dutySheetRow.Name.ToString();
            // RowRef<ContentFinderCondition> is never null, so no null check needed
            // If Value is not valid, dutyName will be empty
            Log.Information($"[PF PROCESS] Duty RowId {listing.Duty.RowId} for listing {listing.Id:X16}. DutyName: '{dutyName}'.");
            
            var description = listing.Name?.TextValue ?? Configuration.Description; 
            if (string.IsNullOrWhiteSpace(description)) description = Configuration.Description; 
            if (string.IsNullOrWhiteSpace(description)) description = "No description provided."; 


            var partyFinderPassword = Configuration.PartyFinderPassword ?? string.Empty;
            
            bool isPasswordProtectedInGame = false;
            unsafe
            {
                var agentModule = AgentModule.Instance();
                if (agentModule != null)
                {
                    var agentPtr = agentModule->GetAgentByInternalId(AgentId.LookingForGroup);
                    if (agentPtr != null)
                    {
                        var agent = (AgentLookingForGroup*)agentPtr;
                        ushort gamePasswordState = agent->StoredRecruitmentInfo.Password;
                        isPasswordProtectedInGame = gamePasswordState != 0 && gamePasswordState != 10000; 
                    }
                }
            }

            if (isPasswordProtectedInGame && string.IsNullOrEmpty(partyFinderPassword)) {
                Log.Warning($"[PF PROCESS] ListingID {listing.Id:X16} is password protected in-game, but no password is set in plugin config. Sending without password in payload.");
            } else if (!isPasswordProtectedInGame && !string.IsNullOrEmpty(partyFinderPassword)) {
                 Log.Warning($"[PF PROCESS] ListingID {listing.Id:X16} is NOT password protected in-game, but a password IS set in plugin config. Sending with configured password.");
            }

            Log.Information($"[PF PROCESS] Details: Player={playerName}, Duty=\"{dutyName}\", Description=\"{description}\", Configured Password Used: {!string.IsNullOrEmpty(partyFinderPassword)} (In-Game Passworded: {isPasswordProtectedInGame})");
            _ = SendPartyFinderNotificationAsync(playerName, dutyName, description, partyFinderPassword, Configuration.TargetChannelId, Configuration.RoleId, Configuration.BotApiEndpoint);
            hasSentPayloadFlag = true;
        }
        else
        {
            Log.Information($"[PF PROCESS] ListingID {listing.Id:X16} does not meet filter criteria. Notification skipped.");
        }
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
            if (response.IsSuccessStatusCode) { Log.Information($"[HTTP SEND] Party Finder notification sent successfully for: {playerName} - {dutyName}"); }
            else { string r = await response.Content.ReadAsStringAsync(); Log.Error($"[HTTP SEND] Failed to send PF notification. Status: {response.StatusCode} ({response.ReasonPhrase}). Endpoint: {apiEndpoint}. Response: {r}"); }
        }
        catch (HttpRequestException ex) { Log.Error(ex, $"[HTTP SEND] HTTP Request Exception to {apiEndpoint}."); }
        catch (TaskCanceledException ex) { Log.Error(ex, $"[HTTP SEND] Task Canceled (Timeout?) to {apiEndpoint}."); }
        catch (Exception ex) { Log.Error(ex, $"[HTTP SEND] General Exception to {apiEndpoint}."); }
    }
}
