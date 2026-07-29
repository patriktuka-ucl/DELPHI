using UnityEditor;
using UnityEngine;

namespace Delphi.Motion.Editor
{
    /// <summary>
    /// Layer-by-layer inspector for CarRumbleCues, with a live meter per pad.
    ///
    /// The meters are the point. Rumble is the one cue in this project you
    /// cannot check by looking at the Scene view — the seat barely moves, and
    /// the whole design goal is that it barely moves. Watching the three bars
    /// (and LongitudinalRaw against LongitudinalLevel, which is the transient
    /// and the adaptation decay made visible) is how you tell "the model isn't
    /// firing" apart from "the model is firing and the hardware isn't
    /// rendering it" — two problems with completely different fixes.
    /// </summary>
    [CustomEditor(typeof(CarRumbleCues))]
    public class CarRumbleCuesEditor : UnityEditor.Editor
    {
        private SerializedProperty _car, _cues;
        private SerializedProperty _masterGain, _soloGain;
        private SerializedProperty _minEffective, _maxIntensity, _silenceThreshold, _minHz, _maxHz;
        private SerializedProperty _bedEnabled, _bedLevel, _bedRefKmh, _bedHz;
        private SerializedProperty _longEnabled, _refAccel, _longGamma, _longGain;
        private SerializedProperty _attack, _release, _adaptation, _sustain, _transient;
        private SerializedProperty _accelCentre, _accelSide, _brakeCentre, _brakeSide;
        private SerializedProperty _accelHz, _brakeHz, _blendMs2;
        private SerializedProperty _latEnabled, _refYawRate, _latGamma, _latGain;
        private SerializedProperty _latInner, _latCentre, _latHz, _latInvert;
        private SerializedProperty _muteFrozen, _muteReturning, _muteFade;

        private void OnEnable()
        {
            _car = serializedObject.FindProperty("car");
            _cues = serializedObject.FindProperty("cues");
            _masterGain = serializedObject.FindProperty("masterGain");
            _soloGain = serializedObject.FindProperty("soloGain");
            _minEffective = serializedObject.FindProperty("minEffectiveIntensity");
            _maxIntensity = serializedObject.FindProperty("maxIntensity");
            _silenceThreshold = serializedObject.FindProperty("silenceThreshold");
            _minHz = serializedObject.FindProperty("minHz");
            _maxHz = serializedObject.FindProperty("maxHz");
            _bedEnabled = serializedObject.FindProperty("roadBedEnabled");
            _bedLevel = serializedObject.FindProperty("roadBedStrength");
            _bedRefKmh = serializedObject.FindProperty("roadBedReferenceKmh");
            _bedHz = serializedObject.FindProperty("roadBedHz");
            _longEnabled = serializedObject.FindProperty("longitudinalEnabled");
            _refAccel = serializedObject.FindProperty("referenceAccelMs2");
            _longGamma = serializedObject.FindProperty("longitudinalGamma");
            _longGain = serializedObject.FindProperty("longitudinalGain");
            _attack = serializedObject.FindProperty("attackSeconds");
            _release = serializedObject.FindProperty("releaseSeconds");
            _adaptation = serializedObject.FindProperty("adaptationSeconds");
            _sustain = serializedObject.FindProperty("sustainLevel");
            _transient = serializedObject.FindProperty("transientGain");
            _accelCentre = serializedObject.FindProperty("accelCentreWeight");
            _accelSide = serializedObject.FindProperty("accelSideWeight");
            _brakeCentre = serializedObject.FindProperty("brakeCentreWeight");
            _brakeSide = serializedObject.FindProperty("brakeSideWeight");
            _accelHz = serializedObject.FindProperty("accelHz");
            _brakeHz = serializedObject.FindProperty("brakeHz");
            _blendMs2 = serializedObject.FindProperty("accelBrakeBlendMs2");
            _latEnabled = serializedObject.FindProperty("lateralEnabled");
            _refYawRate = serializedObject.FindProperty("referenceYawRateDegPerSec");
            _latGamma = serializedObject.FindProperty("lateralGamma");
            _latGain = serializedObject.FindProperty("lateralGain");
            _latInner = serializedObject.FindProperty("lateralInnerFraction");
            _latCentre = serializedObject.FindProperty("lateralCentreFraction");
            _latHz = serializedObject.FindProperty("lateralHz");
            _latInvert = serializedObject.FindProperty("invertLateralPan");
            _muteFrozen = serializedObject.FindProperty("muteWhileFrozen");
            _muteReturning = serializedObject.FindProperty("muteWhileReturningToNeutral");
            _muteFade = serializedObject.FindProperty("muteFadeSeconds");
        }

        public override bool RequiresConstantRepaint() => Application.isPlaying;

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            var r = (CarRumbleCues)target;

            if (Application.isPlaying) DrawLiveMeters(r);

            EditorGUILayout.LabelField("Links", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(_car);
            EditorGUILayout.PropertyField(_cues);

            Section("Master");
            EditorGUILayout.PropertyField(_masterGain);
            EditorGUILayout.PropertyField(_soloGain);

            Section("Output Range — the motors' real window, not 0-100");
            EditorGUILayout.PropertyField(_minEffective);
            EditorGUILayout.PropertyField(_maxIntensity);
            EditorGUILayout.PropertyField(_silenceThreshold);
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.PropertyField(_minHz);
            EditorGUILayout.PropertyField(_maxHz);
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.HelpBox(
                "minEffectiveIntensity and the Hz window are HARDWARE measurements — get them " +
                "from YawVR3Tester's rumble bench. Everything else here is taste; these two are " +
                "fact, and a wrong floor is the most likely reason a cue can't be felt.",
                MessageType.None);

            Section("Layer A — Road Bed (speed)");
            EditorGUILayout.PropertyField(_bedEnabled);
            using (new EditorGUI.DisabledScope(!_bedEnabled.boolValue))
            {
                EditorGUILayout.PropertyField(_bedLevel);
                EditorGUILayout.PropertyField(_bedRefKmh);
                EditorGUILayout.PropertyField(_bedHz);
            }

            Section("Layer B — Longitudinal (accelerate / brake)");
            EditorGUILayout.PropertyField(_longEnabled);
            using (new EditorGUI.DisabledScope(!_longEnabled.boolValue))
            {
                EditorGUILayout.PropertyField(_refAccel);
                EditorGUILayout.PropertyField(_longGamma);
                EditorGUILayout.PropertyField(_longGain);

                EditorGUILayout.Space(4);
                EditorGUILayout.LabelField("Envelope — where 'noticeable' comes from",
                                            EditorStyles.miniBoldLabel);
                EditorGUILayout.PropertyField(_attack);
                EditorGUILayout.PropertyField(_release);
                EditorGUILayout.PropertyField(_adaptation);
                EditorGUILayout.PropertyField(_sustain);
                EditorGUILayout.PropertyField(_transient);

                EditorGUILayout.Space(4);
                EditorGUILayout.LabelField("Texture — how the two read differently",
                                            EditorStyles.miniBoldLabel);
                EditorGUILayout.PropertyField(_accelCentre);
                EditorGUILayout.PropertyField(_accelSide);
                EditorGUILayout.PropertyField(_brakeCentre);
                EditorGUILayout.PropertyField(_brakeSide);
                EditorGUILayout.PropertyField(_accelHz);
                EditorGUILayout.PropertyField(_brakeHz);
                EditorGUILayout.PropertyField(_blendMs2);
            }

            Section("Layer C — Lateral (cornering)");
            EditorGUILayout.PropertyField(_latEnabled);
            using (new EditorGUI.DisabledScope(!_latEnabled.boolValue))
            {
                EditorGUILayout.PropertyField(_refYawRate);
                EditorGUILayout.PropertyField(_latGamma);
                EditorGUILayout.PropertyField(_latGain);
                EditorGUILayout.PropertyField(_latInner);
                EditorGUILayout.PropertyField(_latCentre);
                EditorGUILayout.PropertyField(_latHz);
                EditorGUILayout.PropertyField(_latInvert);
            }

            Section("Session Behaviour");
            EditorGUILayout.PropertyField(_muteFrozen);
            EditorGUILayout.PropertyField(_muteReturning);
            EditorGUILayout.PropertyField(_muteFade);

            serializedObject.ApplyModifiedProperties();
        }

        private void DrawLiveMeters(CarRumbleCues r)
        {
            EditorGUILayout.LabelField("Live", EditorStyles.boldLabel);

            var conn = YawVR3Connection.Instance;
            string transport = conn == null
                ? "no YawVR3Connection in scene — nothing is being transported"
                : conn.State != YawConnectionState.Started
                    ? $"rig is {conn.State} — modelling, but not transported until Started"
                    : !conn.rumbleEnabled
                        ? "rumble switched OFF at the connection — modelling, sending zeros"
                        : r.SoloMode
                            ? "live, solo (tilt off — soloGain applied)"
                            : "live, alongside tilt";
            EditorGUILayout.HelpBox(transport,
                conn != null && conn.State == YawConnectionState.Started && conn.rumbleEnabled
                    ? MessageType.Info : MessageType.Warning);

            Meter("Right pad", r.MotorRight / 100f, $"{r.MotorRight}");
            Meter("Centre pad", r.MotorCentre / 100f, $"{r.MotorCentre}");
            Meter("Left pad", r.MotorLeft / 100f, $"{r.MotorLeft}");
            EditorGUILayout.LabelField($"Frequency — {r.Hz} Hz" +
                                        (r.IsSilent ? "   (all pads silent)" : ""));

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Layers (normalised, before mixing)", EditorStyles.miniBoldLabel);
            Meter("Road bed", r.RoadBedLevel, r.RoadBedLevel.ToString("0.00"));
            // Raw vs enveloped side by side: the gap IS the transient on the
            // way in and the adaptation decay on the way out.
            Meter("Long. raw", r.LongitudinalRaw, r.LongitudinalRaw.ToString("0.00"));
            Meter("Long. shaped", r.LongitudinalLevel, r.LongitudinalLevel.ToString("0.00"));
            Meter("Lateral", r.LateralLevel, r.LateralLevel.ToString("0.00"));
            EditorGUILayout.LabelField(
                $"Brakeness {r.Brakeness:0.00}   (0 = accelerating, 1 = braking)      " +
                $"Mute {r.MuteBlend:0.00}");

            EditorGUILayout.Space(8);
        }

        private static void Meter(string label, float value01, string readout)
        {
            var rect = EditorGUILayout.GetControlRect();
            rect = EditorGUI.PrefixLabel(rect, new GUIContent(label));
            EditorGUI.ProgressBar(rect, Mathf.Clamp01(value01), readout);
        }

        private static void Section(string title)
        {
            EditorGUILayout.Space(10);
            EditorGUILayout.LabelField(title, EditorStyles.boldLabel);
        }
    }
}
