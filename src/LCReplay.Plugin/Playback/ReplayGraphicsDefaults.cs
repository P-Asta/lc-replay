using UnityEngine;

namespace LCReplay.Plugin.Playback
{
    // HDLethalCompany's documented vanilla raster size. This is independent
    // of the desktop window; the replay HUD still renders at window resolution.
    internal static class ReplayGraphicsDefaults
    {
        internal const float MinResolutionMultiplier = .25f;
        internal const float MaxResolutionMultiplier = 4.5f;
        internal static int ResolutionWidth(float multiplier) => Mathf.RoundToInt(860f * multiplier);
        internal static int ResolutionHeight(float multiplier) => Mathf.RoundToInt(520f * multiplier);
    }
}
