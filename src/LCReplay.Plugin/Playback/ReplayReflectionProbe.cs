using System;
using System.Reflection;
using LCReplay.Plugin.Capture;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace LCReplay.Plugin.Playback
{
    internal static class ReplayReflectionProbe
    {
        internal static GameObject? Copy(ReflectionProbe source, Transform? parent, Scene destination, int layer)
        {
            if (!source || !source.enabled || !source.gameObject.activeInHierarchy) return null;
            var hdType = GameAccess.Type("UnityEngine.Rendering.HighDefinition.HDAdditionalReflectionData");
            var original = hdType == null ? null : source.GetComponent(hdType);
            if (original is Behaviour behaviour && !behaviour.enabled) return null;
            var mode = original ? GameAccess.Read(original, "mode")?.ToString() : source.mode.ToString();
            if (mode != "Baked" && mode != "Custom") return null;
            var texture = original ? GameAccess.Read(original, "texture") as Texture :
                source.mode == ReflectionProbeMode.Custom ? source.customBakedTexture : source.bakedTexture;
            // Never retain an updating render target or trigger a realtime bake.
            if (!texture || texture is RenderTexture || texture!.dimension != TextureDimension.Cube) return null;
            GameObject? copy = null;
            try
            {
                copy = new GameObject("Replay reflection " + source.name) { layer = layer, hideFlags = HideFlags.DontSave };
                copy.SetActive(false);
                if (destination.IsValid()) SceneManager.MoveGameObjectToScene(copy, destination);
                copy.transform.SetPositionAndRotation(source.transform.position, source.transform.rotation);
                copy.transform.localScale = source.transform.lossyScale;
                if (parent) copy.transform.SetParent(parent, true);
                var target = copy.AddComponent<ReflectionProbe>();
                target.mode = ReflectionProbeMode.Custom;
                target.refreshMode = ReflectionProbeRefreshMode.ViaScripting;
                target.customBakedTexture = texture;
                target.size = source.size; target.center = source.center;
                target.boxProjection = source.boxProjection; target.blendDistance = source.blendDistance;
                target.intensity = source.intensity; target.importance = source.importance;
                target.cullingMask = 0;
                if (original)
                {
                    var data = copy.AddComponent(hdType!);
                    // HDRP's nested influence/proxy data is serialized. Copying
                    // JSON gives the clone its own settings without mutating a
                    // live moon probe's managed InfluenceVolume instance.
                    JsonUtility.FromJsonOverwrite(JsonUtility.ToJson(original), data);
                    SetEnum(data, "mode", "Custom");
                    Set(data, "customTexture", texture);
                    var renderData = GameAccess.Read(original, "renderData");
                    if (renderData != null) Set(data, "customRenderData", renderData);
                    if (GameAccess.Read(original, "proxyVolume") is Component proxy && proxy)
                    {
                        var proxyObject = new GameObject("Replay reflection proxy") { layer = layer, hideFlags = HideFlags.DontSave };
                        proxyObject.SetActive(false);
                        proxyObject.transform.SetPositionAndRotation(proxy.transform.position, proxy.transform.rotation);
                        proxyObject.transform.localScale = proxy.transform.lossyScale;
                        proxyObject.transform.SetParent(copy.transform, true);
                        var proxyCopy = proxyObject.AddComponent(proxy.GetType());
                        JsonUtility.FromJsonOverwrite(JsonUtility.ToJson(proxy), proxyCopy);
                        Set(data, "proxyVolume", proxyCopy);
                        proxyObject.SetActive(true);
                    }
                    if (data is Behaviour enabled) enabled.enabled = true;
                }
                target.mode = ReflectionProbeMode.Custom;
                target.refreshMode = ReflectionProbeRefreshMode.ViaScripting;
                target.cullingMask = 0;
                copy.SetActive(true);
                return copy;
            }
            catch
            {
                if (copy) { copy!.SetActive(false); Object.Destroy(copy); }
                return null;
            }
        }

        private static void Set(Component target, string name, object value)
        {
            var property = target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (property?.CanWrite == true) property.SetValue(target, value, null);
        }
        private static void SetEnum(Component target, string name, string value)
        {
            var property = target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (property?.CanWrite == true && property.PropertyType.IsEnum)
                property.SetValue(target, Enum.Parse(property.PropertyType, value), null);
        }
    }
}
