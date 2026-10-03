using System;
using System.Linq;
using LCReplay.Core;
using LCReplay.Plugin.Capture;
using UnityEngine;

namespace LCReplay.Plugin.Playback
{
    internal static class NativeMaskState
    {
        internal static bool IsMask(EntitySnapshot entity) => entity.Kind == "item" &&
            (entity.Name.IndexOf("Comedy", StringComparison.OrdinalIgnoreCase) >= 0 ||
             entity.Name.IndexOf("Tragedy", StringComparison.OrdinalIgnoreCase) >= 0 ||
             entity.State.ContainsKey("attaching"));
        internal static bool Attaching(EntitySnapshot entity) => entity.Active &&
            ((entity.State.TryGetValue("attaching", out var flag) && bool.TryParse(flag, out var on) && on) ||
             (entity.State.TryGetValue("finishedAttaching", out flag) && bool.TryParse(flag, out on) && on));
        internal static bool IsEyes(GeometrySnapshot geometry)
        {
            var prefab = geometry.PrefabKey.Length == 0 ? null : PrefabAssetRegistry.ResolvePrefab(geometry.PrefabKey);
            var eyes = GameAccess.Read(prefab, "maskEyesFilled") as Renderer;
            if (!eyes && geometry.PrefabKey.Length == 0)
            {
                var type = GameAccess.Type("HauntedMaskItem");
                if (type != null)
                    eyes = Resources.FindObjectsOfTypeAll(type).OfType<Component>().Where(item => item && !item.gameObject.scene.IsValid())
                        .Select(item => GameAccess.Read(item, "maskEyesFilled") as Renderer)
                        .FirstOrDefault(renderer => renderer && renderer!.name == geometry.Name &&
                            (geometry.MeshName.Length == 0 || renderer.GetComponent<MeshFilter>()?.sharedMesh?.name == geometry.MeshName));
            }
            return eyes && (geometry.PrefabRendererPath.Length != 0 ?
                geometry.PrefabRendererPath == EntityTracker.RelativePath(prefab!.transform, eyes!.transform) : geometry.Name == eyes!.name) ||
                geometry.Name.IndexOf("EyesFilled", StringComparison.OrdinalIgnoreCase) >= 0 ||
                geometry.Name.IndexOf("MaskEyes", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
