using System;
using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using System.IO;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using SamplePlugin.Windows;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Gui.PartyFinder.Types;

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
    [PluginService] internal static IPartyFinderGui PartyFinderGui { get; private set; } = null!;

    private const string CommandName = "/blameserena";

    public Configuration Configuration { get; init; }

    public readonly WindowSystem WindowSystem = new("SamplePlugin");
    private ConfigWindow ConfigWindow { get; init; }

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        // you might normally want to embed resources and load them from the manifest stream
        var goatImagePath = Path.Combine(PluginInterface.AssemblyLocation.Directory?.FullName!, "goat.png");

        ConfigWindow = new ConfigWindow(this);

        WindowSystem.AddWindow(ConfigWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "A useful message to display in /xlhelp"
        });

        PluginInterface.UiBuilder.Draw += DrawUI;

        // This adds a button to the plugin installer entry of this plugin which allows
        // to toggle the display status of the configuration ui
        PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUI;

        // Subscribe to Party Finder listing event
        PartyFinderGui.ReceiveListing += OnReceivePartyFinderListing;

        // Add a simple message to the log with level set to information
        // Use /xllog to open the log window in-game
        // Example Output: 00:57:54.959 | INF | [SamplePlugin] ===A cool log message from Sample Plugin===
        Log.Information($"===A cool log message from {PluginInterface.Manifest.Name}===");
    }

    public void Dispose()
    {
        WindowSystem.RemoveAllWindows();

        ConfigWindow.Dispose();

        CommandManager.RemoveHandler(CommandName);

        // Unsubscribe from Party Finder listing event
        PartyFinderGui.ReceiveListing -= OnReceivePartyFinderListing;
    }

    private void OnCommand(string command, string args)
    {
        // Open the config window when /blameserena is used
        ToggleConfigUI();
    }

    private void DrawUI() => WindowSystem.Draw();

    public void ToggleConfigUI() => ConfigWindow.Toggle();

    // Handler for Party Finder listing events
    private void OnReceivePartyFinderListing(IPartyFinderListing listing, IPartyFinderListingEventArgs args)
    {
        // Only proceed if notifications are enabled and config is valid
        if (!Configuration.EnableNotifications || Configuration.TargetChannelId == 0 || string.IsNullOrEmpty(Configuration.BotApiEndpoint))
            return;

        // Log every listing received for debugging
        Log.Information($"[PF DEBUG] ReceiveListing: ListingContentId={listing.ContentId}, LocalContentId={ClientState.LocalContentId}, Name={listing.Name?.TextValue}");

        // Check if this listing belongs to the local player by comparing Content IDs.
        if (listing.ContentId == ClientState.LocalContentId)
        {
            Log.Information("[PF DEBUG] Detected our own Party Finder listing using ContentId match.");

            // --- Apply plugin-configurable filters ---
            bool minIlvlFlagSet = listing.DutyFinderSettings.HasFlag(Dalamud.Game.Gui.PartyFinder.Types.DutyFinderSettingsFlags.MinimumItemLevel);
            bool silenceEchoFlagSet = listing.DutyFinderSettings.HasFlag(Dalamud.Game.Gui.PartyFinder.Types.DutyFinderSettingsFlags.SilenceEcho);

            bool passesMinIlvlFlag = !Configuration.FilterRequireMinIlvl || minIlvlFlagSet;
            bool passesSilenceEchoFlag = !Configuration.FilterRequireSilenceEcho || silenceEchoFlagSet;

            Log.Information($"[PF DEBUG] Filter Check: RequireMinIlvlFlag={Configuration.FilterRequireMinIlvl}, RequireSilenceEchoFlag={Configuration.FilterRequireSilenceEcho}");
            Log.Information($"[PF DEBUG] Listing Values: MinIlvlFlagSet={minIlvlFlagSet}, SilenceEchoFlagSet={silenceEchoFlagSet}");

            if (passesMinIlvlFlag && passesSilenceEchoFlag)
            {
                Log.Information("[PF DEBUG] Listing meets configured filter criteria.");

                // Extract necessary information, handling potential nulls for safety
                var playerName = ClientState.LocalPlayer?.Name.TextValue ?? "Unknown Player (Local)";
                var dutyName = listing.Duty.Value.Name.ToString();
                // Use the custom description and password from configuration
                var description = Configuration.Description ?? "";
                var partyFinderPassword = Configuration.PartyFinderPassword ?? "";

                // Log the details before sending notification
                Log.Information($"[PF DEBUG] Details: PlayerName={playerName}, DutyName={dutyName}, Description=\"{description}\", PartyFinderPassword=\"{partyFinderPassword}\"");
                Log.Information($"[PF DEBUG] Sending notification to ChannelId={Configuration.TargetChannelId}, Endpoint={Configuration.BotApiEndpoint}");

                // Send notification (async fire-and-forget)
                _ = SendPartyFinderNotificationAsync(playerName, dutyName, description, partyFinderPassword, Configuration.TargetChannelId, Configuration.RoleId, Configuration.BotApiEndpoint);
            }
            else
            {
                Log.Information("[PF DEBUG] Listing does not meet configured filter criteria. Notification skipped.");
            }
        }
        else
        {
            // This listing is not ours, ignore it for notification purposes
            Log.Information($"[PF DEBUG] Received listing is not our own (ContentId mismatch).");
        }
    }

    private async System.Threading.Tasks.Task SendPartyFinderNotificationAsync(string playerName, string dutyName, string description, string partyFinderPassword, ulong channelId, ulong roleId, string apiEndpoint)
    {
        try
        {
            using var httpClient = new System.Net.Http.HttpClient();
            var payload = new
            {
                PlayerName = playerName,
                DutyName = dutyName,
                Description = description,
                PartyFinderPassword = partyFinderPassword,
                ChannelId = channelId,
                RoleId = roleId
            };
            var json = System.Text.Json.JsonSerializer.Serialize(payload);
            var content = new System.Net.Http.StringContent(json, System.Text.Encoding.UTF8, "application/json");
            var response = await httpClient.PostAsync(apiEndpoint, content);
            if (response.IsSuccessStatusCode)
            {
                Log.Information($"Party Finder notification sent successfully for: {playerName} - {dutyName} - {channelId} - {roleId}");
            }
            else
            {
                Log.Error($"Failed to send Party Finder notification. Status Code: {response.StatusCode}. Response: {await response.Content.ReadAsStringAsync()}");
            }
        }
        catch (System.Exception ex)
        {
            Log.Error($"Exception sending Party Finder notification: {ex}");
        }
    }
}
