using System;
using LCReplay.Core;

namespace LCReplay.Plugin
{
    // Explicit debug identifiers, not broad words such as "trigger" that may name real doors.
    internal static class RenderVisibilityPolicy
    {
        private static readonly string[] VegetationNames =
            { "tree", "grass", "leaf", "leaves", "bush", "flower", "forest", "foliage", "vine", "plant", "fern", "shrub", "moss" };
        internal static bool IsGroundSurface(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            return name.IndexOf("terrain", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("ground", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("landscape", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("splatmap", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        internal static bool IsVegetation(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            foreach (var word in VegetationNames)
                if (name.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        internal static bool IsDebugMaterial(string name) =>
            name.StartsWith("testTrigger", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("wireframe_", StringComparison.OrdinalIgnoreCase);

        internal static bool IsDebugObject(string name) =>
            name.StartsWith("ImpVis_", StringComparison.Ordinal) ||
            name.StartsWith("ImpGizmo_", StringComparison.Ordinal);

        // These are first-person helmet/visor effects in the live scene. They
        // follow the gameplay camera rather than representing world geometry.
        internal static bool IsFirstPersonOverlay(string name) =>
            string.Equals(name, "ScavengerHelmet", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "ScreenHelmetGoopPreload", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "VisorCracks", StringComparison.OrdinalIgnoreCase);

        internal static bool IsDebugGeometry(GeometrySnapshot geometry, System.Collections.Generic.ISet<string> debugMaterials)
        {
            if (IsDebugObject(geometry.Name) || IsFirstPersonOverlay(geometry.Name)) return true;
            if (geometry.MaterialIds.Count == 0) return false;
            foreach (var id in geometry.MaterialIds)
                if (id.Length == 0 || !debugMaterials.Contains(id)) return false;
            return true;
        }
    }
}
