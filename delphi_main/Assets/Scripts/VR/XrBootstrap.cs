using UnityEngine;
using UnityEngine.XR.Management;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Delphi.VR
{
    /// <summary>
    /// Starts the Varjo XR session deliberately, instead of letting XR Plug-in
    /// Management do it automatically on every Play press.
    ///
    /// WHY THIS EXISTS — THE COMPOSITOR IS A METRONOME, NOT A COST:
    ///
    ///   Once an XR session is live, the Varjo compositor decides when frames
    ///   are presented. The app no longer runs as fast as it can; it runs at
    ///   whatever rate the compositor paces it to. With the headset powered
    ///   off but the runtime still up, that pacing was a rock-steady 45 fps
    ///   (half of 90 Hz) — measured at 22.2 ms/frame against ~6 ms of main
    ///   thread, ~3.5 ms of render thread and 0.95 ms of GPU. Over half of
    ///   every frame was spent idle-waiting on a headset nobody was wearing.
    ///
    ///   That reads exactly like a performance bug and is not one. There is no
    ///   bottleneck to find: the work fits in the budget several times over.
    ///   Editor iteration was simply pinned to the headset's clock, which is
    ///   why the project ran at 200 fps before the XR integration and 45 after
    ///   it, with no change in what it was actually doing.
    ///
    /// BUILDS ARE UNAFFECTED. The editor-only branch below compiles out
    /// entirely, so a player always starts XR exactly as before. This changes
    /// when the headset is used, never whether it is supported — the Varjo
    /// loader stays configured and StartSubsystems is the same call XR
    /// Plug-in Management would have made.
    ///
    /// TO USE THE HEADSET IN THE EDITOR: DELPHI ▸ Start Headset (XR) in Play
    /// Mode. The setting is remembered per machine (EditorPrefs), not stored
    /// in the project, so it can't be committed and silently slow down anyone
    /// else's iteration.
    ///
    /// NOTE ON MEASURING VR PERFORMANCE: numbers taken with the headset off
    /// say nothing about headset-on performance. Powered off, almost nothing
    /// is actually rendered; powered on, the same scene is drawn to a
    /// 2674x2645 eye texture across multiple render passes. Any judgement
    /// about whether the real thing holds 90 Hz has to be made with the
    /// headset on and this toggle enabled.
    /// </summary>
    public static class XrBootstrap
    {
#if UNITY_EDITOR
        private const string MenuPath = "DELPHI/Start Headset (XR) in Play Mode";
        private const string EditorPrefKey = "Delphi.StartXrInPlayMode";

        private static bool StartXrInEditor
        {
            get => EditorPrefs.GetBool(EditorPrefKey, false);
            set => EditorPrefs.SetBool(EditorPrefKey, value);
        }

        [MenuItem(MenuPath)]
        private static void ToggleStartXr() => StartXrInEditor = !StartXrInEditor;

        [MenuItem(MenuPath, true)]
        private static bool ToggleStartXrValidate()
        {
            Menu.SetChecked(MenuPath, StartXrInEditor);
            return true;
        }
#endif

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Boot()
        {
#if UNITY_EDITOR
            if (!StartXrInEditor)
            {
                Debug.Log("[XrBootstrap] Headset OFF for this Play session — the frame rate is uncapped " +
                          "instead of paced by the Varjo compositor. Every VR component sees no XR device " +
                          "and takes its normal desktop path. Turn the headset on via the menu: " +
                          MenuPath + ".");
                return;
            }
#endif
            var manager = XRGeneralSettings.Instance != null ? XRGeneralSettings.Instance.Manager : null;
            if (manager == null)
            {
                Debug.LogWarning("[XrBootstrap] No XR manager configured — cannot start the headset. " +
                                 "Check Project Settings > XR Plug-in Management has the Varjo loader listed.");
                return;
            }

            // Already running (someone re-enabled Initialize XR on Startup, or
            // a domain reload left it up) — starting it twice is what produces
            // the "subsystem already running" spam, so leave it alone.
            if (manager.activeLoader != null)
            {
                Debug.Log("[XrBootstrap] XR was already initialised — leaving it alone.");
                return;
            }

            manager.InitializeLoaderSync();
            if (manager.activeLoader == null)
            {
                Debug.LogWarning("[XrBootstrap] No XR loader could start. Is Varjo Base running and the " +
                                 "headset connected? Continuing on the desktop path — gaze, hand tracking " +
                                 "and the in-headset panels will all be inactive.");
                return;
            }

            manager.StartSubsystems();
            Debug.Log($"[XrBootstrap] Headset session started via {manager.activeLoader.name}. Frame pacing " +
                      "is now the compositor's, so expect a fixed rate rather than an uncapped one.");
        }
    }
}
