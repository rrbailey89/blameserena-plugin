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
    private const string LfgCondAddon = "LookingForGroupCondition";

    public Configuration Configuration { get; init; }
    public readonly WindowSystem WindowSystem = new("SamplePlugin");
    private ConfigWindow ConfigWindow { get; init; }

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
        CommandManager.RemoveHandler(CommandName);

        Log.Information($"=== {PluginInterface.Manifest.Name} Unloaded ===");
    }

    private void OnCommand(string command, string args) => ToggleConfigUI();
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
            tempComment = GetRecruitmentComment(ref r);
            tempPwdState = r.Password;
            tempFlags = (byte)r.DutyFinderSettingFlags;

            // Fallback logic for comment: if blank, use config; if both blank, send empty
            if (string.IsNullOrWhiteSpace(tempComment) && !string.IsNullOrWhiteSpace(Configuration.Description))
            {
                tempComment = Configuration.Description;
                Log.Debug($"[CustomRecruitButton] PF comment was blank, using description from configuration: '{tempComment}'");
            }
            else if (string.IsNullOrWhiteSpace(tempComment) && string.IsNullOrWhiteSpace(Configuration.Description))
            {
                tempComment = string.Empty;
                Log.Debug("[CustomRecruitButton] Both PF comment and config description are blank, sending empty description.");
            }
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
            HandleCustomRecruitButtonClick();
        }
        ImGui.End();

        ImGui.PopStyleVar(3);
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

    // Helper to extract the comment string from the internal fixed-size array using reflection and correct pinning
    private unsafe string GetRecruitmentComment(ref AgentLookingForGroup.RecruitmentSub r)
    {
        var field = typeof(AgentLookingForGroup.RecruitmentSub).GetField("_comment", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        if (field == null)
        {
            Log.Error("[GetRecruitmentComment] Could not find _comment field via reflection.");
            return string.Empty;
        }
        object commentStruct = field.GetValue(r)!;
        var handle = System.Runtime.InteropServices.GCHandle.Alloc(commentStruct, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            byte* ptr = (byte*)handle.AddrOfPinnedObject();
            int len = 0;
            while (len < 192 && ptr[len] != 0)
                len++;
            return len == 0 ? string.Empty : Encoding.UTF8.GetString(ptr, len);
        }
        finally
        {
            handle.Free();
        }
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
}
