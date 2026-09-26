using System;
using UnityEngine;

namespace LCReplay.Plugin
{
    /// <summary>
    /// Owns the Unity callbacks on a persistent object independent of BepInEx's shared plugin object.
    /// The plugin still owns recording, UI state, and shutdown.
    /// </summary>
    public sealed class ReplayRuntime : MonoBehaviour, IDisposable
    {
        private Action? runFrame, drawUi, started;
        private Action<string, Exception>? reportError;
        private bool disposed, firstFrame = true;
        private float nextFrameError, nextGuiError;

        internal static ReplayRuntime Create(Action runFrame, Action drawUi, Action started, Action<string, Exception> reportError)
        {
            var owner = new GameObject("LCReplay.Runtime");
            owner.SetActive(false);
            try
            {
                DontDestroyOnLoad(owner);
                var runtime = owner.AddComponent<ReplayRuntime>();
                runtime.runFrame = runFrame;
                runtime.drawUi = drawUi;
                runtime.started = started;
                runtime.reportError = reportError;
                owner.SetActive(true);
                return runtime;
            }
            catch
            {
                Destroy(owner);
                throw;
            }
        }

        private void Update()
        {
            if (disposed) return;
            try
            {
                if (firstFrame)
                {
                    firstFrame = false;
                    started?.Invoke();
                }
                runFrame?.Invoke();
            }
            catch (Exception ex) { Report("frame", ex, ref nextFrameError); }
        }

        private void OnGUI()
        {
            if (disposed) return;
            try { drawUi?.Invoke(); }
            catch (ExitGUIException) { throw; }
            catch (Exception ex) { Report("UI", ex, ref nextGuiError); }
        }

        private void Report(string stage, Exception exception, ref float nextAllowed)
        {
            var now = Time.realtimeSinceStartup;
            if (now < nextAllowed) return;
            nextAllowed = now + 10f;
            reportError?.Invoke(stage, exception);
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            runFrame = null;
            drawUi = null;
            started = null;
            reportError = null;
            // Scene/application teardown may destroy the native component before the
            // managed coordinator receives Application.quitting.
            if (this)
            {
                enabled = false;
                Destroy(gameObject);
            }
        }
    }
}
