using System;
using BlameSerena.Constants;
using BlameSerena.Utilities;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace BlameSerena.Services;

/// <summary>
/// Handles UI-related operations like reading dialog messages
/// </summary>
public interface IUIService
{
    string GetYesNoMessage(nint yesnoAddonAddress);
}

public unsafe class UIService : IUIService
{
    private readonly IPluginLog _log;

    public UIService(IPluginLog log)
    {
        _log = log;
    }

    /// <summary>
    /// Extract and sanitize the Yes/No dialog message
    /// </summary>
    public string GetYesNoMessage(nint yesnoAddonAddress)
    {
        var yesno = (AtkUnitBase*)yesnoAddonAddress;

        for (int id = (int)AddonNodeIds.DialogTextNode1; id >= (int)AddonNodeIds.DialogTextNode3; id--)
        {
            var node = yesno->GetNodeById((uint)id);
            if (node == null || node->Type != NodeType.Text)
                continue;

            var textNode = (AtkTextNode*)node;
            var nodeText = textNode->NodeText;

            if (!GameStructUtilities.HasText(nodeText))
                continue;

            return StringUtilities.Sanitize(nodeText.ToString());
        }

        return string.Empty;
    }
}
