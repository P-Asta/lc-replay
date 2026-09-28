using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using LCReplay.Core;
using UnityEngine;
using Object = UnityEngine.Object;

namespace LCReplay.Plugin.Capture
{
    // Only known properties are invoked. General capture reads fields, never arbitrary getters.
    internal static class GameAccess
    {
        private static readonly Dictionary<string, Type?> Types = new Dictionary<string, Type?>();
        private static readonly Dictionary<Type, FieldInfo[]> Fields = new Dictionary<Type, FieldInfo[]>();
        private static readonly Dictionary<(Type, string), (FieldInfo? Field, PropertyInfo? Property)> Members =
            new Dictionary<(Type, string), (FieldInfo?, PropertyInfo?)>();
        private static readonly HashSet<string> EssentialFields = new HashSet<string>(new[]
        {
            "playerClientId", "actualClientId", "playerUsername", "health", "isPlayerControlled", "isPlayerDead", "causeOfDeath",
            "isSprinting", "isCrouching", "isInsideFactory", "isInHangarShipRoom", "sprintMeter", "currentSuitID",
            "ItemSlots", "ItemOnlySlot", "currentItemSlot", "currentlyHeldObjectServer", "playerHeldBy", "isHeld", "isHeldByEnemy",
            "isBeingUsed", "itemUsedUp", "scrapValue", "isInShipRoom", "enemyHP", "isEnemyDead", "targetPlayer",
            "currentBehaviourStateIndex", "stunNormalizedTimer", "isLocked", "isDoorOpened", "isDoorOpen", "isPickingLock",
            "objectCode", "isPoweredOn", "turretMode", "turretActive", "hasExploded", "carHP", "carDestroyed", "gear", "speed",
            "randomMapSeed", "currentLevelID", "inShipPhase", "shipHasLanded", "shipIsLeaving", "livingPlayers", "allPlayersDead",
            "currentDungeonType", "dungeonCompletedGenerating", "dungeonIsGenerating", "currentDayTime", "normalizedTimeOfDay",
            "currentLevelWeather", "profitQuota", "quotaFulfilled", "daysUntilDeadline", "timeUntilDeadline", "groupCredits"
        }, StringComparer.Ordinal);
        private static readonly KeyValuePair<string, string[]>[] NestedFields =
        {
            new KeyValuePair<string, string[]>("insertedBattery", new[] { "charge", "empty" }),
            new KeyValuePair<string, string[]>("itemProperties", new[] { "itemId", "itemName", "isScrap", "requiresBattery", "weight" }),
            new KeyValuePair<string, string[]>("enemyType", new[] { "enemyName", "isOutsideEnemy", "isDaytimeEnemy" }),
            new KeyValuePair<string, string[]>("currentLevel", new[] { "levelID", "PlanetName", "sceneName", "currentWeather" })
        };
        public static Type? Type(string name)
        {
            if (!Types.TryGetValue(name, out var result)) Types[name] = result = AccessTools.TypeByName(name);
            return result;
        }

        public static object? Read(object? value, string name)
        {
            if (value == null) return null;
            try
            {
                var type = value as Type ?? value.GetType();
                if (!Members.TryGetValue((type, name), out var member))
                {
                    // Optional members vary by game version. Resolve silently once;
                    // Harmony's missing-field warnings otherwise repeat every frame
                    // for members that are properties (including NetworkManager).
                    const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic |
                        BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
                    for (var cursor = type; cursor != null; cursor = cursor.BaseType)
                    {
                        member.Field = member.Field ?? cursor.GetField(name, flags);
                        member.Property = member.Property ?? cursor.GetProperty(name, flags);
                    }
                    Members[(type, name)] = member;
                }
                var target = value is Type ? null : value;
                return member.Field?.GetValue(target) ?? member.Property?.GetValue(target, null);
            }
            catch { return null; }
        }

        public static object? Singleton(string name) => Read(Type(name), "Instance") ?? Read(Type(name), "Singleton");
        public static bool Bool(object? value, string name) => Read(value, name) is bool b && b;
        public static bool Connected
        {
            get
            {
                var network = Singleton("Unity.Netcode.NetworkManager");
                return network != null && (Bool(network, "IsListening") || Bool(network, "IsClient") || Bool(network, "IsServer"));
            }
        }
        public static bool CanRecord => Connected && Singleton("StartOfRound") is Component round && round;
        public static bool IsHost => Bool(Singleton("Unity.Netcode.NetworkManager"), "IsServer");
        public static bool NetworkStateKnown => Type("Unity.Netcode.NetworkManager") != null;

        public static IEnumerable<Component> Find(string name)
        {
            var type = Type(name);
            if (type == null || !typeof(Component).IsAssignableFrom(type)) return Array.Empty<Component>();
            return Object.FindObjectsOfType(type, true).OfType<Component>()
                .Where(c => c && c.gameObject.scene.IsValid() && c.gameObject.scene.isLoaded &&
                    !ReplayIsolation.IsReplayScene(c.gameObject.scene));
        }

        public static Dictionary<string, string> CaptureFields(Component component, Func<Component, string> identify, int maxFields)
        {
            var type = component.GetType();
            if (!Fields.TryGetValue(type, out var fields))
            {
                var list = new List<FieldInfo>();
                for (var cursor = type; cursor != null && cursor != typeof(MonoBehaviour) && cursor != typeof(Component); cursor = cursor.BaseType)
                {
                    if (cursor.Namespace?.StartsWith("Unity", StringComparison.Ordinal) == true) continue;
                    list.AddRange(cursor.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                        .Where(f => !f.IsStatic && !f.Name.StartsWith("__", StringComparison.Ordinal)));
                }
                // Preserve outcomes before incidental animation/audio fields consume the configured budget.
                Fields[type] = fields = list.OrderByDescending(f => EssentialFields.Contains(f.Name))
                    .ThenBy(f => f.Name, StringComparer.Ordinal).ToArray();
            }
            var result = new Dictionary<string, string>();
            foreach (var path in NestedFields)
            {
                var parent = Read(component, path.Key);
                if (parent == null) continue;
                foreach (var name in path.Value)
                {
                    var text = Scalar(Read(parent, name), identify);
                    if (text != null && result.Count < maxFields) result[path.Key + "." + name] = text;
                }
            }
            foreach (var field in fields)
            {
                if (result.Count >= maxFields) { result["$truncated"] = "field limit"; break; }
                try
                {
                    var text = Scalar(field.GetValue(component), identify);
                    if (text != null) result[field.Name] = text;
                }
                catch { /* Destroyed Unity objects and removed fields are optional data. */ }
            }
            result["$type"] = type.FullName ?? type.Name;
            var networkId = Read(component, "NetworkObjectId");
            if (networkId != null) result["$networkId"] = Convert.ToString(networkId, CultureInfo.InvariantCulture) ?? "";
            return result;
        }

        public static string? Scalar(object? value, Func<Component, string>? identify = null)
        {
            if (value == null) return null;
            if (value is string s) return s.Length <= 256 ? s : s.Substring(0, 256);
            var type = value.GetType();
            if (type.IsEnum || type.IsPrimitive || value is decimal)
                return Convert.ToString(value, CultureInfo.InvariantCulture);
            if (value is Vector3 v) return string.Format(CultureInfo.InvariantCulture, "{0},{1},{2}", v.x, v.y, v.z);
            if (value is Quaternion q) return string.Format(CultureInfo.InvariantCulture, "{0},{1},{2},{3}", q.x, q.y, q.z, q.w);
            if (value is Component c) return c ? (identify != null ? identify(c) : Scalar(c.GetType().Name + ":" + c.name)) : null;
            if (value is ScriptableObject so) return so ? Scalar(so.name) : null;
            if (value is Array array && array.Rank == 1)
            {
                var values = new List<string>();
                for (var i = 0; i < Math.Min(array.Length, 32); i++)
                {
                    var item = array.GetValue(i);
                    if (item is Array) continue;
                    var scalar = Scalar(item, identify);
                    if (scalar != null) values.Add(i + "=" + scalar);
                }
                if (array.Length > 32) values.Add("...length=" + array.Length);
                return values.Count == 0 ? null : string.Join(";", values);
            }
            return null;
        }

        public static Vec3 Vec(Vector3 v) => new Vec3(v.x, v.y, v.z);
        public static Quat Rot(Quaternion q) => new Quat(q.x, q.y, q.z, q.w);
        public static bool Finite(Vector3 v) => Finite(v.x) && Finite(v.y) && Finite(v.z);
        public static bool Finite(Quaternion q) => Finite(q.x) && Finite(q.y) && Finite(q.z) && Finite(q.w)
            && (double)q.x * q.x + (double)q.y * q.y + (double)q.z * q.z + (double)q.w * q.w > 1e-12;
        public static bool Finite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);
    }
}
