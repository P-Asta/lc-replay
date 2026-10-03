using System;
using System.Runtime.CompilerServices;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using LCReplay.Plugin.Playback;
using UnityEngine;
using UnityEngine.InputSystem;

namespace LCReplay.Plugin
{
    internal sealed class ReplayBookmarkInput : IDisposable
    {
        internal const string InputUtilsGuid = "com.rune580.LethalCompanyInputUtils";
        private readonly ConfigEntry<KeyCode> fallback;
        private Func<bool>? pressed;
        private Action? disable;
        internal ReplayBookmarkInput(ConfigEntry<KeyCode> fallback, Action<string> log)
        {
            this.fallback = fallback;
            if (!Chainloader.PluginInfos.ContainsKey(InputUtilsGuid)) return;
            try { AttachInputUtils(); log("Replay bookmark key registered with InputUtils's game settings."); }
            catch (Exception error) { log("Replay bookmark uses the configured fallback key: " + error.Message); }
        }
        [MethodImpl(MethodImplOptions.NoInlining)]
        private void AttachInputUtils()
        {
            var plugin = Chainloader.PluginInfos[InputUtilsGuid].Instance;
            var metadata = Chainloader.PluginInfos[ReplayPlugin.Guid].Metadata;
            var actions = ReplayKeybinds.Create(plugin.GetType().Assembly, metadata);
            var property = actions.GetType().GetProperty("AddBookmark")!;
            InputAction? action = null;
            pressed = () => (action ??= property.GetValue(actions) as InputAction)?.WasPerformedThisFrame() == true;
            disable = () => actions.GetType().GetMethod("Disable")!.Invoke(actions, null);
        }
        internal bool WasPressed() => pressed != null ? pressed() : ReplayInput.WasPressed(fallback.Value.ToString());
        public void Dispose() { disable?.Invoke(); pressed = null; disable = null; }
    }
}
