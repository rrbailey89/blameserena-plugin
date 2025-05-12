using System;
using System.Runtime.InteropServices;
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
using ImGuiNET;
using Dalamud.Plugin.Services;
using BlameSerena.Windows;
using Dalamud.Game.Gui.PartyFinder;
using Dalamud.Game.Gui.PartyFinder.Types;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI.Info;

namespace BlameSerena;

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

    private const string MainWindowCommandName = "/blameserena";
    private const string ConfigWindowCommandName = "/blameserenaconfig";
    private const string LfgCondAddon = "LookingForGroupCondition";

    public Configuration Configuration { get; init; }
    public readonly WindowSystem WindowSystem = new("BlameSerena");
    private ConfigWindow ConfigWindow { get; init; }
    private MainWindow MainWindow { get; init; }

    // New event-driven PF state fields
    private bool condWindowOpen = false;
    private bool recruitClicked = false;
    private ulong lastListingId = 0;   // for duplicate-send guard
    private ulong lastDutyId = 0;
    private int lastCommentHash = 0;

    // Temporary storage for PF data from StoredRecruitmentInfo
    private ushort tempDutyId = 0;
    private string tempComment = string.Empty;
    private ushort tempPwdState = 0;
    private byte tempFlags = 0;

    // State for confirmation modal
    private bool showNonCombatConfirm = false;
    private bool confirmRecruitClick = false;

    public static string LogoPath {
        get {
            var imagesLogoPath = Path.Combine(PluginInterface.AssemblyLocation.DirectoryName!, "images", "logo.png");
            if (File.Exists(imagesLogoPath))
                return imagesLogoPath;
            return Path.Combine(PluginInterface.AssemblyLocation.DirectoryName!, "logo.png");
        }
    }

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        ConfigWindow = new ConfigWindow(this);
        WindowSystem.AddWindow(ConfigWindow);

        MainWindow = new MainWindow(this, LogoPath);
        WindowSystem.AddWindow(MainWindow);

        CommandManager.AddHandler(MainWindowCommandName, new CommandInfo(OnBlameSerenaMainCommand)
        {
            HelpMessage = "Shows or hides the main plugin window."
        });

        CommandManager.AddHandler(ConfigWindowCommandName, new CommandInfo(OnBlameSerenaConfigCommand)
        {
            HelpMessage = "Shows or hides the configuration window for BlameSerena."
        });

        PluginInterface.UiBuilder.Draw += DrawUI;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUI;
        PluginInterface.UiBuilder.OpenMainUi += OnBlameSerenaMainUi;

        // Register only the new event-driven hooks
        AddonLifecycle.RegisterListener(AddonEvent.PostSetup, LfgCondAddon, OnCondWindow);
        AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, LfgCondAddon, OnCondWindow);

        Log.Information($"=== {PluginInterface.Manifest.Name} Loaded ===");
        Log.Information($"Monitoring Party Finder agent for own listing.");
    }

    public void Dispose()
    {
        AddonLifecycle.UnregisterListener(OnCondWindow);

        WindowSystem.RemoveAllWindows();
        ConfigWindow.Dispose();
        CommandManager.RemoveHandler(MainWindowCommandName);
        CommandManager.RemoveHandler(ConfigWindowCommandName);
        PluginInterface.UiBuilder.OpenMainUi -= OnBlameSerenaMainUi;

        Log.Information($"=== {PluginInterface.Manifest.Name} Unloaded ===");
    }

    private void OnBlameSerenaMainCommand(string command, string args) => MainWindow.Toggle();

    private void OnBlameSerenaConfigCommand(string command, string args) => ConfigWindow.Toggle();

    private void OnBlameSerenaMainUi() => MainWindow.Toggle();

    private void DrawUI() => WindowSystem.Draw();
    public void ToggleConfigUI() => ConfigWindow.Toggle();

    // --- New event-driven PF logic ---

    // Called by ImGui button to mimic native button behavior
    private unsafe void HandleCustomRecruitButtonClick()
    {
        Log.Debug("[CustomRecruitButton] Clicked.");

        // Capture PF data from StoredRecruitmentInfo before firing the callback
        var agent = AgentLookingForGroup.Instance();
        if (agent != null)
        {
            var r = agent->StoredRecruitmentInfo;
            tempDutyId = r.SelectedDutyId;
            tempComment = r.CommentString;
            tempPwdState = r.Password;
            tempFlags = (byte)r.DutyFinderSettingFlags;

            Log.Debug($"[CustomRecruitButton] Stored temp data: DutyId={tempDutyId}, Comment='{tempComment}', PwdState={tempPwdState}, Flags={tempFlags}");
        }
        else
        {
            Log.Error("[CustomRecruitButton] Agent was null before reading StoredRecruitmentInfo.");
            // Still proceed to fire callback, but temp data will be empty
        }

        IntPtr addonPtr = GameGui.GetAddonByName(LfgCondAddon, 1);
        if (addonPtr == IntPtr.Zero)
        {
            Log.Error("[CustomRecruitButton] LookingForGroupCondition addon not found when trying to fire native click.");
            return;
        }
        var unitBase = (AtkUnitBase*)addonPtr;
        AtkResNode* originalButtonNode = unitBase->GetNodeById(111);

        if (originalButtonNode == null)
        {
            Log.Error("[CustomRecruitButton] Original Recruit Members button (Node 111) not found.");
            return;
        }

        // Fire the native callback to create the listing and close the window
        AtkValue arg;
        arg.Type = FFXIVClientStructs.FFXIV.Component.GUI.ValueType.Int;
        arg.Int = 0; // Confirmed to work for this action
        unitBase->FireCallback(1, &arg);
        Log.Debug("[CustomRecruitButton] Fired native callback for PF creation.");

        recruitClicked = true;

        // Programmatically close the window to mimic native behavior
        unitBase->Close(true);
        Log.Debug("[CustomRecruitButton] Programmatically closed LookingForGroupCondition window.");

        // Hide the original button to prevent double interaction if window doesn't close instantly
        originalButtonNode->NodeFlags &= ~NodeFlags.Visible;
        Log.Debug("[CustomRecruitButton] Original native button hidden after programmatic click.");
    }

    // Draw custom ImGui button to replace the native Recruit Members button
    private unsafe void DrawCustomRecruitButton()
    {
        if (!condWindowOpen)
            return;

        var player = ClientState.LocalPlayer;
        bool isNonCombat = player != null && player.ClassJob.IsValid && !IsDoWorDoM(player.ClassJob.RowId);

        IntPtr addonPtr = GameGui.GetAddonByName(LfgCondAddon, 1);
        if (addonPtr == IntPtr.Zero)
            return;

        var unitBase = (AtkUnitBase*)addonPtr;
        AtkResNode* originalButtonNode = unitBase->GetNodeById(111);
        if (originalButtonNode == null)
            return;

        // Calculate screen position and size for ImGui button, including node's own scale
        float buttonScreenX = unitBase->X + (originalButtonNode->X * unitBase->Scale);
        float buttonScreenY = unitBase->Y + (originalButtonNode->Y * unitBase->Scale);
        float buttonWidth = originalButtonNode->Width * unitBase->Scale * originalButtonNode->ScaleX;
        float buttonHeight = originalButtonNode->Height * unitBase->Scale * originalButtonNode->ScaleY;

        // Remove ImGui window padding and make button fill the window
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, System.Numerics.Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new System.Numerics.Vector2(4, 2));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, System.Numerics.Vector2.Zero);

        var windowFlags = ImGuiWindowFlags.NoDecoration |
                          ImGuiWindowFlags.NoScrollWithMouse |
                          ImGuiWindowFlags.NoBackground |
                          ImGuiWindowFlags.NoSavedSettings |
                          ImGuiWindowFlags.NoFocusOnAppearing |
                          ImGuiWindowFlags.NoNav |
                          ImGuiWindowFlags.NoMove;

        ImGui.SetNextWindowPos(new System.Numerics.Vector2(buttonScreenX, buttonScreenY));
        ImGui.SetNextWindowSize(new System.Numerics.Vector2(buttonWidth, buttonHeight));

        ImGui.Begin("CustomRecruitButtonWindow", windowFlags);
        if (ImGui.Button("Recruit Members##Custom", ImGui.GetContentRegionAvail()))
        {
            if (isNonCombat)
            {
                showNonCombatConfirm = true;
            }
            else
            {
                HandleCustomRecruitButtonClick();
            }
        }
        ImGui.End();

        ImGui.PopStyleVar(3);

        // Confirmation modal for non-combat jobs
        if (showNonCombatConfirm)
        {
            ImGui.OpenPopup("NonCombatConfirmModal");
            showNonCombatConfirm = false;
        }
        bool open = true;
        if (ImGui.BeginPopupModal("NonCombatConfirmModal", ref open, ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.TextUnformatted("You are not on a combat job (DoW/DoM).\nAre you sure you want to list this Party Finder?");
            if (ImGui.Button("Yes, List Anyway", new System.Numerics.Vector2(150, 0)))
            {
                HandleCustomRecruitButtonClick();
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
            if (ImGui.Button("Cancel", new System.Numerics.Vector2(100, 0)))
            {
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndPopup();
        }
    }

    private unsafe void OnCondWindow(AddonEvent ev, AddonArgs args)
    {
        var addonPtr = args.Addon;
        if (ev == AddonEvent.PostSetup)
        {
            Log.Debug($"[PF COND WINDOW] PostSetup for {LfgCondAddon}.");
            condWindowOpen = true;

            // Hide the original button
            var unitBase = (AtkUnitBase*)addonPtr;
            AtkResNode* originalButtonNode = unitBase->GetNodeById(111);
            if (originalButtonNode != null)
            {
                originalButtonNode->NodeFlags &= ~NodeFlags.Visible;
                Log.Debug("[PF COND WINDOW] Original Recruit Members button (Node 111) hidden.");
            }

            // Register ImGui draw
            PluginInterface.UiBuilder.Draw += DrawCustomRecruitButton;
            return;
        }

        // PreFinalize
        Log.Debug($"[PF COND WINDOW] PreFinalize for {LfgCondAddon}. recruitClicked: {recruitClicked}");
        condWindowOpen = false;

        // Restore the original button
        var unitBaseRestore = (AtkUnitBase*)addonPtr;
        AtkResNode* originalButtonNodeRestore = unitBaseRestore->GetNodeById(111);
        if (originalButtonNodeRestore != null)
        {
            originalButtonNodeRestore->NodeFlags |= NodeFlags.Visible;
            Log.Debug("[PF COND WINDOW] Original Recruit Members button (Node 111) restored.");
        }

        // Unregister ImGui draw
        PluginInterface.UiBuilder.Draw -= DrawCustomRecruitButton;

        if (!recruitClicked)
        {
            Log.Debug("[PF COND WINDOW] PreFinalize: Recruit button was not clicked. Resetting recruitClicked and returning.");
            return;
        }

        recruitClicked = false; // Reset immediately after checking

        var agent = AgentLookingForGroup.Instance();
        ulong currentOwnListingId = (agent != null) ? agent->OwnListingId : 0;
        int tempCommentHash = tempComment.GetHashCode();

        // Duplicate guard: check against last sent values
        if ((currentOwnListingId != 0 && currentOwnListingId == lastListingId && tempDutyId == lastDutyId && tempCommentHash == lastCommentHash) ||
            (currentOwnListingId == 0 && tempDutyId == lastDutyId && tempCommentHash == lastCommentHash && lastListingId != 0))
        {
            Log.Debug("[PF COND WINDOW] PreFinalize: Duplicate recruit click detected. Skipping send.");
            return;
        }

        string dutyName = GetDutyName(tempDutyId);
        if (string.IsNullOrEmpty(dutyName))
        {
            Log.Warning($"[PF COND WINDOW] PreFinalize: Could not get duty name for Duty ID {tempDutyId}. Using raw ID as fallback.");
            dutyName = $"Duty ID {tempDutyId}";
        }

        Log.Debug($"[PF COND WINDOW] PreFinalize: Processing listing. DutyName: '{dutyName}', Comment: '{tempComment}', PwdState: {tempPwdState}, Flags: {tempFlags}");
        ProcessAndNotifyStoredListing(dutyName, tempComment, tempPwdState, tempFlags);

        lastDutyId = tempDutyId;
        lastCommentHash = tempCommentHash;
        if (currentOwnListingId != 0)
            lastListingId = currentOwnListingId;
        Log.Debug($"[PF COND WINDOW] PreFinalize: Updated last sent values. LastLID: {lastListingId}, LastDutyID: {lastDutyId}, LastCommentHash: {lastCommentHash}");
    }

    private string GetDutyName(ushort dutyId)
    {
        var dutySheet = DataManager.GetExcelSheet<Lumina.Excel.Sheets.ContentFinderCondition>();
        if (dutySheet == null)
        {
            Log.Warning($"[GetDutyName] Could not get ContentFinderCondition sheet.");
            return string.Empty;
        }
        var entry = dutySheet.GetRow(dutyId);
        // Lumina's GetRow returns a struct, not null. Check RowId.
        if (entry.RowId != dutyId)
        {
            Log.Warning($"[GetDutyName] Could not find name for duty ID: {dutyId}");
            return string.Empty;
        }
        string name = entry.Name.ToString();
        if (!string.IsNullOrEmpty(name) && char.IsLower(name[0]))
        {
            // Capitalize the first letter if it's lowercase
            char[] chars = name.ToCharArray();
            chars[0] = char.ToUpper(chars[0]);
            name = new string(chars);
        }

        // Special case: Move (Savage) to the end for Bahamut turns
        // e.g. "The Second Coil of Bahamut (Savage) - Turn 2" -> "The Second Coil of Bahamut - Turn 2 (Savage)"
        if (name.Contains("(Savage)") && name.Contains("Turn"))
        {
            // Find "(Savage)" and "Turn"
            int savageIdx = name.IndexOf("(Savage)");
            int turnIdx = name.IndexOf("Turn");
            if (savageIdx > 0 && turnIdx > savageIdx)
            {
                // Remove " (Savage)" from its current position
                string withoutSavage = name.Remove(savageIdx - 1, 9); // Remove space + "(Savage)"
                // Insert " (Savage)" at the end
                name = withoutSavage + " (Savage)";
            }
        }

        return name;
    }

    // --- Unchanged notification/HTTP/config logic below ---

    private void ProcessAndNotifyStoredListing(string dutyName, string description, ushort gamePasswordState, byte dutyFinderSettings)
    {
        Log.Debug($"[DEBUG PROCESS] ProcessAndNotifyStoredListing called. Duty: '{dutyName}', Desc: '{description}', PassState: {gamePasswordState}, Settings: {dutyFinderSettings}");

        if (!Configuration.EnableNotifications || Configuration.TargetChannelId == 0 || string.IsNullOrEmpty(Configuration.BotApiEndpoint))
        {
            Log.Debug("[DEBUG PROCESS] Notifications disabled or config missing. Skipping.");
            return;
        }

        var playerName = ClientState.LocalPlayer?.Name.TextValue ?? "Unknown Player";
        string finalPasswordToSend = string.Empty;

        // Correct password logic: only send config password if UI password is enabled and config is set
        bool isUiPasswordProtectionEnabled = (gamePasswordState != 10000);
        string configPassword = Configuration.PartyFinderPassword?.Trim('\0') ?? string.Empty;

        if (isUiPasswordProtectionEnabled)
        {
            if (!string.IsNullOrEmpty(configPassword)) // Changed from IsNullOrWhiteSpace to IsNullOrEmpty after trimming nulls
            {
                finalPasswordToSend = configPassword; // Use the trimmed version
                Log.Debug("[DEBUG PROCESS] PF has password enabled in UI. Using password from plugin config: '{0}'", finalPasswordToSend);
            }
            else
            {
                finalPasswordToSend = string.Empty;
                Log.Debug("[DEBUG PROCESS] PF has password enabled in UI, but plugin config password is blank. Sending blank password.");
            }
        }
        else
        {
            finalPasswordToSend = string.Empty;
            Log.Debug("[DEBUG PROCESS] PF has password disabled in UI. Sending blank password.");
        }

        Log.Debug($"[DEBUG PROCESS] Preparing to send notification. Player: {playerName}, Duty: {dutyName}, Desc: {description}, PasswordToSend: '{finalPasswordToSend}'");
        _ = SendPartyFinderNotificationAsync(playerName, dutyName, description, finalPasswordToSend, Configuration.TargetChannelId, Configuration.RoleId, Configuration.BotApiEndpoint);
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

    // Helper: Check if a job is DoW or DoM (combat job)
    private bool IsDoWorDoM(uint classJobId)
    {
        // DoW: 1-10 (GLA, PGL, MRD, LNC, ARC, ROG, PLD, MNK, WAR, DRG)
        //       21-30 (BRD, MCH, DNC, NIN, SAM, RPR, VPR, GNB, DRK, SGE)
        // DoM: 11-20 (CNJ, THM, WHM, BLM, ACN, SMN, SCH, AST, RDM, BLU)
        //       31-40 (SGE, PICT, etc. future jobs)
        // This list is up to Dawntrail (2024). Adjust as needed for new jobs.
        // DoH: 8x, DoL: 16x
        // See: https://ffxiv.consolegameswiki.com/wiki/Class
        //
        // DoW: 1-10, 21-30, 32-38, 39-40, 45 (Viper)
        // DoM: 11-20, 24, 25, 27, 28, 35, 36, 43, 44 (Pictomancer, Blue Mage, etc.)
        //
        // For simplicity, use known ranges and explicit IDs for new jobs.
        //
        // Tanks: 19 (PLD), 21 (WAR), 32 (DRK), 37 (GNB)
        // Melee DPS: 20 (MNK), 22 (DRG), 30 (NIN), 34 (SAM), 39 (RPR), 45 (VPR)
        // Physical Ranged: 23 (BRD), 31 (MCH), 38 (DNC)
        // Magic Ranged: 24 (BLM), 25 (SMN), 27 (RDM), 35 (BLU), 44 (PICT)
        // Healers: 26 (WHM), 28 (SCH), 33 (AST), 40 (SGE)
        //
        // DoH: 8x, DoL: 16x
        //
        // We'll use a switch for clarity.
        switch (classJobId)
        {
            // DoW
            case 1: // GLA
            case 2: // PGL
            case 3: // MRD
            case 4: // LNC
            case 5: // ARC
            case 6: // CNJ (DoM)
            case 7: // THM (DoM)
            case 8: // CRP (DoH)
            case 9: // BSM (DoH)
            case 10: // ARM (DoH)
            case 11: // GSM (DoH)
            case 12: // LTW (DoH)
            case 13: // WVR (DoH)
            case 14: // ALC (DoH)
            case 15: // CUL (DoH)
            case 16: // MIN (DoL)
            case 17: // BTN (DoL)
            case 18: // FSH (DoL)
            case 19: // PLD
            case 20: // MNK
            case 21: // WAR
            case 22: // DRG
            case 23: // BRD
            case 24: // WHM (DoM)
            case 25: // BLM (DoM)
            case 26: // ACN (DoM)
            case 27: // SMN (DoM)
            case 28: // SCH (DoM)
            case 29: // ROG
            case 30: // NIN
            case 31: // MCH
            case 32: // DRK
            case 33: // AST (DoM)
            case 34: // SAM
            case 35: // RDM (DoM)
            case 36: // BLU (DoM)
            case 37: // GNB
            case 38: // DNC
            case 39: // RPR
            case 40: // SGE (DoM)
            case 44: // Pictomancer (DoM)
            case 45: // Viper
                // Only exclude DoH (8-15, 31, 41, 42, 43) and DoL (16-18, 24, 25, 26, 27, 28, 29, 30, 46, 47, 48)
                // But for safety, let's use a whitelist:
                return (classJobId >= 1 && classJobId <= 5) || // GLA, PGL, MRD, LNC, ARC
                       (classJobId == 19 || classJobId == 20 || classJobId == 21 || classJobId == 22 || classJobId == 23 ||
                        classJobId == 29 || classJobId == 30 || classJobId == 31 || classJobId == 32 || classJobId == 34 ||
                        classJobId == 37 || classJobId == 38 || classJobId == 39 || classJobId == 45) // DoW
                    || (classJobId == 6 || classJobId == 7 || classJobId == 24 || classJobId == 25 || classJobId == 26 ||
                        classJobId == 27 || classJobId == 28 || classJobId == 33 || classJobId == 35 || classJobId == 36 ||
                        classJobId == 40 || classJobId == 44); // DoM
            default:
                return false;
        }
    }
}
