using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using LCReplay.Core;
using UnityEngine;

namespace LCReplay.Plugin.Capture
{
    internal sealed class EventHooks : IDisposable
    {
        private readonly Harmony harmony = new Harmony(ReplayPlugin.Guid + ".events");
        private readonly Action<string> logger;
        private bool disposed;
        private static Action<ReplayEvent>? sink;
        private static readonly Dictionary<int, Component> SpawnedEnemies = new Dictionary<int, Component>();
        internal static Component? TakeSpawnedEnemy(int id)
        {
            if (!SpawnedEnemies.TryGetValue(id, out var component)) return null;
            SpawnedEnemies.Remove(id);
            return component;
        }
        public List<string> Installed { get; } = new List<string>();
        public List<string> Missing { get; } = new List<string>();
        private static readonly Dictionary<string, string[]> Methods = new Dictionary<string, string[]>
        {
            ["StartOfRound"] = new[] { "StartGame", "OnShipLandedMiscEvents", "ShipLeave", "ShipHasLeft", "SetShipReadyToLand", "OnLocalDisconnect" },
            ["RoundManager"] = new[] { "GenerateNewFloor", "FinishGeneratingLevel", "FinishGeneratingNewLevelClientRpc" },
            ["GameNetcodeStuff.PlayerControllerB"] = new[] { "DamagePlayer", "DamageOnOtherClients", "KillPlayer", "KillPlayerClientRpc", "GrabObjectClientRpc", "DropHeldItem", "TeleportPlayer", "SwitchToItemSlot" },
            ["GrabbableObject"] = new[] { "GrabItem", "DiscardItem", "PlayDropSFX", "ItemActivate", "SetScrapValue" },
            ["EnemyAI"] = new[] { "Start", "HitEnemy", "KillEnemy", "SwitchToBehaviourState" },
            ["SandSpiderAI"] = new[] { "SpawnWebTrapClientRpc" },
            ["DoorLock"] = new[] { "OpenOrCloseDoor", "SetDoorAsOpen", "UnlockDoor", "LockDoor" },
            ["TerminalAccessibleObject"] = new[] { "SetDoorOpen", "CallFunctionFromTerminal" },
            ["Landmine"] = new[] { "Detonate" }, ["Turret"] = new[] { "ToggleTurretEnabled", "SwitchTurretMode" },
            ["ShipTeleporter"] = new[] { "PressTeleportButtonOnLocalClient" }
        };
        public EventHooks(Action<ReplayEvent> onEvent, bool captureChat, Action<string> log)
        {
            logger = log;
            sink = onEvent;
            try
            {
            var postfix = new HarmonyMethod(typeof(EventHooks).GetMethod(nameof(Observe), BindingFlags.NonPublic | BindingFlags.Static));
            foreach (var pair in Methods)
            {
                var type = GameAccess.Type(pair.Key);
                foreach (var name in pair.Value)
                {
                    var methods = type?.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                        .Where(m => m.Name == name && !m.IsAbstract && !m.ContainsGenericParameters).ToArray() ?? Array.Empty<MethodInfo>();
                    if (methods.Length == 0) Missing.Add(pair.Key + "." + name);
                    foreach (var method in methods)
                        try { harmony.Patch(method, postfix: postfix); Installed.Add(pair.Key + "." + name); }
                        catch (Exception ex) { Missing.Add(pair.Key + "." + name); log("Optional event hook unavailable: " + name + " (" + ex.Message + ")"); }
                }
            }
            if (captureChat)
            {
                var method = GameAccess.Type("HUDManager")?.GetMethod("AddChatMessage", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (method != null)
                    try { harmony.Patch(method, postfix: postfix); Installed.Add("HUDManager.AddChatMessage"); }
                    catch (Exception ex) { log("Chat hook unavailable: " + ex.Message); Missing.Add("HUDManager.AddChatMessage"); }
                else Missing.Add("HUDManager.AddChatMessage");
            }
            }
            catch
            {
                sink = null;
                try { harmony.UnpatchSelf(); } catch { }
                throw;
            }
        }
        private static void Observe(object? __instance, MethodBase __originalMethod, object[] __args)
        {
            try
            {
                var currentSink = sink;
                if (currentSink == null) return;
                var evt = new ReplayEvent { Category = __originalMethod.Name == "AddChatMessage" ? "chat" : "call",
                    Name = (__originalMethod.DeclaringType?.Name ?? "unknown") + "." + __originalMethod.Name };
                if (__instance is Component c && c) evt.Data["instanceId"] = c.GetInstanceID().ToString();
                if (__originalMethod.DeclaringType?.Name == "EnemyAI" && __originalMethod.Name == "Start" && __instance is Component enemy && enemy)
                {
                    if (SpawnedEnemies.Count >= 256) SpawnedEnemies.Clear();
                    SpawnedEnemies[enemy.GetInstanceID()] = enemy;
                }
                var parameters = __originalMethod.GetParameters();
                for (var i = 0; i < Math.Min(__args.Length, parameters.Length); i++)
                { var scalar = GameAccess.Scalar(__args[i]); if (scalar != null) evt.Data[parameters[i].Name ?? ("arg" + i)] = scalar; }
                if (__originalMethod.Name == "DropHeldItem" && __args.Length != 0 &&
                    __args[0] is Component dropped && dropped)
                    evt.Data["itemInstanceId"] = dropped.GetInstanceID().ToString();
                // A call is an observation, not proof it changed state. Snapshots establish outcomes.
                currentSink(evt);
            }
            catch { /* Recording must never change game method behavior. */ }
        }
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            sink = null;
            SpawnedEnemies.Clear();
            try { harmony.UnpatchSelf(); }
            catch (Exception ex) { logger("Event hooks could not all be removed: " + ex.Message); }
        }
    }
}
