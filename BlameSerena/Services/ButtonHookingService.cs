using System;
using BlameSerena.Constants;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace BlameSerena.Services;

/// <summary>
/// Handles hooking UI button events for Party Finder interactions
/// </summary>
public interface IButtonHookingService : IDisposable
{
    void HookRecruitButton(nint addonAddress);
    void HookYesButton(nint addonAddress);
    void DisableAndDispose();

    event Action? OnRecruitButtonClicked;
    event Action? OnYesButtonClicked;
}

public unsafe class ButtonHookingService : IButtonHookingService
{
    private readonly IGameInteropProvider _hookProvider;
    private readonly IPluginLog _log;

    private delegate void ReceiveEventDelegate(
        AtkEventListener* listener,
        AtkEventType eventType,
        uint param,
        void* data,
        void* a5);

    private Hook<ReceiveEventDelegate>? _buttonHook;
    private nint _hookedVTablePtr = nint.Zero;

    private struct ButtonListeners
    {
        public AtkEventListener* RecruitButtonListener;
        public AtkEventListener* YesButtonListener;
    }
    private ButtonListeners _buttonListeners;

    public event Action? OnRecruitButtonClicked;
    public event Action? OnYesButtonClicked;

    public ButtonHookingService(IGameInteropProvider hookProvider, IPluginLog log)
    {
        _hookProvider = hookProvider;
        _log = log;
    }

    public void HookRecruitButton(nint addonAddress)
    {
        var addon = (AtkUnitBase*)addonAddress;
        if (addon == null)
        {
            _log.Error("LFGCond addon not found");
            return;
        }

        var btn = GetButtonById(addon, AddonNodeIds.RecruitButton, "Recruit");
        if (btn == null)
            return;

        _buttonListeners.RecruitButtonListener = (AtkEventListener*)btn;
        nint recvPtr = *((nint*)*(nint*)btn + GameConstants.VTableReceiveEventSlot);

        // Only hook if not already hooked
        if (_buttonHook == null || _hookedVTablePtr != recvPtr)
        {
            _buttonHook?.Disable();
            _buttonHook?.Dispose();
            _buttonHook = null;
            _buttonHook = _hookProvider.HookFromAddress<ReceiveEventDelegate>(recvPtr, ButtonDetour);
            _buttonHook.Enable();
            _hookedVTablePtr = recvPtr;
            _log.Debug("Button hook enabled (component ReceiveEvent)");
        }
    }

    public void HookYesButton(nint addonAddress)
    {
        var yesno = (AtkUnitBase*)addonAddress;
        var btn = GetButtonById(yesno, AddonNodeIds.YesButton, "YesNo");
        if (btn == null)
            return;

        _buttonListeners.YesButtonListener = (AtkEventListener*)btn;
        nint recvPtr = *((nint*)*(nint*)btn + GameConstants.VTableReceiveEventSlot);

        // Only hook if not already hooked
        if (_buttonHook == null || _hookedVTablePtr != recvPtr)
        {
            _buttonHook?.Disable();
            _buttonHook = _hookProvider.HookFromAddress<ReceiveEventDelegate>(recvPtr, ButtonDetour);
            _buttonHook.Enable();
            _hookedVTablePtr = recvPtr;
            _log.Debug("Button hook enabled (component ReceiveEvent)");
        }
    }

    private AtkComponentButton* GetButtonById(AtkUnitBase* addon, uint nodeId, string label)
    {
        var btn = addon->GetComponentButtonById(nodeId);
        if (btn != null)
            return btn;

        var node = addon->GetNodeById(nodeId);
        if (node == null)
        {
            _log.Error($"{label}: button node {nodeId} not found");
            return null;
        }

        btn = node->GetAsAtkComponentButton();
        if (btn != null)
            return btn;

        _log.Error($"{label}: node {nodeId} exists but is type {node->Type} and did not resolve to an AtkComponentButton");
        return null;
    }

    private void ButtonDetour(AtkEventListener* listener, AtkEventType type, uint param, void* p4, void* p5)
    {
        _buttonHook!.Original(listener, type, param, p4, p5);

        if (type == AtkEventType.MouseClick || type == AtkEventType.ButtonClick)
        {
            if (listener == _buttonListeners.RecruitButtonListener)
                OnRecruitButtonClicked?.Invoke();
            else if (listener == _buttonListeners.YesButtonListener)
                OnYesButtonClicked?.Invoke();
        }
    }

    public void DisableAndDispose()
    {
        if (_buttonHook != null)
        {
            _buttonHook.Disable();
            _buttonHook.Dispose();
            _buttonHook = null;
        }
        _buttonListeners.RecruitButtonListener = null;
        _buttonListeners.YesButtonListener = null;
        _hookedVTablePtr = IntPtr.Zero;
    }

    public void Dispose()
    {
        DisableAndDispose();
    }
}
