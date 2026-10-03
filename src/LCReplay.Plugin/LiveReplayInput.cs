using System;
using System.Collections.Generic;
using LCReplay.Plugin.Capture;
using UnityEngine;
using UnityEngine.InputSystem;

namespace LCReplay.Plugin
{
    // The game polls actions in Update as well as receiving callbacks. Keep
    // replay's raw device/UI input independent of those gameplay actions.
    internal sealed class LiveReplayInput : IDisposable
    {
        private readonly Dictionary<InputAction, bool> actions = new Dictionary<InputAction, bool>();
        private readonly HashSet<InputActionAsset> assets = new HashSet<InputActionAsset>();
        private bool disposed;

        internal void Block(Component player)
        {
            if (disposed) return;
            Add(InputSystem.actions);
            var wrapper = GameAccess.Read(player, "playerActions");
            Add(GameAccess.Read(wrapper, "asset") as InputActionAsset);
            foreach (var action in actions.Keys)
                if (action.enabled) action.Disable();
        }

        private void Add(InputActionAsset? asset)
        {
            if (!asset || !assets.Add(asset!)) return;
            foreach (var map in asset!.actionMaps)
                foreach (var action in map.actions)
                    actions[action] = action.enabled;
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            foreach (var pair in actions)
            {
                // A disconnect may dispose the original asset. Restoring one
                // obsolete action must not prevent other controls from returning.
                try { if (pair.Value) pair.Key.Enable(); }
                catch (ObjectDisposedException) { }
            }
            actions.Clear(); assets.Clear();
        }
    }
}
