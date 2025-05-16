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
using BlameSerena.Windows;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System.Linq;

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
    private bool yesClicked = false;
    private bool confirmationShown = false; // Track if confirmation dialog appeared
    private ulong lastListingId = 0;   // for duplicate-send guard
    private ulong lastDutyId = 0;
    private int lastCommentHash = 0;

    // Temporary storage for PF data from StoredRecruitmentInfo
    private ushort tempDutyId = 0;
    private string tempComment = string.Empty;
    private ushort tempPwdState = 0;
    private byte tempFlags = 0;
    private ushort tempCategory = 0; // Store SelectedCategory

    // Add global hook fields for button click hooks
    private unsafe delegate void ReceiveEventDelegate(
        AtkEventListener* listener,
        AtkEventType eventType,
        uint param,
        void* data,
        void* a5);

    // Store the hook and listener pointers
    private Dalamud.Hooking.Hook<ReceiveEventDelegate>? buttonHook;
    private unsafe struct ButtonListeners {
        public AtkEventListener* recruitButtonListener;
        public AtkEventListener* yesButtonListener;
    }
    private unsafe ButtonListeners buttonListeners;
    private nint hookedVTablePtr = nint.Zero;

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
        // YesNoAddonName listeners are now registered lazily in OnCondWindow

        Log.Information($"=== {PluginInterface.Manifest.Name} Loaded ===");
        Log.Information($"Monitoring Party Finder agent for own listing.");
    }

    public void Dispose()
    {
        AddonLifecycle.UnregisterListener(OnCondWindow);
        // Remove global YesNoAddonName listener unregister
        // AddonLifecycle.UnregisterListener(OnYesNoDialog);

        DisableAndDisposeHook();
        unsafe { buttonListeners.recruitButtonListener = null; buttonListeners.yesButtonListener = null; }

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
        // Set flags for new recruit attempt
        recruitClicked = true;
        yesClicked = false; // reset from previous run
        confirmationShown = false;
        Log.Debug("[OnButtonClickDetected] Recruit button click intercepted!");
        var agent = AgentLookingForGroup.Instance();
        if (agent != null)
        {
            var r = agent->StoredRecruitmentInfo;
            tempDutyId = r.SelectedDutyId;
            tempComment = r.CommentString;
            tempPwdState = r.Password;
            tempFlags = (byte)r.DutyFinderSettingFlags;
            tempCategory = r.SelectedCategory;
            Log.Debug($"[OnButtonClickDetected] Stored PF data: DutyId={{tempDutyId}}, Comment='{{tempComment}}', PwdState={{tempPwdState}}, Flags={{tempFlags}}, Category={{tempCategory}} ({MapCategory(tempCategory)})");
        }
    }

    // Helper to map SelectedCategory to a string
    private string MapCategory(ushort category)
    {
        // These values may need to be adjusted based on game version/data:
        // 1 = Dungeon, 2 = Trial, 3 = Raid (commonly used, but check your client!)
        return category switch
        {
            1 => "Dungeon",
            2 => "Trial",
            3 => "Raid",
            _ => "Other"
        };
    }

    // Handler for Yes button clicks in confirmation dialog
    private void OnYesButtonClicked()
    {
        Log.Debug("[OnYesButtonClicked] User clicked Yes in confirmation dialog");
        yesClicked = true;
        recruitClicked = true; // let PreFinalize know this dialog was confirmed
    }

    // Method to hook the Recruit button's event listener
    private unsafe void HookRecruitButton()
    {
        var addon = (AtkUnitBase*)GameGui.GetAddonByName(LfgCondAddon, 1);
        if (addon == null) { Log.Error("LFGCond addon not found"); return; }

        // 111 is the Recruit button container
        var node = addon->GetNodeById(111);
        if (node == null) { Log.Error("Recruit button node 111 not found"); return; }

        var btn = (AtkComponentButton*)node->GetComponent();
        if (btn == null) { Log.Error("Recruit button component not found"); return; }

        buttonListeners.recruitButtonListener = (AtkEventListener*)btn;
        nint recvPtr = *((nint*)*(nint*)btn + 2);   // v-table slot 2 = ReceiveEvent

        // Only hook if not already hooked
        if (buttonHook == null || hookedVTablePtr != recvPtr)
        {
            buttonHook?.Disable();
            buttonHook = HookProvider.HookFromAddress<ReceiveEventDelegate>(recvPtr, ButtonDetour);
            buttonHook.Enable();
            hookedVTablePtr = recvPtr;
            Log.Debug("Button hook enabled (component ReceiveEvent)");
        }
    }

    // Method to hook the Yes button in the dialog
    private unsafe void HookYesButton(AtkUnitBase* yesno)
    {
        var node8 = yesno->GetNodeById(8);
        if (node8 == null) { Log.Error("YesNo: node 8 not found"); return; }

        var btn = (AtkComponentButton*)node8->GetComponent();
        if (btn == null) { Log.Error("YesNo: button component not found"); return; }

        buttonListeners.yesButtonListener = (AtkEventListener*)btn;
        nint recvPtr = *((nint*)*(nint*)btn + 2);   // v-table slot 2 = ReceiveEvent

        // Only hook if not already hooked
        if (buttonHook == null || hookedVTablePtr != recvPtr)
        {
            buttonHook?.Disable();
            buttonHook = HookProvider.HookFromAddress<ReceiveEventDelegate>(recvPtr, ButtonDetour);
            buttonHook.Enable();
            hookedVTablePtr = recvPtr;
            Log.Debug("Button hook enabled (component ReceiveEvent)");
        }
    }

    // Single detour for both buttons
    private unsafe void ButtonDetour(AtkEventListener* listener, AtkEventType type, uint param, void* p4, void* p5)
    {
        buttonHook!.Original(listener, type, param, p4, p5);

        // existing filter
        if (type == AtkEventType.MouseClick || type == AtkEventType.ButtonClick)
        {
            if (listener == buttonListeners.recruitButtonListener)
                OnButtonClickDetected();
            else if (listener == buttonListeners.yesButtonListener)
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
            // Lazily register YesNo dialog listeners
            AddonLifecycle.RegisterListener(AddonEvent.PostSetup, YesNoAddonName, OnYesNoDialog);
            AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, YesNoAddonName, OnYesNoDialog);
            return;
        }
        // PreFinalize
        Log.Debug($"[PF COND WINDOW] PreFinalize for {LfgCondAddon}. recruitClicked: {recruitClicked}");
        condWindowOpen = false;
        DisableAndDisposeHook();
        // Lazily unregister YesNo dialog listeners
        AddonLifecycle.UnregisterListener(OnYesNoDialog);
        // If confirmation dialog was shown but not confirmed, skip send
        if (confirmationShown && !yesClicked)
        {
            Log.Debug("[PF COND WINDOW] Recruit canceled by user. Skipping send.");
            recruitClicked = false;
        }
        if (!recruitClicked)
            return; // nothing to do
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

    // Utility to extract and sanitize the Yes/No dialog message
    private unsafe string GetYesNoMessage(AtkUnitBase* yesno)
    {
        // Try node 3 first, then 2, then 1
        for (uint id = 3; id >= 1; --id)
        {
            var node = yesno->GetNodeById(id);
            if (node != null)
            {
                var n = (AtkTextNode*)node;
                if (n != null)
                {
                    var raw = n->NodeText.ToString();
                    if (!string.IsNullOrEmpty(raw))
                        return Sanitize(raw);
                }
            }
        }
        return string.Empty;
    }

    private static string Sanitize(string s)
    {
        // Remove all C0-control chars except \n (0x0A) if you want to keep line-breaks
        return new string(s.Where(c => c >= 0x20 || c == '\n').ToArray())
                 .Trim(); // also strips the trailing newline Lumina adds
    }

    // Handler for the YesNo dialog events
    private unsafe void OnYesNoDialog(AddonEvent ev, AddonArgs args)
    {
        var addonPtr = args.Addon;
        if (ev == AddonEvent.PostSetup)
        {
            confirmationShown = true; // Mark that dialog appeared
            var yesno = (AtkUnitBase*)addonPtr;
            var message = GetYesNoMessage(yesno);
            if (!(message.Contains("You cannot carry out the selected objective with", StringComparison.OrdinalIgnoreCase) &&
                  message.Contains("this party composition. Proceed anyway?", StringComparison.OrdinalIgnoreCase)))
            {
                Log.Debug($"[PF YES/NO] Dialog text did not match PF confirmation: '{message}'");
                recruitClicked = false;
                yesClicked = false;
                DisableAndDisposeHook();
                return;
            }
            HookYesButton(yesno);
            Log.Debug("[PF YES/NO] Confirmation dialog opened for PF.");
            return;
        }
        // PreFinalize
        Log.Debug($"[PF YES/NO] YesNo dialog closing. yesClicked: {yesClicked}");
        DisableAndDisposeHook();
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

        // Use the password from the UI only
        if (gamePasswordState != 10000)
        {
            finalPasswordToSend = gamePasswordState.ToString("D4"); // Always 4 digits
            Log.Debug("[DEBUG PROCESS] Using password from PF UI: '{0}'", finalPasswordToSend);
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

    private void DisableAndDisposeHook()
    {
        if (buttonHook != null)
        {
            buttonHook.Disable();
            buttonHook.Dispose();
            buttonHook = null;
            hookedVTablePtr = IntPtr.Zero;
        }
    }
}