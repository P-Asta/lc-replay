using System;
using System.Collections.Generic;
using System.Reflection;
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
        private object? hudOwner;
        private bool disposed;

        internal void Block(Component player)
        {
            if (disposed) return;
            Add(InputSystem.actions);
            var wrapper = GameAccess.Read(player, "playerActions");
            Add(GameAccess.Read(wrapper, "asset") as InputActionAsset);
            // HUDManager owns a separate PlayerActions instance. Its Update polls
            // PingScan (RMB) while spectating and votes to leave after a hold.
            var hud = GameAccess.Singleton("HUDManager");
            if (!ReferenceEquals(hudOwner, hud))
            {
                hudOwner = hud;
                ResetSpectatorVoteHold(hud);
            }
            var hudActions = GameAccess.Read(hud, "playerActions");
            Add(GameAccess.Read(hudActions, "asset") as InputActionAsset);
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
            ResetSpectatorVoteHold(hudOwner);
            var currentHud = GameAccess.Singleton("HUDManager");
            if (!ReferenceEquals(currentHud, hudOwner)) ResetSpectatorVoteHold(currentHud);
            foreach (var pair in actions)
            {
                // A disconnect may dispose the original asset. Restoring one
                // obsolete action must not prevent other controls from returning.
                try { if (pair.Value) pair.Key.Enable(); }
                catch (Exception) { }
            }
            actions.Clear(); assets.Clear(); hudOwner = null;
        }

        private static void ResetSpectatorVoteHold(object? hud)
        {
            if (hud == null) return;
            try
            {
                hud.GetType().GetField("holdButtonToEndGameEarlyHoldTime",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.SetValue(hud, 0f);
            }
            catch (Exception) { /* Optional HUD state differs between game versions. */ }
        }
    }
}
