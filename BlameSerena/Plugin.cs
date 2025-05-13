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
using Dalamud.Hooking;

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
    [PluginService] internal static IGameInteropProvider HookProvider { get; private set; } = null!;

    private const string MainWindowCommandName = "/blameserena";
    private const string ConfigWindowCommandName = "/blameserenaconfig";
    private const string LfgCondAddon = "LookingForGroupCondition";
    private const string YesNoAddonName = "SelectYesno";

    public Configuration Configuration { get; init; }
    public readonly WindowSystem WindowSystem = new("BlameSerena");
    private ConfigWindow ConfigWindow { get; init; }
    private MainWindow MainWindow { get; init; }

    // New event-driven PF state fields
    private bool condWindowOpen = false;
    private bool recruitClicked = false;
    private bool yesNoDialogOpen = false;
    private bool yesClicked = false;
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

    // Add global hook fields for button click hooks
    private unsafe delegate void ReceiveEventDelegate(
        AtkEventListener* listener,
        AtkEventType eventType,
        uint param,
        void* data,
        void* a5);

    private Dalamud.Hooking.Hook<ReceiveEventDelegate>? recruitHook;
    private Dalamud.Hooking.Hook<ReceiveEventDelegate>? yesHook;

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

        // Register event-driven hooks
        AddonLifecycle.RegisterListener(AddonEvent.PostSetup, LfgCondAddon, OnCondWindow);
        AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, LfgCondAddon, OnCondWindow);
        
        // Add listeners for Yes/No dialog
        AddonLifecycle.RegisterListener(AddonEvent.PostSetup, YesNoAddonName, OnYesNoDialog);
        AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, YesNoAddonName, OnYesNoDialog);

        Log.Information($"=== {PluginInterface.Manifest.Name} Loaded ===");
        Log.Information($"Monitoring Party Finder agent for own listing.");
    }

    public void Dispose()
    {
        AddonLifecycle.UnregisterListener(OnCondWindow);
        AddonLifecycle.UnregisterListener(OnYesNoDialog);

        recruitHook?.Disable();
        yesHook?.Disable();

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

    // Method to determine if a job is DoW/DoM
    // No longer needed as the game handles this check automatically
    // Keeping the method declaration in case it's used elsewhere
    private bool IsDoWorDoM(uint classJobId)
    {
        // This would contain the logic to check if a class/job is DoW/DoM
        // For example, DoW/DoM jobs typically have IDs 1-38
        return classJobId <= 38;
    }

    // --- New event-driven PF logic with AtkEventListener ---

    // NOTE: The following two classes are removed because AtkEventListener is sealed and cannot be inherited.
    // Instead, you should use a function hook or delegate to intercept button clicks.
    // See comments in HookButtonEventListener and HookYesNoDialogButton for where to implement hooks.

    // Handler for Recruit button clicks
    private unsafe void OnButtonClickDetected()
    {
        Log.Debug("[OnButtonClickDetected] Recruit button click intercepted!");
        
        // Capture PF data here - similar to original HandleCustomRecruitButtonClick
        var agent = AgentLookingForGroup.Instance();
        if (agent != null)
        {
            var r = agent->StoredRecruitmentInfo;
            tempDutyId = r.SelectedDutyId;
            tempComment = r.CommentString;
            tempPwdState = r.Password;
            tempFlags = (byte)r.DutyFinderSettingFlags;
            
            Log.Debug($"[OnButtonClickDetected] Stored PF data: DutyId={tempDutyId}, Comment='{tempComment}', PwdState={tempPwdState}, Flags={tempFlags}");
        }
        
        // We don't know yet if this will be confirmed immediately or if the game
        // will show a confirmation dialog (for non-combat jobs)
        // The game will handle this check automatically:
        // - If combat job: no dialog will appear, window closes immediately
        // - If non-combat: the game will show confirmation, which our YesNoDialog handler will catch
        
        // For now, we optimistically set recruitClicked, but we'll reset it if the window closes
        // without proper confirmation for non-combat jobs
        Log.Debug("[OnButtonClickDetected] Setting recruitClicked flag, waiting to see if confirmation is required");
        recruitClicked = true;
        
        // If the game shows a confirmation dialog, our OnYesNoDialog handler will manage this flag:
        // - If "No" is clicked, the dialog closes without setting yesClicked, and the window stays open
        // - If "Yes" is clicked, yesClicked is set to true in OnYesButtonClicked
    }

    // Handler for Yes button clicks in confirmation dialog
    private void OnYesButtonClicked()
    {
        Log.Debug("[OnYesButtonClicked] User clicked Yes in confirmation dialog");
        yesClicked = true;
    }

    // Method to hook the Recruit button's event listener
    private unsafe void HookRecruitButton()
    {
        var addon = (AtkUnitBase*)GameGui.GetAddonByName(LfgCondAddon, 1);
        if (addon == null) { Log.Error("LFGCond addon not found"); return; }

        var btnNode = addon->GetNodeById(111);
        if (btnNode == null) { Log.Error("Recruit button node 111 not found"); return; }

        // Find the first CollisionNode under this component
        AtkResNode* collision = null;
        var comp = btnNode->GetComponent();
        for (var i = 0; i < comp->UldManager.NodeListCount; i++)
        {
            var n = comp->UldManager.NodeList[i];
            if (n->Type == NodeType.Collision)
            {
                collision = n;
                break;
            }
        }
        if (collision == null) { Log.Error("No collision node under button 111"); return; }

        recruitHook?.Disable();
        recruitHook = HookNodeClick(collision, RecruitDetour);
        Log.Debug("Recruit-button hook enabled (collision node)");
    }

    // Helper to find the first Collision node under a component
    private static unsafe AtkResNode* FirstCollisionNode(AtkComponentBase* comp)
    {
        for (int i = 0; i < comp->UldManager.NodeListCount; i++)
        {
            var n = comp->UldManager.NodeList[i];
            if (n->Type == NodeType.Collision)
                return n;
        }
        return null;
    }

    // Method to hook the Yes button in the dialog
    private unsafe void HookYesButton(AtkUnitBase* yesno)
    {
        // Yes button is node 8 under the correct hierarchy, get its component and find the first collision node
        var yesNode = yesno->GetNodeById(8); // ID 8 = "Yes"
        if (yesNode == null) { Log.Error("YesNo: Node 8 not found"); return; }
        var comp = yesNode->GetComponent();
        if (comp == null) { Log.Error("YesNo: Component for node 8 not found"); return; }
        var collision = FirstCollisionNode(comp);
        if (collision == null) { Log.Error("YesNo: No collision node found under node 8"); return; }

        yesHook?.Disable();
        yesHook = HookNodeClick(collision, YesDetour);
        Log.Debug("Yes-button hook enabled (first collision node under node 8)");
    }

    // Utility to install a hook on a node
    private unsafe Dalamud.Hooking.Hook<ReceiveEventDelegate> HookNodeClick(
        AtkResNode* collisionNode,
        ReceiveEventDelegate detour)
    {
        var listener = (AtkEventListener*)collisionNode;
        nint vtable = *(nint*)listener;
        nint recvPtr = Marshal.ReadIntPtr(vtable, IntPtr.Size * 2);

        var hook = HookProvider.HookFromAddress(recvPtr, detour);
        hook.Enable();
        return hook;
    }

    // Detour bodies for button click hooks
    private unsafe void RecruitDetour(AtkEventListener* listener,
        AtkEventType type, uint p3, void* p4, void* p5)
    {
        recruitHook!.Original(listener, type, p3, p4, p5);

        if (type == AtkEventType.ButtonClick)
        {
            OnButtonClickDetected();
        }
    }

    private unsafe void YesDetour(AtkEventListener* listener,
        AtkEventType type, uint p3, void* p4, void* p5)
    {
        yesHook!.Original(listener, type, p3, p4, p5);

        if (type == AtkEventType.ButtonClick)
        {
            OnYesButtonClicked();
        }
    }

    // Handler for LookingForGroupCondition addon events
    private unsafe void OnCondWindow(AddonEvent ev, AddonArgs args)
    {
        var addonPtr = args.Addon;
        if (ev == AddonEvent.PostSetup)
        {
            Log.Debug($"[PF COND WINDOW] PostSetup for {LfgCondAddon}.");
            condWindowOpen = true;

            HookRecruitButton();
            
            return;
        }

        // PreFinalize
        Log.Debug($"[PF COND WINDOW] PreFinalize for {LfgCondAddon}. recruitClicked: {recruitClicked}");
        condWindowOpen = false;

        recruitHook?.Disable();

        if (!recruitClicked)
        {
            Log.Debug("[PF COND WINDOW] PreFinalize: Recruit button was not clicked. Resetting recruitClicked and returning.");
            recruitClicked = false; // Reset just to be safe
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

    // Handler for the YesNo dialog events
    private unsafe void OnYesNoDialog(AddonEvent ev, AddonArgs args)
    {
        var addonPtr = args.Addon;
        
        if (ev == AddonEvent.PostSetup)
        {
            Log.Debug("[PF YES/NO] YesNo dialog opened.");
            yesNoDialogOpen = true;
            
            if (condWindowOpen)
            {
                HookYesButton((AtkUnitBase*)addonPtr);
                
                Log.Debug("[PF YES/NO] Confirmation dialog appeared for PF listing - resetting recruitClicked until confirmed");
                recruitClicked = false;
            }
            
            return;
        }
        
        // PreFinalize
        Log.Debug($"[PF YES/NO] YesNo dialog closing. yesClicked: {yesClicked}");
        yesNoDialogOpen = false;
        
        yesHook?.Disable();
        
        // If Yes was clicked and PF condition window is open, this is our confirmation
        if (yesClicked && condWindowOpen)
        {
            Log.Debug("[PF YES/NO] User confirmed non-combat PF post via YesNo dialog.");
            
            // Set our recruitment clicked flag so the PF window can process it on close
            recruitClicked = true;
        }
        
        // Reset the yes clicked flag
        yesClicked = false;
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
        catch (TaskCanceledException ex) { Log.Error(ex, $"[HTTP SEND] Request timed out to {apiEndpoint}."); }
        catch (Exception ex) { Log.Error(ex, $"[HTTP SEND] Unexpected error sending notification to {apiEndpoint}."); }
    }
}