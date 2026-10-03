using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace LCReplay.Plugin.Playback
{
    /// <summary>Uses the game's new Input System without binding to one game assembly version.</summary>
    public static class ReplayInput
    {
        private static Type? _keyboardType;
        private static Type? _mouseType;
        private static readonly Dictionary<string, PropertyInfo?> Properties = new Dictionary<string, PropertyInfo?>();
        private static readonly Dictionary<Type, MethodInfo?> VectorReaders = new Dictionary<Type, MethodInfo?>();

        public static bool WasPressed(string key) => ReadButton(Keyboard, KeyProperty(key), "wasPressedThisFrame");
        public static bool IsPressed(string key) => ReadButton(Keyboard, KeyProperty(key), "isPressed");
        public static bool IsMousePressed(string button) => ReadButton(Mouse, button, "isPressed");
        public static bool WasMousePressed(string button) => ReadButton(Mouse, button, "wasPressedThisFrame");
        public static Vector2 MouseDelta => ReadVector(ReadProperty(Mouse, "delta"));
        public static Vector2 MousePosition => ReadVector(ReadProperty(Mouse, "position"));
        public static Vector2 MouseScroll => ReadVector(ReadProperty(Mouse, "scroll"));
        public static bool Available => Keyboard != null;

        private static object? Keyboard => ReadCurrent(ref _keyboardType, "UnityEngine.InputSystem.Keyboard");
        private static object? Mouse => ReadCurrent(ref _mouseType, "UnityEngine.InputSystem.Mouse");

        private static object? ReadCurrent(ref Type? type, string name)
        {
            if (type == null)
            {
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    type = assembly.GetType(name, false);
                    if (type != null) break;
                }
            }
            if (type == null) return null;
            try { return FindProperty(type, "current")?.GetValue(null, null); }
            catch (Exception) { return null; }
        }

        private static bool ReadButton(object? device, string control, string property)
            => ReadProperty(ReadProperty(device, control), property) is bool pressed && pressed;

        private static object? ReadProperty(object? instance, string name)
        {
            if (instance == null) return null;
            try { return FindProperty(instance.GetType(), name)?.GetValue(instance, null); }
            catch (Exception) { return null; }
        }

        private static PropertyInfo? FindProperty(Type type, string name)
        {
            var key = type.AssemblyQualifiedName + ":" + name;
            if (!Properties.TryGetValue(key, out var property))
            {
                // Mouse.current hides Pointer.current with a different return type.
                // A flattened GetProperty can throw AmbiguousMatchException; select
                // the closest declaration explicitly, also supporting inherited controls.
                for (var current = type; current != null && property == null; current = current.BaseType)
                    property = current.GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);
                Properties[key] = property;
            }
            return property;
        }

        private static Vector2 ReadVector(object? control)
        {
            if (control == null) return Vector2.zero;
            try
            {
                var type = control.GetType();
                if (!VectorReaders.TryGetValue(type, out var reader))
                {
                    reader = type.GetMethod("ReadValue", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                    VectorReaders[type] = reader;
                }
                return reader?.Invoke(control, null) is Vector2 value ? value : Vector2.zero;
            }
            catch (Exception) { return Vector2.zero; }
        }

        private static string KeyProperty(string key)
        {
            var normalized = (key ?? "").Trim().ToLowerInvariant();
            if (normalized.Length == 1 && normalized[0] >= '0' && normalized[0] <= '9') return "digit" + normalized + "Key";
            switch (normalized)
            {
                case "esc": return "escapeKey";
                case "return": return "enterKey";
                case "control": return "ctrlKey";
                case "leftshift": return "leftShiftKey";
                case "rightshift": return "rightShiftKey";
                case "leftctrl": return "leftCtrlKey";
                case "rightctrl": return "rightCtrlKey";
                case "leftalt": return "leftAltKey";
                case "rightalt": return "rightAltKey";
                case "uparrow": return "upArrowKey";
                case "downarrow": return "downArrowKey";
                case "leftarrow": return "leftArrowKey";
                case "rightarrow": return "rightArrowKey";
                case "pageup": return "pageUpKey";
                case "pagedown": return "pageDownKey";
                default: return normalized + "Key";
            }
        }
    }
}
