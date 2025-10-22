using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using System.IO;
using System.Threading.Tasks;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using BlameSerena.Windows;
using BlameSerena.Services;
using BlameSerena.Constants;
using FFXIVClientStructs.FFXIV.Component.GUI;

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
    private const string BlameCommandName = "/blame";

    public Configuration Configuration { get; init; }
    public readonly WindowSystem WindowSystem = new("BlameSerena");
    private ConfigWindow ConfigWindow { get; init; }
    private MainWindow MainWindow { get; init; }

    // Services
    private readonly INotificationService _notificationService;
    private readonly IDutyDataService _dutyDataService;
    private readonly IPartyFinderService _partyFinderService;
    private readonly IButtonHookingService _buttonHookingService;
    private readonly IUIService _uiService;

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

        // Initialize services
        _notificationService = new NotificationService(Log);
        _dutyDataService = new DutyDataService(DataManager, Log);
        _partyFinderService = new PartyFinderService(Log, _dutyDataService, _notificationService);
        _buttonHookingService = new ButtonHookingService(HookProvider, Log);
        _uiService = new UIService(Log);

        // Wire up button hook events
        _buttonHookingService.OnRecruitButtonClicked += OnButtonClickDetected;
        _buttonHookingService.OnYesButtonClicked += OnYesButtonClicked;

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

        CommandManager.AddHandler(BlameCommandName, new CommandInfo(OnBlameCommand)
        {
            HelpMessage = "Blame someone (defaults to Serena). Usage: /blame [reason]"
        });

        PluginInterface.UiBuilder.Draw += DrawUI;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUI;
        PluginInterface.UiBuilder.OpenMainUi += OnBlameSerenaMainUi;

        // Register event-driven hooks
        AddonLifecycle.RegisterListener(AddonEvent.PostSetup, AddonNames.LookingForGroupCondition, OnCondWindow);
        AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, AddonNames.LookingForGroupCondition, OnCondWindow);

        Log.Information($"=== {PluginInterface.Manifest.Name} Loaded ===");
        Log.Information($"Monitoring Party Finder agent for own listing.");
    }

    public void Dispose()
    {
        AddonLifecycle.UnregisterListener(OnCondWindow);

        _buttonHookingService?.Dispose();

        WindowSystem.RemoveAllWindows();
        ConfigWindow.Dispose();
        CommandManager.RemoveHandler(MainWindowCommandName);
        CommandManager.RemoveHandler(ConfigWindowCommandName);
        CommandManager.RemoveHandler(BlameCommandName);
        PluginInterface.UiBuilder.OpenMainUi -= OnBlameSerenaMainUi;

        Log.Information($"=== {PluginInterface.Manifest.Name} Unloaded ===");
    }

    private void OnBlameSerenaMainCommand(string command, string args) => MainWindow.Toggle();

    private void OnBlameSerenaConfigCommand(string command, string args) => ConfigWindow.Toggle();

    private void OnBlameSerenaMainUi() => MainWindow.Toggle();

    private async void OnBlameCommand(string command, string args)
    {
        if (!Configuration.EnableBlameIntegration)
        {
            ChatGui.PrintError("Blame integration is not enabled. Use /blameserenaconfig to configure.");
            return;
        }

        if (string.IsNullOrEmpty(Configuration.BlameApiKey) || Configuration.BlameApiKey.StartsWith("CHANGE_THIS"))
        {
            ChatGui.PrintError("Please configure your API key in /blameserenaconfig first.");
            return;
        }

        string reason = !string.IsNullOrEmpty(args) ? args : null;
        await SendBlameToDiscord(reason);
    }

    private async Task SendBlameToDiscord(string reason)
    {
        const string SERENA_DISCORD_ID = "803867382447079485";
        
        try
        {
            using var httpClient = new HttpClient();
            httpClient.Timeout = TimeSpan.FromSeconds(10);
            
            var payload = new
            {
                apiKey = Configuration.BlameApiKey,
                source = "FFXIV Dalamud Plugin",
                reason = reason,
                targetUserId = SERENA_DISCORD_ID
            };
            
            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            
            if (Configuration.ShowBlameConfirmation)
            {
                ChatGui.Print("Sending blame to Discord...");
            }
            
            var response = await httpClient.PostAsync(Configuration.BlameApiEndpoint, content);
            
            if (response.IsSuccessStatusCode)
            {
                var responseBody = await response.Content.ReadAsStringAsync();
                var result = JsonSerializer.Deserialize<BlameResponse>(responseBody);
                
                if (result?.success == true)
                {
                    var message = result.data?.message ?? "Blame sent successfully!";
                    ChatGui.Print($"[Blame] {message}");
                }
                else
                {
                    ChatGui.PrintError($"Blame failed: {result?.error ?? "Unknown error"}");
                }
            }
            else if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                ChatGui.PrintError("Invalid API key. Please check your configuration.");
            }
            else
            {
                ChatGui.PrintError($"Failed to send blame: HTTP {response.StatusCode}");
            }
        }
        catch (HttpRequestException ex)
        {
            ChatGui.PrintError($"Network error: {ex.Message}");
        }
        catch (TaskCanceledException)
        {
            ChatGui.PrintError("Request timed out. Please try again.");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to send blame");
            ChatGui.PrintError($"Unexpected error: {ex.Message}");
        }
    }

    private class BlameResponse
    {
        public bool success { get; set; }
        public BlameData data { get; set; }
        public string error { get; set; }
    }

    private class BlameData
    {
        public string userId { get; set; }
        public string userName { get; set; }
        public int blameCount { get; set; }
        public string source { get; set; }
        public string reason { get; set; }
        public string message { get; set; }
    }

    private void DrawUI()
    {
        // Don't cancel the popup if one is waiting to be shown
        if (!_partyFinderService.State.CondWindowOpen && showPayloadConfirmPopup && payloadConfirmAction == null)
            showPayloadConfirmPopup = false;

        if (showPayloadConfirmPopup)
        {
            ImGui.SetNextWindowSize(new System.Numerics.Vector2(400, 0), ImGuiCond.Always);
            ImGui.OpenPopup("Confirm Payload Send");

            if (ImGui.BeginPopupModal("Confirm Payload Send",
                                       ref showPayloadConfirmPopup,
                                       ImGuiWindowFlags.AlwaysAutoResize))
            {
                ImGui.TextWrapped("Do you want to send the Party Finder details to the configured endpoint?");
                ImGui.Spacing();

                if (ImGui.Button("Send", new System.Numerics.Vector2(120, 0)))
                {
                    showPayloadConfirmPopup = false;
                    payloadConfirmAction?.Invoke();
                    payloadConfirmAction = null;
                    ImGui.CloseCurrentPopup();
                }

                ImGui.SameLine();

                if (ImGui.Button("Don't Send", new System.Numerics.Vector2(120, 0)))
                {
                    showPayloadConfirmPopup = false;
                    payloadCancelAction?.Invoke();
                    payloadConfirmAction = null;
                    ImGui.CloseCurrentPopup();
                }
                ImGui.EndPopup();
            }
        }

        WindowSystem.Draw();
    }
    public void ToggleConfigUI() => ConfigWindow.Toggle();


    // --- Event handlers for button clicks ---

    // Handler for Recruit button clicks
    private void OnButtonClickDetected()
    {
        // Set flags for new recruit attempt
        _partyFinderService.State.RecruitClicked = true;
        _partyFinderService.State.YesClicked = false; // reset from previous run
        _partyFinderService.State.ConfirmationShown = false;
        Log.Debug("[OnButtonClickDetected] Recruit button click intercepted!");
    }

    // Handler for Yes button clicks in confirmation dialog
    private void OnYesButtonClicked()
    {
        Log.Debug("[OnYesButtonClicked] User clicked Yes in confirmation dialog");
        _partyFinderService.State.YesClicked = true;
        _partyFinderService.State.RecruitClicked = true; // let PreFinalize know this dialog was confirmed
    }

    // Hook the Recruit button
    private void HookRecruitButton()
    {
        var addon = GameGui.GetAddonByName(AddonNames.LookingForGroupCondition, 1);
        _buttonHookingService.HookRecruitButton(addon.Address);
    }

    // Hook the Yes button in the dialog
    private unsafe void HookYesButton(AtkUnitBase* yesno)
    {
        _buttonHookingService.HookYesButton((nint)yesno);
    }

    // Handler for LookingForGroupCondition addon events
    private unsafe void OnCondWindow(AddonEvent ev, AddonArgs args)
    {
        var addonPtr = (AtkUnitBase*)args.Addon.Address;
        if (ev == AddonEvent.PostSetup)
        {
            Log.Debug($"[PF COND WINDOW] PostSetup for {AddonNames.LookingForGroupCondition}.");
            _partyFinderService.State.CondWindowOpen = true;
            HookRecruitButton();
            // Lazily register YesNo dialog listeners
            AddonLifecycle.RegisterListener(AddonEvent.PostSetup, AddonNames.SelectYesNo, OnYesNoDialog);
            AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, AddonNames.SelectYesNo, OnYesNoDialog);
            return;
        }
        // PreFinalize
        Log.Debug($"[PF COND WINDOW] PreFinalize for {AddonNames.LookingForGroupCondition}. recruitClicked: {_partyFinderService.State.RecruitClicked}");
        _partyFinderService.State.CondWindowOpen = false;
        _buttonHookingService.DisableAndDispose();
        // Lazily unregister YesNo dialog listeners
        AddonLifecycle.UnregisterListener(OnYesNoDialog);
        // If confirmation dialog was shown but not confirmed, skip send
        if (_partyFinderService.State.ConfirmationShown && !_partyFinderService.State.YesClicked)
        {
            Log.Debug("[PF COND WINDOW] Recruit canceled by user. Skipping send.");
            _partyFinderService.State.RecruitClicked = false;
        }
        if (!_partyFinderService.State.RecruitClicked)
            return; // nothing to do
        _partyFinderService.State.RecruitClicked = false; // Reset immediately after checking

        // Capture PF data from StoredRecruitmentInfo
        _partyFinderService.CaptureStoredRecruitmentInfo();

        // Handle user preference for payload confirmation
        var playerName = ClientState.LocalPlayer?.Name.TextValue ?? "Unknown Player";
        switch (Configuration.SendPayloadConfirmation)
        {
            case PayloadSendPreference.AlwaysSend:
                _ = _partyFinderService.ProcessAndNotifyAsync(Configuration, playerName).ConfigureAwait(false);
                break;
            case PayloadSendPreference.NeverSend:
                Log.Debug("[PF COND WINDOW] User preference set to NeverSend. Skipping payload.");
                break;
            case PayloadSendPreference.AskEveryTime:
            default:
                ShowPayloadConfirmationPopup(
                    () => _ = _partyFinderService.ProcessAndNotifyAsync(Configuration, playerName).ConfigureAwait(false),
                    () => Log.Debug("[PF COND WINDOW] User chose not to send payload via popup.")
                );
                break;
        }
    }

    // --- Custom ImGui confirmation popup state ---
    private bool showPayloadConfirmPopup = false;
    private Action? payloadConfirmAction = null;
    private Action? payloadCancelAction = null;

    private void ShowPayloadConfirmationPopup(Action onConfirm, Action onCancel)
    {
        payloadConfirmAction = onConfirm;
        payloadCancelAction = onCancel;
        showPayloadConfirmPopup = true;
    }


    // Handler for the YesNo dialog events
    private unsafe void OnYesNoDialog(AddonEvent ev, AddonArgs args)
    {
        var addonPtr = (AtkUnitBase*)args.Addon.Address;
        if (ev == AddonEvent.PostSetup)
        {
            _partyFinderService.State.ConfirmationShown = true; // Mark that dialog appeared
            var yesno = addonPtr;
            var message = _uiService.GetYesNoMessage((nint)yesno);
            if (!(message.Contains("You cannot carry out the selected objective with", StringComparison.OrdinalIgnoreCase) &&
                  message.Contains("this party composition. Proceed anyway?", StringComparison.OrdinalIgnoreCase)))
            {
                Log.Debug($"[PF YES/NO] Dialog text did not match PF confirmation: '{message}'");
                _partyFinderService.State.RecruitClicked = false;
                _partyFinderService.State.YesClicked = false;
                _buttonHookingService.DisableAndDispose();
                return;
            }
            HookYesButton(yesno);
            Log.Debug("[PF YES/NO] Confirmation dialog opened for PF.");
            return;
        }
        // PreFinalize
        Log.Debug($"[PF YES/NO] YesNo dialog closing. yesClicked: {_partyFinderService.State.YesClicked}");
        _buttonHookingService.DisableAndDispose();
        _partyFinderService.State.YesClicked = false;
    }

}
