using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace LCReplay.Plugin
{
    // Sibling-index paths identify objects in the same installed Unity scene.
    // They are used only when the game build matches the recording.
    internal static class SceneAssetPaths
    {
        internal const int MaxLength = 256;

        internal static bool IsValidReference(string path) =>
            !string.IsNullOrEmpty(path) && path.Length <= MaxLength;

        internal static string For(Component component)
        {
            var indices = new List<int>();
            for (var node = component.transform; node; node = node.parent)
                indices.Add(node.GetSiblingIndex());
            indices.Reverse();
            var siblings = string.Join("/", indices);
            var components = component.GetComponents(component.GetType());
            var componentIndex = Array.IndexOf(components, component);
            return siblings + ":" + component.GetType().Name + ":" + componentIndex;
        }

        internal static bool IsSupportedScene(Scene scene, string expectedName) =>
            scene.IsValid() && scene.isLoaded && scene.buildIndex >= 0 &&
            string.Equals(scene.name, expectedName, StringComparison.Ordinal);

        internal static bool IsStable(Renderer renderer)
        {
            if (!(renderer is MeshRenderer) || renderer.GetComponent<MeshFilter>() == null) return false;
            if (renderer.GetComponentInParent<Animator>() || renderer.GetComponentInParent<Rigidbody>()) return false;
            return true;
        }
    }
}
