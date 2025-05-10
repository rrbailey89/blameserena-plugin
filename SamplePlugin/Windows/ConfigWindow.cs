using System;
using System.Numerics;
using Dalamud.Interface.Windowing;
using ImGuiNET;

namespace SamplePlugin.Windows;

public class ConfigWindow : Window, IDisposable
{
    private Configuration configuration;
    private bool isCollapsed = false;

    // We give this window a constant ID using ###
    // This allows for labels being dynamic, like "{FPS Counter}fps###XYZ counter window",
    // and the window ID will always be "###XYZ counter window" for ImGui
    public ConfigWindow(Plugin plugin) : base("A Wonderful Configuration Window###With a constant ID")
    {
        Flags = ImGuiWindowFlags.NoScrollbar |
                ImGuiWindowFlags.NoScrollWithMouse;

        Size = new Vector2(232, 90);
        SizeCondition = ImGuiCond.FirstUseEver;

        configuration = plugin.Configuration;
    }

    public void Dispose() { }

    public override void PreDraw()
    {
        // Flags must be added or removed before Draw() is being called, or they won't apply
        if (configuration.IsConfigWindowMovable)
        {
            Flags &= ~ImGuiWindowFlags.NoMove;
        }
        else
        {
            Flags |= ImGuiWindowFlags.NoMove;
        }

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

        // can't ref a property, so use a local copy
        var configValue = configuration.SomePropertyToBeSavedAndWithADefault;
        if (ImGui.Checkbox("Random Config Bool", ref configValue))
        {
            configuration.SomePropertyToBeSavedAndWithADefault = configValue;
            // can save immediately on change, if you don't want to provide a "Save and Close" button
            configuration.Save();
        }

        var movable = configuration.IsConfigWindowMovable;
        if (ImGui.Checkbox("Movable Config Window", ref movable))
        {
            configuration.IsConfigWindowMovable = movable;
            configuration.Save();
        }

        // --- Plugin Notification Settings ---
        var enableNotifications = configuration.EnableNotifications;
        if (ImGui.Checkbox("Enable Party Finder Notifications", ref enableNotifications))
        {
            configuration.EnableNotifications = enableNotifications;
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
            if (ulong.TryParse(newChannelIdStr, out var newChannelId) && newChannelId != configuration.TargetChannelId)
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
            if (ulong.TryParse(newRoleIdStr, out var newRoleId) && newRoleId != configuration.RoleId)
            {
                configuration.RoleId = newRoleId;
                configuration.Save();
            }
        }

        // Party Finder Password
        string pfPassword = configuration.PartyFinderPassword ?? string.Empty;
        if (ImGui.InputText("Party Finder Password", ref pfPassword, 64))
        {
            if (pfPassword != configuration.PartyFinderPassword)
            {
                configuration.PartyFinderPassword = pfPassword;
                configuration.Save();
                // Optional: Add debug log
                // Dalamud.Plugin.PluginLog.Debug($"[ConfigWindow] PartyFinderPassword updated to: '{(string.IsNullOrEmpty(pfPassword) ? "<empty>" : pfPassword)}'");
            }
        }

        // Party Finder Description
        var pfDescription = configuration.Description ?? string.Empty;
        if (ImGui.InputTextMultiline("Party Finder Description", ref pfDescription, 256, new Vector2(220, 60)))
        {
            if (pfDescription != configuration.Description)
            {
                configuration.Description = pfDescription;
                configuration.Save();
            }
        }
    }
}
