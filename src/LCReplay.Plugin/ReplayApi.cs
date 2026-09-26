using System;
using System.Collections.Generic;
using LCReplay.Core;

namespace LCReplay.Plugin
{
    /// <summary>Optional extension point for other mods. Provider callbacks run on the Unity main thread.</summary>
    public static class ReplayApi
    {
        private static readonly Dictionary<string, Func<IDictionary<string, string>>> Providers = new Dictionary<string, Func<IDictionary<string, string>>>();
        internal static Action<ReplayEvent>? EventSink;
        public static bool IsRecording => EventSink != null;
        public static void RegisterStateProvider(string id, Func<IDictionary<string, string>> provider)
        {
            if (string.IsNullOrWhiteSpace(id) || id.Length > 128) throw new ArgumentException("Provider id must contain 1 to 128 characters.", nameof(id));
            if (provider == null) throw new ArgumentNullException(nameof(provider));
            lock (Providers)
            {
                if (Providers.Count >= 64 && !Providers.ContainsKey(id)) throw new InvalidOperationException("Replay supports at most 64 state providers.");
                Providers[id] = provider;
            }
        }
        public static void UnregisterStateProvider(string id) { lock (Providers) Providers.Remove(id); }
        public static void LogEvent(string category, string name, IDictionary<string, string>? data = null)
        {
            var sink = EventSink;
            if (sink == null) return;
            var captured = new Dictionary<string, string>();
            if (data != null)
                foreach (var value in data)
                {
                    if (captured.Count >= 128) { captured["$truncated"] = "128 event fields"; break; }
                    captured[Limit(value.Key, 128)] = Limit(value.Value, 1024);
                }
            sink(new ReplayEvent { Category = Limit(category ?? "custom", 128), Name = Limit(name ?? "event", 256), Data = captured });
        }
        private static string Limit(string? value, int length) => value == null ? "" : value.Length > length ? value.Substring(0, length) : value;
        internal static void CaptureProviders(Dictionary<string, string> state, Action<string> log)
        {
            KeyValuePair<string, Func<IDictionary<string, string>>>[] callbacks;
            lock (Providers) { callbacks = new KeyValuePair<string, Func<IDictionary<string, string>>>[Providers.Count]; ((ICollection<KeyValuePair<string, Func<IDictionary<string, string>>>>)Providers).CopyTo(callbacks, 0); }
            foreach (var callback in callbacks)
            {
                if (state.Count >= 1000) { state["$providersTruncated"] = "frame field limit"; break; }
                try
                {
                    var count = 0;
                    foreach (var value in callback.Value())
                    {
                        if (++count > 128 || state.Count >= 1000) { state["mod:" + callback.Key + ":$truncated"] = "provider/frame field limit"; break; }
                        var text = value.Value ?? "";
                        state["mod:" + callback.Key + ":" + Limit(value.Key, 128)] = Limit(text, 1024);
                    }
                }
                catch (Exception ex)
                {
                    log("State provider disabled after error: " + callback.Key + " / " + ex.Message);
                    lock (Providers)
                        if (Providers.TryGetValue(callback.Key, out var active) && active == callback.Value) Providers.Remove(callback.Key);
                }
            }
        }
    }
}
