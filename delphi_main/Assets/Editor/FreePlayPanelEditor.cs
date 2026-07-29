using Delphi.Session;
using UnityEditor;
using UnityEngine;

namespace Delphi.EditorTools
{
    /// <summary>
    /// Authoring UI for FreePlayPanel.
    ///
    /// The panel carries three mutually exclusive control surfaces, and the
    /// settings for the two you are not using are not merely noise — they are
    /// actively misleading. A researcher who tunes "Preset button height" while
    /// the panel is in single-slider mode has changed nothing, and there is no
    /// feedback anywhere to say so. So the mode picker comes first and
    /// everything below it is the mode's own settings, drawn to scale in
    /// centimetres rather than canvas pixels, because centimetres are what
    /// decide whether a tracked fingertip can hit the thing.
    /// </summary>
    [CustomEditor(typeof(FreePlayPanel))]
    public class FreePlayPanelEditor : UnityEditor.Editor
    {
        /// <summary>A fingertip lands within roughly a centimetre of where the
        /// tracker says it is, so anything much under two centimetres is a
        /// target you hit by luck rather than by aiming.</summary>
        private const float MinUsableTargetCm = 2.0f;

        /// <summary>Roughly how tall a press target has to be before a fingertip
        /// stops sliding off it mid-poke. Above the bare-minimum figure above,
        /// because "hittable" and "does not cancel while you press it" are two
        /// different bars and only the second one matters to a participant.</summary>
        private const float ComfortableTargetCm = 5.0f;

        /// <summary>Speed of a deliberate poke at a small target, in m/s.
        /// People poke small targets FASTER than large ones, which is why
        /// shrinking a button and its touch depth together fails twice over.
        /// VrPokeUi rejects anything over 2.5 m/s as a swipe, so this sits well
        /// inside what the driver already considers a press.</summary>
        private const float DeliberatePokeSpeed = 1.2f;

        private static readonly string[] DefaultPresets =
            { "SLOTH", "CHILL", "STANDARD", "HURRY", "MAD MAX" };

        private static bool _showPlacement = true;
        private static bool _showLegacy;

        public override void OnInspectorGUI()
        {
            var p = (FreePlayPanel)target;
            serializedObject.Update();

            DrawModePicker();

            // APPLIED IMMEDIATELY, before anything reads the mode back off the
            // component. PropertyField writes into the SerializedObject's copy,
            // so until this runs `p.controlMode` still holds the value from
            // before the dropdown was changed — and every section below, plus
            // WidthMeters/HeightMeters, branches on exactly that. Without this
            // the Inspector spends a pass showing the old mode's settings under
            // the new mode's name.
            serializedObject.ApplyModifiedProperties();
            serializedObject.Update();

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Links", EditorStyles.boldLabel);
            Field("session");
            Field("anchorOverride");
            Field("participantCamera");

            EditorGUILayout.Space(8);
            switch (p.controlMode)
            {
                case FreePlayPanel.ControlMode.DrivingStyleSlider: DrawStyleSlider(p); break;
                case FreePlayPanel.ControlMode.StylePresets:       DrawPresets(p);     break;
                default:                                          DrawPerParameter(p); break;
            }

            EditorGUILayout.Space(8);
            DrawPlacement(p);

            EditorGUILayout.Space(8);
            DrawLegacy(p);

            serializedObject.ApplyModifiedProperties();
        }

        // ── Mode ────────────────────────────────────────────────────────

        private void DrawModePicker()
        {
            var mode = serializedObject.FindProperty("controlMode");
            EditorGUILayout.PropertyField(mode, new GUIContent("Control mode",
                "Which surface the participant is given. Switching hides the " +
                "other modes' settings; it never deletes them."));

            string blurb = (FreePlayPanel.ControlMode)mode.enumValueIndex switch
            {
                FreePlayPanel.ControlMode.DrivingStyleSlider =>
                    "ONE SLIDER. Every driving parameter takes the slider's value, so 0.5 means " +
                    "0.5 on all four. Asks the participant only how assertive they want the car " +
                    "to be, which is the thing they have an opinion about — at the cost of ever " +
                    "learning that they wanted gentle braking with brisk acceleration.",

                FreePlayPanel.ControlMode.StylePresets =>
                    "NAMED LEVELS. The options are spread evenly over 0..1 — first at 0, last at 1 — " +
                    "and pressing one sets every parameter to that value. Faster to answer and " +
                    "directly comparable between participants; nothing between two levels is " +
                    "reachable, which is the point.",

                _ =>
                    "FOUR SLIDERS, one per driving parameter — the original panel. The only mode " +
                    "that can record a participant wanting the parameters at DIFFERENT values.",
            };
            EditorGUILayout.HelpBox(blurb, MessageType.None);
        }

        // ── Mode: per parameter ─────────────────────────────────────────

        private void DrawPerParameter(FreePlayPanel p)
        {
            EditorGUILayout.LabelField("Per-parameter sliders", EditorStyles.boldLabel);
            Field("sizeMeters", "Panel size (m)",
                  "Width and height are authored separately here, so this panel keeps the exact " +
                  "proportions it has always had.");
            Sync();

            var s = p.sizeMeters;
            EditorGUILayout.LabelField(" ", $"→ {s.x:0.00} × {s.y:0.00} m · 4 rows",
                                       EditorStyles.miniLabel);

            if (s.x <= 0f || s.y <= 0f)
                EditorGUILayout.HelpBox("A zero or negative dimension collapses or mirrors the canvas — " +
                                        "the panel simply will not appear.", MessageType.Error);
        }

        // ── Mode: driving-style slider ──────────────────────────────────

        private void DrawStyleSlider(FreePlayPanel p)
        {
            EditorGUILayout.LabelField("Driving style slider", EditorStyles.boldLabel);

            Field("styleWidthMeters", "Panel width (m)");
            Field("styleLowLabel", "Left end label");
            Field("styleHighLabel", "Right end label");
            Field("styleSteps", "Detents");
            Sync();

            EditorGUILayout.LabelField(" ", $"→ {p.WidthMeters:0.00} × {p.HeightMeters:0.00} m panel",
                                       EditorStyles.miniLabel);

            float mPerPx = Mathf.Max(0.001f, p.WidthMeters) / FreePlayPanel.PixelWidth;
            // Usable width less the knob, which the track insets by half its
            // diameter at each end so the knob never overhangs the panel.
            float trackCm = (FreePlayPanel.UsableWidthPixels - 68f) * mPerPx * 100f;
            float rowCm = 92f * mPerPx * 100f;

            EditorGUILayout.LabelField(" ", $"Track {trackCm:0.0} cm long · {rowCm:0.0} cm of touch height",
                                       EditorStyles.miniLabel);

            if (p.styleSteps > 1)
            {
                float detentCm = trackCm / (p.styleSteps - 1);
                EditorGUILayout.LabelField(" ", $"{p.styleSteps} detents, {detentCm:0.0} cm apart, " +
                                                $"{1f / (p.styleSteps - 1):0.00} of style each",
                                           EditorStyles.miniLabel);
                if (detentCm < 1.0f)
                    EditorGUILayout.HelpBox("Detents under about a centimetre apart are finer than " +
                                            "hand-tracking jitter, so the participant cannot reliably " +
                                            "choose between neighbours. The slider snaps, so the value " +
                                            "is still clean — it just will not be the one they aimed at.",
                                            MessageType.Warning);
            }
            else
            {
                EditorGUILayout.HelpBox("Continuous. Detents (2 or more) make a setting repeatable " +
                                        "between participants; continuous lets them find a value they " +
                                        "could not have named.", MessageType.None);
            }

            if (rowCm < MinUsableTargetCm)
                EditorGUILayout.HelpBox($"At {rowCm:0.0} cm the touch row is thinner than hand-tracking " +
                                        "jitter — the slider will be hard to engage at all. Widen the panel.",
                                        MessageType.Error);

            EditorGUILayout.HelpBox(
                "Writes SessionController.SetFreePlayStyle — one applied state and one free-play " +
                "log row per change, not four.", MessageType.None);
        }

        // ── Mode: style presets ─────────────────────────────────────────

        private void DrawPresets(FreePlayPanel p)
        {
            EditorGUILayout.LabelField("Style presets", EditorStyles.boldLabel);

            Field("presetsWidthMeters", "Panel width (m)");

            EditorGUILayout.Space(4);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("stylePresets"),
                new GUIContent("Options (defensive → aggressive)",
                    "Use +/- to add and remove, and drag the handles to reorder. The values " +
                    "below re-space themselves automatically."), true);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(new GUIContent("Reset to the five defaults",
                        "SLOTH · CHILL · STANDARD · HURRY · MAD MAX")))
                {
                    var list = serializedObject.FindProperty("stylePresets");
                    list.ClearArray();
                    for (int i = 0; i < DefaultPresets.Length; i++)
                    {
                        list.InsertArrayElementAtIndex(i);
                        list.GetArrayElementAtIndex(i).stringValue = DefaultPresets[i];
                    }
                }
                if (GUILayout.Button(new GUIContent("Upper-case all",
                        "The panel does not force case, so mixed entries stay mixed.")))
                {
                    var list = serializedObject.FindProperty("stylePresets");
                    for (int i = 0; i < list.arraySize; i++)
                    {
                        var e = list.GetArrayElementAtIndex(i);
                        e.stringValue = (e.stringValue ?? string.Empty).ToUpperInvariant();
                    }
                }
            }

            Sync();

            EditorGUILayout.Space(6);
            DrawPresetMapping(p);

            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Buttons", EditorStyles.boldLabel);
            Field("presetButtonHeightPixels", "Button height (px)");
            Field("presetMinButtonWidthPixels", "Min button width (px)");
            Field("presetHitPaddingPixels", "Hit padding (px)");
            Field("presetTouchDepthMeters", "Touch depth (m)");
            Field("presetMinPressSeconds", "Min press time (s)");
            Field("presetRepeatLockSeconds", "Repeat lock (s)");
            Field("presetValueCaption", "Readout caption");
            Sync();

            DrawPresetGeometry(p);
        }

        /// <summary>The whole point of the mode, spelled out: which name maps
        /// to which number. Getting this wrong is silent — the session file
        /// records 0.75 and nobody can say afterwards whether the participant
        /// was shown four options or five.</summary>
        private static void DrawPresetMapping(FreePlayPanel p)
        {
            var labels = FreePlayPanel.SanitisePresets(p.stylePresets);

            if (p.stylePresets == null || p.stylePresets.Count == 0)
                EditorGUILayout.HelpBox("No options listed. The panel falls back to a single STANDARD " +
                                        "button at 0.50 rather than showing nothing.", MessageType.Warning);
            else if (labels.Length != p.stylePresets.Count)
                EditorGUILayout.HelpBox("Blank entries are skipped at build time, so the values below " +
                                        "are what the participant actually gets.", MessageType.Info);

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                for (int i = 0; i < labels.Length; i++)
                {
                    float v = FreePlayPanel.PresetValue(i, labels.Length);
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        EditorGUILayout.LabelField(labels[i], EditorStyles.miniBoldLabel, GUILayout.Width(140));
                        var bar = GUILayoutUtility.GetRect(60, 12, GUILayout.ExpandWidth(true));
                        EditorGUI.DrawRect(bar, new Color(1f, 1f, 1f, 0.06f));
                        EditorGUI.DrawRect(new Rect(bar.x, bar.y, bar.width * v, bar.height),
                                           new Color(0.24f, 0.83f, 0.76f, 0.75f));
                        EditorGUILayout.LabelField(v.ToString("0.00"), EditorStyles.miniLabel,
                                                   GUILayout.Width(38));
                    }
                }
            }

            if (labels.Length == 1)
                EditorGUILayout.HelpBox("A single option sits at 0.50 — there is no scale for it to be " +
                                        "an end of, and putting it at 0 would silently pin the car to " +
                                        "its gentlest setting.", MessageType.Info);
            else
                EditorGUILayout.LabelField(" ", $"{labels.Length} options · " +
                                                $"{1f / (labels.Length - 1):0.00} of style apart",
                                           EditorStyles.miniLabel);
        }

        /// <summary>Canvas pixels mean nothing on a world-space panel: the same
        /// 84 px button is 7.4 cm tall at 0.56 m wide and 3.7 cm at 0.28 m, and
        /// only one of those is hittable with a tracked fingertip.</summary>
        private static void DrawPresetGeometry(FreePlayPanel p)
        {
            var labels = FreePlayPanel.SanitisePresets(p.stylePresets);
            int cols = FreePlayPanel.PresetColumns(labels.Length, p.presetMinButtonWidthPixels);
            int rows = Mathf.CeilToInt(labels.Length / (float)cols);

            float mPerPx = Mathf.Max(0.001f, p.presetsWidthMeters) / FreePlayPanel.PixelWidth;
            float gap = FreePlayPanel.PresetGapPixels;
            float btnWpx = (FreePlayPanel.UsableWidthPixels - (cols - 1) * gap) / cols;
            float btnHpx = Mathf.Max(FreePlayPanel.MinPresetButtonHeightPixels, p.presetButtonHeightPixels);
            float wCm = btnWpx * mPerPx * 100f;
            float hCm = btnHpx * mPerPx * 100f;

            float padPx = Mathf.Clamp(p.presetHitPaddingPixels, 0f, gap * 0.5f);
            float padCm = padPx * mPerPx * 100f;

            EditorGUILayout.LabelField(" ", $"→ {p.WidthMeters:0.00} × {p.HeightMeters:0.00} m panel · " +
                                            $"{rows} row{(rows == 1 ? "" : "s")} of {cols}",
                                       EditorStyles.miniLabel);
            EditorGUILayout.LabelField(" ", $"Each button {wCm:0.0} × {hCm:0.0} cm, " +
                                            $"+{padCm:0.0} cm of invisible hit area all round",
                                       EditorStyles.miniLabel);

            if (padPx < p.presetHitPaddingPixels)
                EditorGUILayout.HelpBox($"Hit padding is clamped to {padPx:0} px — half the gap between " +
                                        "buttons. Any more and neighbouring hit areas overlap, so a press " +
                                        "in the overlap lands on whichever button was registered first " +
                                        "rather than the one under the finger.", MessageType.Warning);

            if (btnHpx > p.presetButtonHeightPixels)
                EditorGUILayout.HelpBox(
                    $"Button height is clamped up to {FreePlayPanel.MinPresetButtonHeightPixels:0} px — " +
                    $"{p.presetButtonHeightPixels:0} px is below the runtime floor, so the panel is not " +
                    "the size this field says.", MessageType.Info);

            if (Mathf.Min(wCm, hCm) + 2f * padCm < MinUsableTargetCm)
                EditorGUILayout.HelpBox("A press target under about 2 cm is smaller than hand-tracking " +
                                        "jitter — it will feel broken rather than small. Widen the panel, " +
                                        "raise the button height, or use fewer options.", MessageType.Error);

            // A press must stay INSIDE the button while it is inside the band.
            // A short button is not merely a small target: a fingertip that
            // drifts off it vertically mid-poke CANCELS instead of firing, so
            // the button lights up and then quietly does nothing.
            else if (hCm + 2f * padCm < ComfortableTargetCm)
                EditorGUILayout.HelpBox(
                    $"At {hCm + 2f * padCm:0.0} cm the press target is barely taller than tracking jitter. " +
                    "A fingertip that drifts off the button while it is at the surface CANCELS rather " +
                    "than fires — the button lights up on the way in and nothing happens. About " +
                    $"{ComfortableTargetCm:0} cm is the point where that stops happening.",
                    MessageType.Warning);

            DrawPressTiming(p);

            EditorGUILayout.HelpBox(
                "Buttons fire on RELEASE and the lit one always reflects the CAR, not the last press — " +
                "so if anything else moves the parameters, the group stops claiming a level the " +
                "participant is not driving.", MessageType.None);
        }

        /// <summary>Touch depth and minimum press time reported as the one
        /// thing they jointly decide: the fastest poke that still counts.
        ///
        /// Separately both fields look harmless — 20 mm is a reasonable-sounding
        /// tolerance and 50 ms is a reasonable-sounding debounce. Together they
        /// mean any poke over 0.8 m/s is discarded, which is most deliberate
        /// presses at a small target. Neither field can show that on its own,
        /// which is exactly why this line exists.</summary>
        private static void DrawPressTiming(FreePlayPanel p)
        {
            float bandCm = 2f * p.presetTouchDepthMeters * 100f;
            float fastest = FreePlayPanel.FastestRegisteringPoke(p.presetTouchDepthMeters,
                                                                 p.presetMinPressSeconds);

            EditorGUILayout.LabelField(" ", $"Touch band {bandCm:0.0} cm thick · " +
                                            $"registers pokes up to {fastest:0.0} m/s",
                                       EditorStyles.miniLabel);

            if (p.presetTouchDepthMeters < 0.015f)
                EditorGUILayout.HelpBox("Under 15 mm of touch depth asks the participant to judge depth " +
                                        "precisely, which is what hand tracking is worst at. Presses will " +
                                        "be missed.", MessageType.Warning);

            if (fastest < DeliberatePokeSpeed)
                EditorGUILayout.HelpBox(
                    $"Only pokes SLOWER than {fastest:0.0} m/s can register. A deliberate press at a " +
                    $"small target runs about {DeliberatePokeSpeed:0.0} m/s, so most presses will light " +
                    "the button on the way in and then be discarded as an accidental brush.\n\n" +
                    "The finger has to be measured inside the touch band for the minimum press time, and " +
                    "a fast finger crosses a thin band between frames. Fix it from either end: raise " +
                    $"Touch depth to {Mathf.Ceil(DeliberatePokeSpeed * p.presetMinPressSeconds * 0.5f * 1000f) / 1000f:0.000} m, " +
                    $"or lower Min press time to {Mathf.Floor(2f * p.presetTouchDepthMeters / DeliberatePokeSpeed * 1000f) / 1000f:0.000} s.",
                    MessageType.Error);
            else if (fastest < DeliberatePokeSpeed * 1.4f)
                EditorGUILayout.HelpBox(
                    $"{fastest:0.0} m/s is not much headroom over a normal press. If the odd poke does " +
                    "nothing, this pair is the reason before anything else is.", MessageType.Warning);

            if (p.presetRepeatLockSeconds > 1.0f)
                EditorGUILayout.HelpBox($"A {p.presetRepeatLockSeconds:0.00} s repeat lock is shared by " +
                                        "EVERY button — a participant changing their mind twice in quick " +
                                        "succession will find the second press ignored.", MessageType.Warning);
        }

        // ── Shared ──────────────────────────────────────────────────────

        private void DrawPlacement(FreePlayPanel p)
        {
            _showPlacement = EditorGUILayout.Foldout(_showPlacement, "Placement", true, EditorStyles.foldoutHeader);
            if (!_showPlacement) return;

            using (new EditorGUI.IndentLevelScope())
            {
                EditorGUILayout.LabelField("Desktop — relative to the participant camera", EditorStyles.miniBoldLabel);
                Field("localPosition");
                Field("localEulerAngles");

                EditorGUILayout.Space(4);
                EditorGUILayout.LabelField("VR — relative to the driver's SEAT", EditorStyles.miniBoldLabel);
                Field("vrLocalPosition");
                Field("vrLocalEulerAngles");

                EditorGUILayout.HelpBox(
                    "In VR the panel anchors to the seat, never the head: a panel welded to the head " +
                    "retreats exactly as fast as the finger approaches, so it can never be touched.",
                    MessageType.None);

                // The VR placement is only used when the legacy Ultraleap
                // prefab field is also filled in — a coupling that is easy to
                // trip over and impossible to see from these fields alone.
                if (p.usePhysicalSlidersInVr && p.physicalSliderPrefab == null)
                    EditorGUILayout.HelpBox(
                        "The VR placement above is only applied when 'Physical Slider Prefab' " +
                        "(under Legacy) is also assigned — that flag is what selects the seat-mounted " +
                        "layout. With it empty the panel uses the DESKTOP placement even in the headset.",
                        MessageType.Warning);
            }
        }

        private void DrawLegacy(FreePlayPanel p)
        {
            _showLegacy = EditorGUILayout.Foldout(_showLegacy, "Legacy — Ultraleap physical-slider path", true,
                                                  EditorStyles.foldoutHeader);
            if (!_showLegacy) return;

            using (new EditorGUI.IndentLevelScope())
            {
                EditorGUILayout.HelpBox(
                    "All three modes build their own fingertip controls (VrTouchSlider / VrTouchButton) " +
                    "rather than Ultraleap's rigid-body prefabs. These fields survive from that earlier " +
                    "path; only the two on top still do anything, and only by selecting the VR placement.",
                    MessageType.None);
                Field("usePhysicalSlidersInVr");
                Field("physicalSliderPrefab");
                Field("sliderTravelMeters");
                Field("sliderSpacingMeters");
            }
        }

        /// <summary>Pushes what has been edited so far onto the component, so
        /// the diagnostics drawn below read this frame's values rather than
        /// last frame's. Every "→ 0.56 × 0.23 m" line under a field would
        /// otherwise describe the panel as it was BEFORE the number above it
        /// was typed, which is exactly the reading that makes someone type it
        /// again.</summary>
        private void Sync()
        {
            serializedObject.ApplyModifiedProperties();
            serializedObject.Update();
        }

        private void Field(string name, string label = null, string tooltip = null)
        {
            var prop = serializedObject.FindProperty(name);
            if (prop == null) return;
            if (label == null) EditorGUILayout.PropertyField(prop, true);
            else EditorGUILayout.PropertyField(prop, new GUIContent(label, tooltip), true);
        }
    }
}
