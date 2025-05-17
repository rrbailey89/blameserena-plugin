using Dalamud.Configuration;
using Dalamud.Plugin;
using System;

namespace BlameSerena;

public enum PayloadSendPreference
{
    AskEveryTime,
    AlwaysSend,
    NeverSend
}

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 0;

    public bool IsConfigWindowMovable { get; set; } = true;
    public bool SomePropertyToBeSavedAndWithADefault { get; set; } = true;

    // Plugin notification configuration
    public string BotApiEndpoint { get; set; } = "https://blameserena.app/notify/partyfinder";
    public ulong TargetChannelId { get; set; } = 0;
    public ulong RoleId { get; set; } = 0;
    public bool EnableNotifications { get; set; } = false;

    // New: User preference for payload confirmation
    public PayloadSendPreference SendPayloadConfirmation { get; set; } = PayloadSendPreference.AskEveryTime;

    // the below exist just to make saving less cumbersome
    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}
