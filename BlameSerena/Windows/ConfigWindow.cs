using System;
using System.Numerics;
using Dalamud.Interface.Windowing;
using ImGuiNET;
using BlameSerena;

namespace BlameSerena.Windows;

public class ConfigWindow : Window, IDisposable
{
    private Configuration configuration;
    private bool isCollapsed = false;
    private readonly Plugin plugin;
    private readonly string logoPath;

    // We give this window a constant ID using ###
    // This allows for labels being dynamic, like "{FPS Counter}fps###XYZ counter window",
    // and the window ID will always be "###XYZ counter window" for ImGui
    public ConfigWindow(Plugin plugin) : base("BlameSerena Configuration Window###With a constant ID")
    {
        Flags = ImGuiWindowFlags.NoScrollbar |
                ImGuiWindowFlags.NoScrollWithMouse;

        Size = new Vector2(232, 90);
        SizeCondition = ImGuiCond.FirstUseEver;

        configuration = plugin.Configuration;
        this.plugin = plugin;
        this.logoPath = BlameSerena.Plugin.LogoPath;
    }

    public void Dispose() { }

    public override void PreDraw()
    {
        // Always allow the config window to be movable
        Flags &= ~ImGuiWindowFlags.NoMove;

        // Handle double-click on title bar
        if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left) && ImGui.IsWindowHovered(ImGuiHoveredFlags.RootWindow))
        {
            isCollapsed = !isCollapsed;
            if (isCollapsed)
            {
                Flags |= ImGuiWindowFlags.NoCollapse;
            }
            else
            {
                Flags &= ~ImGuiWindowFlags.NoCollapse;
            }
        }
    }

    public override void Draw()
    {
        if (isCollapsed)
            return;

        var logo = BlameSerena.Plugin.TextureProvider.GetFromFile(logoPath).GetWrapOrDefault();
        if (logo != null)
        {
            // Save current cursor position
            var oldCursorPos = ImGui.GetCursorPos();
            float windowWidth = ImGui.GetWindowWidth();
            float imageWidth = logo.Width;
            float rightAlign = windowWidth - imageWidth - 10.0f; // 10px padding from right
            ImGui.SetCursorPos(new Vector2(rightAlign > 0 ? rightAlign : 0, oldCursorPos.Y));
            ImGui.Image(logo.ImGuiHandle, new Vector2(logo.Width, logo.Height));
            // Restore cursor position so config options start at the top left
            ImGui.SetCursorPos(oldCursorPos);
        }
        else
        {
            var oldCursorPos = ImGui.GetCursorPos();
            float windowWidth = ImGui.GetWindowWidth();
            float textWidth = ImGui.CalcTextSize("Image not found.").X;
            float rightAlign = windowWidth - textWidth - 10.0f;
            ImGui.SetCursorPos(new Vector2(rightAlign > 0 ? rightAlign : 0, oldCursorPos.Y));
            ImGui.TextUnformatted("Image not found.");
            ImGui.SetCursorPos(oldCursorPos);
        }

        // can't ref a property, so use a local copy
        var configValue = configuration.SomePropertyToBeSavedAndWithADefault;
        // Removed: Random Config Bool

        // --- Plugin Notification Settings ---
        var enableNotifications = configuration.EnableNotifications;
        if (ImGui.Checkbox("Enable Party Finder Notifications", ref enableNotifications))
        {
            configuration.EnableNotifications = enableNotifications;
            configuration.Save();
        }

        // Payload confirmation preference
        var payloadOptions = new[] { "Ask Every Time", "Always Send", "Never Send" };
        int selectedPayloadOption = (int)configuration.SendPayloadConfirmation;
        if (ImGui.Combo("Payload Confirmation", ref selectedPayloadOption, payloadOptions, payloadOptions.Length))
        {
            configuration.SendPayloadConfirmation = (PayloadSendPreference)selectedPayloadOption;
            configuration.Save();
        }

        // Bot API Endpoint
        var apiEndpoint = configuration.BotApiEndpoint ?? string.Empty;
        var apiEndpointBuffer = new byte[256];
        var apiEndpointBytes = System.Text.Encoding.UTF8.GetBytes(apiEndpoint);
        Array.Copy(apiEndpointBytes, apiEndpointBuffer, Math.Min(apiEndpointBytes.Length, apiEndpointBuffer.Length - 1));
        if (ImGui.InputText("Bot API Endpoint", apiEndpointBuffer, (uint)apiEndpointBuffer.Length))
        {
            var newEndpoint = System.Text.Encoding.UTF8.GetString(apiEndpointBuffer).TrimEnd('\0');
            if (newEndpoint != configuration.BotApiEndpoint)
            {
                configuration.BotApiEndpoint = newEndpoint;
                configuration.Save();
            }
        }

        // Target Channel ID
        var channelIdStr = configuration.TargetChannelId == 0 ? "" : configuration.TargetChannelId.ToString();
        var channelIdBuffer = new byte[32];
        var channelIdBytes = System.Text.Encoding.UTF8.GetBytes(channelIdStr);
        Array.Copy(channelIdBytes, channelIdBuffer, Math.Min(channelIdBytes.Length, channelIdBuffer.Length - 1));
        if (ImGui.InputText("Discord Channel ID", channelIdBuffer, (uint)channelIdBuffer.Length, ImGuiInputTextFlags.CharsDecimal))
        {
            var newChannelIdStr = System.Text.Encoding.UTF8.GetString(channelIdBuffer).TrimEnd('\0');
            if (string.IsNullOrWhiteSpace(newChannelIdStr))
            {
                if (configuration.TargetChannelId != 0)
                {
                    configuration.TargetChannelId = 0;
                    configuration.Save();
                }
            }
            else if (ulong.TryParse(newChannelIdStr, out var newChannelId) && newChannelId != configuration.TargetChannelId)
            {
                configuration.TargetChannelId = newChannelId;
                configuration.Save();
            }
        }

        // Discord Role ID
        var roleIdStr = configuration.RoleId == 0 ? "" : configuration.RoleId.ToString();
        var roleIdBuffer = new byte[32];
        var roleIdBytes = System.Text.Encoding.UTF8.GetBytes(roleIdStr);
        Array.Copy(roleIdBytes, roleIdBuffer, Math.Min(roleIdBytes.Length, roleIdBuffer.Length - 1));
        if (ImGui.InputText("Discord Role ID", roleIdBuffer, (uint)roleIdBuffer.Length, ImGuiInputTextFlags.CharsDecimal))
        {
            var newRoleIdStr = System.Text.Encoding.UTF8.GetString(roleIdBuffer).TrimEnd('\0');
            if (string.IsNullOrWhiteSpace(newRoleIdStr))
            {
                if (configuration.RoleId != 0)
                {
                    configuration.RoleId = 0;
                    configuration.Save();
                }
            }
            else if (ulong.TryParse(newRoleIdStr, out var newRoleId) && newRoleId != configuration.RoleId)
            {
                configuration.RoleId = newRoleId;
                configuration.Save();
            }
        }
    }
}
