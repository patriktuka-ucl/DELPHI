using Delphi.Session;
using Delphi.VR;
using UnityEditor;
using UnityEngine;

namespace Delphi.EditorTools
{
    /// <summary>
    /// Authoring UI for VrQuestionnairePanel: where the popup sits relative to
    /// the driver's eye point, drawn to scale in the Scene view and draggable
    /// with a handle.
    ///
    /// Placement is three numbers in a header, which makes it look like a
    /// detail. It is not. The panel is answered with a fingertip from a
    /// belted-in seat, so a few centimetres too far forward puts SUBMIT out of
    /// reach, a few too close makes it impossible to focus on, and too high
    /// hides the road the participant is about to be asked about. None of that
    /// is visible in a Vector3 field, and the only other way to find it is to
    /// guess, put the headset on, take it off, and guess again. So this draws
    /// the real rectangle at the real size against the real seat, reports
    /// reach and view angle in words, and lets you drag it.
    /// </summary>
    [CustomEditor(typeof(VrQuestionnairePanel))]
    public class VrQuestionnairePanelEditor : UnityEditor.Editor
    {
        // Comfortable seated fingertip reach, measured from the eye point
        // rather than the shoulder — that is the frame the placement fields
        // are in. Beyond this a belted participant has to lean.
        private const float ComfortableReach = 0.72f;
        private const float MaxReach         = 0.85f;
        // Closer than this and a headset wearer cannot converge on the text.
        private const float MinFocus         = 0.30f;
        // Not a design limit — the only width that genuinely cannot be drawn.
        private const float MinWidth         = VrQuestionnairePanel.MinWidthMeters;

        private static readonly Vector3 DefaultPosition = new Vector3(0f, 0.05f, 0.46f);
        private static readonly Vector3 DefaultEuler    = new Vector3(16f, 0f, 0f);
        private const float DefaultWidth = 0.62f;

        private static readonly Color Accent = new Color(0.24f, 0.83f, 0.76f);
        private static readonly Color Plate  = new Color(0.055f, 0.065f, 0.085f, 0.92f);
        private static readonly Color Muted  = new Color(0.72f, 0.77f, 0.86f);

        private enum HandleMode { Move, Rotate, Off }
        private static HandleMode _mode = HandleMode.Move;
        private static bool _drawPreview = true;

        private Transform _cachedAnchor;

        private void OnDisable()
        {
            Tools.hidden = false;
            _cachedAnchor = null;
        }

        /// <summary>The seat (or, out of play mode, the camera standing in for
        /// it). Cached because resolving it outside play mode means scanning
        /// the scene for the rig, and this runs on every repaint; in play mode
        /// the rig hands it over directly and it is re-read each time, since
        /// the real seat node only exists from Awake onwards.</summary>
        private Transform Anchor()
        {
            var p = (VrQuestionnairePanel)target;
            if (Application.isPlaying || _cachedAnchor == null) _cachedAnchor = p.ResolveAnchor();
            return _cachedAnchor;
        }

        public override void OnInspectorGUI()
        {
            var p = (VrQuestionnairePanel)target;

            DrawWiring(p);

            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Content", EditorStyles.boldLabel);
            serializedObject.Update();
            EditorGUILayout.PropertyField(serializedObject.FindProperty("questionnaire"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("title"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("hideOnSubmit"));
            serializedObject.ApplyModifiedProperties();

            if (p.questionnaire == null && p.GetComponent<DelphiQuestionnaire>() == null)
                EditorGUILayout.HelpBox(
                    "No questionnaire linked and none on this GameObject — this panel " +
                    "will log an error and show nothing. Add a DelphiQuestionnaire " +
                    "component here, or drag one in.", MessageType.Error);

            EditorGUILayout.Space(8);
            DrawPlacement(p);

            EditorGUILayout.Space(8);
            DrawButtons(p);

            EditorGUILayout.Space(8);
            DrawDiagnostics(p);
        }

        /// <summary>The button geometry, reported in centimetres as well as
        /// canvas pixels. Pixels on a world-space canvas mean nothing on their
        /// own — the same 80 px button is 5.5 cm tall on a 0.62 m panel and
        /// 2.7 cm on a 0.30 m one, and only one of those is hittable with a
        /// tracked fingertip.</summary>
        private void DrawButtons(VrQuestionnairePanel p)
        {
            EditorGUILayout.LabelField("Buttons", EditorStyles.boldLabel);

            serializedObject.Update();
            EditorGUILayout.PropertyField(serializedObject.FindProperty("buttonHeightPixels"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("backWidthPixels"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("nextWidthPixels"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("buttonHitPaddingPixels"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("buttonTouchDepthMeters"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("buttonRepeatLockSeconds"));
            serializedObject.ApplyModifiedProperties();

            EditorGUILayout.HelpBox(
                "Buttons fire on RELEASE — the finger lights the button on the way in and the " +
                "press lands when it leaves. A poke goes through the panel and back out, so " +
                "firing on contact would count one poke twice.", MessageType.None);

            float mPerPx = Mathf.Max(MinWidth, p.widthMeters) / VrQuestionnairePanel.PixelWidth;
            float h = p.buttonHeightPixels * mPerPx * 100f;
            float wBack = p.backWidthPixels * mPerPx * 100f;
            float wNext = p.nextWidthPixels * mPerPx * 100f;
            float pad = p.buttonHitPaddingPixels * mPerPx * 100f;

            EditorGUILayout.LabelField(
                $"BACK {wBack:0.0} × {h:0.0} cm · NEXT {wNext:0.0} × {h:0.0} cm · " +
                $"+{pad:0.0} cm of invisible hit area all round",
                EditorStyles.miniLabel);

            // A fingertip lands within roughly a centimetre of where the
            // tracker says it is, so anything under about two centimetres is a
            // target you hit by luck.
            if (h + 2f * pad < 2.0f || Mathf.Min(wBack, wNext) + 2f * pad < 2.0f)
                EditorGUILayout.HelpBox("A press target under about 2 cm is smaller than hand-tracking " +
                                        "jitter — it will feel broken rather than small.", MessageType.Error);

            if (p.buttonTouchDepthMeters < 0.015f)
                EditorGUILayout.HelpBox("Under 15 mm of touch depth asks the participant to judge depth " +
                                        "precisely, which is what hand tracking is worst at. Presses " +
                                        "will be missed.", MessageType.Warning);

            if (p.buttonHeightPixels > 0f && p.buttonHeightPixels < 24f)
                EditorGUILayout.HelpBox("Heights below 24 px are clamped at build time.", MessageType.Info);
        }

        /// <summary>Says out loud which session slot this panel fills. Two
        /// panels that look identical in the hierarchy drive completely
        /// different parts of the study, and wiring the wrong one is silent
        /// until the optimizer gets objectives it did not expect.</summary>
        private static void DrawWiring(VrQuestionnairePanel p)
        {
            var session = Object.FindFirstObjectByType<SessionController>();
            if (session == null)
            {
                EditorGUILayout.HelpBox("No SessionController in the scene, so this panel is " +
                                        "not driven by anything yet.", MessageType.Warning);
                return;
            }

            string role =
                session.delphiQuestionnairePanel == p    ? "PER-TRIAL panel (Explicit condition) — shown after every measured iteration; its answers become the optimizer's objectives." :
                session.conditionEvaluationPanel == p    ? "CONDITION-EVALUATION panel — shown once at the end of each condition; recorded, not optimized." :
                null;

            if (role != null) EditorGUILayout.HelpBox(role, MessageType.Info);
            else EditorGUILayout.HelpBox(
                "This panel is not assigned to the SessionController. Drag it into either " +
                "'Delphi Questionnaire Panel' (per trial) or 'Condition Evaluation Panel' " +
                "on " + session.name + ", or it will never be shown.", MessageType.Warning);
        }

        private void DrawPlacement(VrQuestionnairePanel p)
        {
            EditorGUILayout.LabelField("Placement — relative to the driver's eye point", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "+Z is straight ahead, +Y is up, all in metres. The panel is anchored to the " +
                "SEAT, never to the head, so these numbers do not move when the participant " +
                "looks around. Drag the handle in the Scene view for the same effect.",
                MessageType.None);

            // UNBOUNDED FIELDS, NOT SLIDERS. A slider's ends are a claim about
            // what is reasonable, and the rig keeps proving that claim wrong —
            // seat height, panel size and where a given participant can
            // comfortably look all move the usable window around. Any bound
            // wide enough to never be in the way is not doing anything anyway.
            // The guidance lives in the warnings below instead, where it can be
            // read and overruled rather than silently enforced.
            //
            // Drag the label to scrub, or type an exact value.
            EditorGUI.BeginChangeCheck();

            Vector3 pos = p.localPosition;
            pos.z = EditorGUILayout.FloatField(new GUIContent("Distance ahead (m)",
                        "How far in front of the eyes. Reach runs out around " +
                        ComfortableReach.ToString("0.00") + " m, but nothing stops you."), pos.z);
            pos.y = EditorGUILayout.FloatField(new GUIContent("Height (m)",
                        "Above (+) or below (-) the eye line."), pos.y);
            pos.x = EditorGUILayout.FloatField(new GUIContent("Sideways (m)",
                        "Right (+) or left (-). Usually 0 — off-centre favours one arm."), pos.x);

            Vector3 eul = p.localEulerAngles;
            eul.x = EditorGUILayout.FloatField(new GUIContent("Tilt / pitch (°)",
                        "Positive tips the top away, like a lectern."), eul.x);
            eul.y = EditorGUILayout.FloatField(new GUIContent("Turn / yaw (°)", "Positive turns right."), eul.y);
            eul.z = EditorGUILayout.FloatField(new GUIContent("Roll (°)", "Usually 0."), eul.z);

            float width = EditorGUILayout.FloatField(new GUIContent("Width (m)",
                        "Height follows automatically — the page is a fixed shape, scaled " +
                        "uniformly, so nothing is ever stretched."), p.widthMeters);

            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(p, "Move questionnaire panel");
                p.localPosition = pos; p.localEulerAngles = eul; p.widthMeters = width;
                EditorUtility.SetDirty(p);
            }

            EditorGUILayout.LabelField(" ", $"→ {width:0.00} m × {p.HeightMeters:0.00} m panel",
                                       EditorStyles.miniLabel);

            // The one value that cannot be left alone: a zero or negative width
            // collapses or mirrors the whole canvas, and the panel would just
            // vanish with no error to explain it.
            if (width <= 0f)
                EditorGUILayout.HelpBox("Width must be greater than zero — at or below it the panel " +
                                        "collapses to nothing. The runtime falls back to 1 mm.",
                                        MessageType.Error);

            EditorGUILayout.Space(4);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(new GUIContent("Face the eye point",
                        "Rotates the panel square to the line of sight, removing any tilt.")))
                {
                    Undo.RecordObject(p, "Aim questionnaire panel");
                    Vector3 dir = p.localPosition.sqrMagnitude > 1e-6f ? p.localPosition : Vector3.forward;
                    p.localEulerAngles = Quaternion.LookRotation(dir.normalized, Vector3.up).eulerAngles;
                    EditorUtility.SetDirty(p);
                }
                if (GUILayout.Button(new GUIContent("Reset", "Back to the shipped placement.")))
                {
                    Undo.RecordObject(p, "Reset questionnaire panel");
                    p.localPosition = DefaultPosition;
                    p.localEulerAngles = DefaultEuler;
                    p.widthMeters = DefaultWidth;
                    EditorUtility.SetDirty(p);
                }
                if (GUILayout.Button(new GUIContent("Copy to other panels",
                        "Gives every other questionnaire panel in the scene this placement. " +
                        "Two panels in different spots means the participant has to re-learn " +
                        "where to reach halfway through the session.")))
                    CopyPlacementToOthers(p);
            }

            EditorGUILayout.Space(4);
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("Scene handle", GUILayout.Width(90));
                _mode = (HandleMode)GUILayout.Toolbar((int)_mode, new[] { "Move", "Rotate", "Off" });
            }
            _drawPreview = EditorGUILayout.ToggleLeft("Draw the panel to scale in the Scene view", _drawPreview);
        }

        /// <summary>Turns the placement into the two facts that actually decide
        /// whether it works: can they reach it, and can they read it.</summary>
        private void DrawDiagnostics(VrQuestionnairePanel p)
        {
            EditorGUILayout.LabelField("Reach & readability", EditorStyles.boldLabel);

            if (Anchor() == null)
                EditorGUILayout.HelpBox(
                    "No VrRig with a participant camera found, so the Scene preview falls back " +
                    "to this GameObject's own transform and the distances below are measured " +
                    "from the eye point as if it were at the origin.", MessageType.Warning);

            Vector3 c = p.localPosition;
            float centre = c.magnitude;
            float far = FarthestCornerDistance(p);
            float elevation = Mathf.Atan2(c.y, Mathf.Sqrt(c.x * c.x + c.z * c.z)) * Mathf.Rad2Deg;

            EditorGUILayout.LabelField($"Centre {centre:0.00} m from the eyes · " +
                                       $"furthest corner {far:0.00} m · " +
                                       $"{Mathf.Abs(elevation):0} ° {(elevation >= 0 ? "above" : "below")} the eye line",
                                       EditorStyles.miniLabel);

            if (c.z <= 0.05f)
                EditorGUILayout.HelpBox("The panel is level with or behind the participant — they " +
                                        "will never see it.", MessageType.Error);
            else if (centre < MinFocus)
                EditorGUILayout.HelpBox($"At {centre:0.00} m the panel is inside comfortable focus " +
                                        "distance for a headset. Expect eye strain and doubled text.",
                                        MessageType.Error);
            else if (far > MaxReach)
                EditorGUILayout.HelpBox($"The far corner is {far:0.00} m away — past a belted " +
                                        "participant's reach. They will have to lean out of the seat " +
                                        "to answer, which is both a nuisance and a safety problem on " +
                                        "a moving rig.", MessageType.Error);
            else if (far > ComfortableReach)
                EditorGUILayout.HelpBox($"The far corner is {far:0.00} m away — reachable but at full " +
                                        "arm's length. Pull it closer if answers start taking longer " +
                                        "than the questions deserve.", MessageType.Warning);
            else
                EditorGUILayout.HelpBox("Every control is within comfortable seated reach.", MessageType.Info);

            if (Mathf.Abs(elevation) > 35f)
                EditorGUILayout.HelpBox($"{Mathf.Abs(elevation):0} ° off the eye line means holding the " +
                                        "neck at an angle for the whole questionnaire. Under about 30 ° " +
                                        "is kinder.", MessageType.Warning);
        }

        // ── Scene view ──────────────────────────────────────────────────

        private void OnSceneGUI()
        {
            var p = (VrQuestionnairePanel)target;
            var anchor = Anchor();

            Vector3 origin = anchor != null ? anchor.position : p.transform.position;
            Quaternion aRot = anchor != null ? anchor.rotation : p.transform.rotation;

            Vector3 centre = origin + aRot * p.localPosition;
            Quaternion rot = aRot * Quaternion.Euler(p.localEulerAngles);

            if (_drawPreview) DrawPanelPreview(p, centre, rot, origin);

            Tools.hidden = _mode != HandleMode.Off;

            if (_mode == HandleMode.Move)
            {
                EditorGUI.BeginChangeCheck();
                Vector3 moved = Handles.PositionHandle(centre, rot);
                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(p, "Move questionnaire panel");
                    p.localPosition = Quaternion.Inverse(aRot) * (moved - origin);
                    EditorUtility.SetDirty(p);
                }
            }
            else if (_mode == HandleMode.Rotate)
            {
                EditorGUI.BeginChangeCheck();
                Quaternion turned = Handles.RotationHandle(rot, centre);
                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(p, "Rotate questionnaire panel");
                    p.localEulerAngles = (Quaternion.Inverse(aRot) * turned).eulerAngles;
                    EditorUtility.SetDirty(p);
                }
            }
        }

        /// <summary>Draws the page at true size using the SAME pixel
        /// coordinates the runtime lays out with, so what you position here is
        /// what the participant meets.</summary>
        private static void DrawPanelPreview(VrQuestionnairePanel p, Vector3 centre, Quaternion rot, Vector3 eye)
        {
            float w = Mathf.Max(MinWidth, p.widthMeters);
            float s = w / VrQuestionnairePanel.PixelWidth;      // metres per layout pixel
            float h = p.HeightMeters;

            // Layout pixels are measured from the page's top-left corner, with
            // y going down; the panel's own origin is its centre.
            Vector3 Px(float x, float y) =>
                centre + rot * new Vector3(-w * 0.5f + x * s, h * 0.5f - y * s, 0f);

            void Box(float x, float y, float rw, float rh, Color fill, Color line)
            {
                Handles.DrawSolidRectangleWithOutline(
                    new[] { Px(x, y), Px(x + rw, y), Px(x + rw, y + rh), Px(x, y + rh) }, fill, line);
            }

            Box(0, 0, VrQuestionnairePanel.PixelWidth, VrQuestionnairePanel.PixelHeight,
                Plate, new Color(Accent.r, Accent.g, Accent.b, 0.55f));

            Box(44, 84, 812, 4, Accent, Color.clear);                                    // rule
            Box(44, 120, 812, 140, new Color(1, 1, 1, 0.06f), Color.clear);              // question block

            // The scale, with this questionnaire's real step count on page 1 —
            // 21 detents across 0.62 m is a very different target from 7.
            int steps = 7;
            var q = p.questionnaire != null ? p.questionnaire : p.GetComponent<DelphiQuestionnaire>();
            if (q != null && q.questions.Count > 0) steps = Mathf.Max(2, q.questions[0].steps);

            Box(44, 380, 812, 6, new Color(1, 1, 1, 0.22f), Color.clear);
            for (int i = 0; i < steps; i++)
            {
                float x = 44 + (812 - 8) * (i / (float)(steps - 1));
                Box(x, 370, 8, 26, new Color(Accent.r, Accent.g, Accent.b, 0.8f), Color.clear);
            }

            // Buttons at their authored size, each wrapped in an outline
            // showing the invisible hit area — the thing you are actually
            // aiming a fingertip at, and the only one of the two that decides
            // whether a press lands.
            float bh = Mathf.Max(24f, p.buttonHeightPixels);
            float by = VrQuestionnairePanel.PixelHeight - 30f - bh;
            float bw = Mathf.Max(60f, p.backWidthPixels);
            float nw = Mathf.Max(60f, p.nextWidthPixels);
            float pad = Mathf.Max(0f, p.buttonHitPaddingPixels);
            var hitLine = new Color(1f, 0.75f, 0.2f, 0.55f);

            Box(44, by, bw, bh, new Color(1, 1, 1, 0.10f), Color.clear);                 // BACK
            Box(VrQuestionnairePanel.PixelWidth - 44 - nw, by, nw, bh, Accent, Color.clear);  // NEXT/SUBMIT
            Box(44 - pad, by - pad, bw + pad * 2, bh + pad * 2, Color.clear, hitLine);
            Box(VrQuestionnairePanel.PixelWidth - 44 - nw - pad, by - pad,
                nw + pad * 2, bh + pad * 2, Color.clear, hitLine);

            var titleStyle = new GUIStyle(EditorStyles.boldLabel) { normal = { textColor = Color.white } };
            var smallStyle = new GUIStyle(EditorStyles.miniLabel) { normal = { textColor = Muted } };
            Handles.Label(Px(44, 24), p.title, titleStyle);
            Handles.Label(Px(44, 130), q != null && q.questions.Count > 0 ? q.questions[0].text : "(no questions)", smallStyle);
            Handles.Label(Px(44, 470), $"{steps}-point scale · {w:0.00}×{h:0.00} m", smallStyle);

            // Sight line and reach, the two things the numbers are really about.
            Handles.color = new Color(Accent.r, Accent.g, Accent.b, 0.5f);
            Handles.DrawDottedLine(eye, centre, 3f);
            Handles.Label(Vector3.Lerp(eye, centre, 0.5f), $"{p.localPosition.magnitude:0.00} m", smallStyle);

            Handles.color = new Color(1f, 0.75f, 0.2f, 0.35f);
            Handles.DrawWireDisc(eye, rot * Vector3.up, ComfortableReach);
        }

        // ── Helpers ─────────────────────────────────────────────────────

        private static float FarthestCornerDistance(VrQuestionnairePanel p)
        {
            float hw = Mathf.Max(MinWidth, p.widthMeters) * 0.5f, hh = p.HeightMeters * 0.5f;
            var rot = Quaternion.Euler(p.localEulerAngles);
            float far = 0f;
            for (int i = 0; i < 4; i++)
            {
                var corner = p.localPosition + rot * new Vector3((i & 1) == 0 ? -hw : hw,
                                                                 (i & 2) == 0 ? -hh : hh, 0f);
                far = Mathf.Max(far, corner.magnitude);
            }
            return far;
        }

        private static void CopyPlacementToOthers(VrQuestionnairePanel from)
        {
            var all = Object.FindObjectsByType<VrQuestionnairePanel>(FindObjectsSortMode.None);
            int n = 0;
            foreach (var other in all)
            {
                if (other == from) continue;
                Undo.RecordObject(other, "Copy questionnaire placement");
                other.localPosition = from.localPosition;
                other.localEulerAngles = from.localEulerAngles;
                other.widthMeters = from.widthMeters;
                EditorUtility.SetDirty(other);
                n++;
            }
            Debug.Log($"[VrQuestionnairePanel] Placement copied to {n} other panel(s).", from);
        }
    }
}
