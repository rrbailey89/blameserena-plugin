using System;
using System.Numerics;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;
using Lumina.Excel.Sheets;

namespace BlameSerena.Windows;

public class MainWindow : Window, IDisposable
{
    private readonly string logoPath;
    private readonly Plugin plugin;

    // We give this window a hidden ID using ##
    // So that the user will see "BlameSerena" as window title,
    // but for ImGui the ID is "BlameSerena##With a hidden ID"
    public MainWindow(Plugin plugin, string logoPath)
        : base("BlameSerena##With a hidden ID", ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(375, 330),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
        };

        this.logoPath = logoPath;
        this.plugin = plugin;
    }

    public void Dispose() { }

    public override void Draw()
    {
        // Do not use .Text() or any other formatted function like TextWrapped(), or SetTooltip().
        // These expect formatting parameter if any part of the text contains a "%", which we can't
        // provide through our bindings, leading to a Crash to Desktop.
        // Replacements can be found in the ImGuiHelpers Class

        if (ImGui.Button("Show Settings"))
        {
            plugin.ToggleConfigUI();
        }

        ImGui.Spacing();

        using (var child = ImRaii.Child("SomeChildWithAScrollbar", Vector2.Zero, true))
        {
            if (child.Success)
            {
                var logo = Plugin.TextureProvider.GetFromFile(logoPath).GetWrapOrDefault();
                if (logo != null)
                {
                    using (ImRaii.PushIndent(55f))
                    {
                        ImGui.Image(logo.Handle, new Vector2(logo.Width, logo.Height));
                    }
                }
                else
                {
                    ImGui.TextUnformatted("Image not found.");
                }
            }
        }
    }
}
