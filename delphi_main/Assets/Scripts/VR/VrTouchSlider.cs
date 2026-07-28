using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace Delphi.VR
{
    /// <summary>
    /// A world-space slider the participant drags with a fingertip.
    ///
    /// WHY NOT ULTRALEAP'S PHYSICAL SLIDER:
    ///
    ///   That one is a rigid body pushed by collider hands, so it only works
    ///   when the whole PhysicalHands contact rig has come up correctly — a
    ///   chain with several silent failure modes, all of which present as "the
    ///   slider does nothing". It also cannot be styled: it is a grey box
    ///   because it is a physics primitive.
    ///
    ///   This takes the fingertip position directly from the hand tracker and
    ///   moves a value. No rigid bodies, no colliders, no contact rig, nothing
    ///   to fail silently. If the tracker reports a hand, this works.
    ///
    /// WHY HOVER-DRAG RATHER THAN A PUSH:
    ///
    ///   Pushing a control needs the participant to judge depth, which is the
    ///   one thing hand tracking is worst at and headsets convey poorly. A
    ///   slider also needs CONTINUOUS input — a poke that fires once on
    ///   contact can set a value but cannot let you feel your way to it.
    ///
    ///   So the finger only has to be NEAR the panel, within touchDepth, and
    ///   inside the row. While it is, the value follows the fingertip along
    ///   the track and the control lights up. That is forgiving of depth error
    ///   in exactly the way a push is not, and it reads as "I'm touching it".
    ///
    /// All artwork is generated at run time (see MakeRounded / MakeCircle),
    /// so the look needs no imported assets and cannot silently degrade to
    /// hard rectangles if a built-in sprite fails to resolve.
    /// </summary>
    public class VrTouchSlider : MonoBehaviour
    {
        /// <summary>Every live slider, so one driver can offer fingertips to
        /// all of them without a per-frame scene search.</summary>
        public static readonly List<VrTouchSlider> Active = new();

        [Range(0f, 1f)] public float Value { get; private set; } = 0.5f;
        public UnityEvent<float> onValueChanged = new();

        /// <summary>How near the panel plane a fingertip must be to engage,
        /// in metres. Generous on purpose — see the class summary.</summary>
        public float touchDepthMeters = 0.045f;

        private RectTransform _row;      // the interactive area (bigger than the visible track)
        private RectTransform _track, _fill, _knob, _halo;
        private Image _trackImg, _fillImg, _knobImg, _haloImg;
        private Text _valueText;
        private bool _engaged;

        /// <summary>True while a fingertip is driving this slider. Callers must
        /// not write a value during that — echoing the car's current setting
        /// back into a control the participant is holding fights their finger
        /// and makes the slider feel like it is snapping away from them.</summary>
        public bool IsEngaged => _engaged;

        private static readonly Color Accent    = new Color(0.24f, 0.83f, 0.76f);
        private static readonly Color AccentDim = new Color(0.18f, 0.55f, 0.52f);
        private static readonly Color TrackCol  = new Color(1f, 1f, 1f, 0.13f);
        private static readonly Color KnobCol   = new Color(0.96f, 0.98f, 1f);

        private void OnEnable() { if (!Active.Contains(this)) Active.Add(this); }
        private void OnDisable() { Active.Remove(this); _engaged = false; Restyle(); }

        /// <summary>Builds the visuals. Call once, right after AddComponent.</summary>
        public void Build(RectTransform row, Font font, float trackHeight = 22f, float knobSize = 58f)
        {
            _row = row;

            // Recessed channel behind the track — a subtle darker inset makes
            // the lit fill read as sitting IN something rather than painted on.
            var channel = NewImage(row, "Channel", out var channelImg, sliced: true);
            channelImg.color = new Color(0f, 0f, 0f, 0.35f);
            channel.anchorMin = new Vector2(0f, 0.5f);
            channel.anchorMax = new Vector2(1f, 0.5f);
            channel.offsetMin = new Vector2(knobSize * 0.5f - 4f, -trackHeight * 0.5f - 4f);
            channel.offsetMax = new Vector2(-knobSize * 0.5f + 4f, trackHeight * 0.5f + 4f);

            _track = NewImage(row, "Track", out _trackImg, sliced: true);
            _trackImg.color = TrackCol;
            _track.anchorMin = new Vector2(0f, 0.5f);
            _track.anchorMax = new Vector2(1f, 0.5f);
            _track.offsetMin = new Vector2(knobSize * 0.5f, -trackHeight * 0.5f);
            _track.offsetMax = new Vector2(-knobSize * 0.5f, trackHeight * 0.5f);

            _fill = NewImage(_track, "Fill", out _fillImg, sliced: true);
            _fillImg.color = AccentDim;
            _fill.anchorMin = Vector2.zero;
            _fill.anchorMax = new Vector2(0.5f, 1f);
            _fill.offsetMin = _fill.offsetMax = Vector2.zero;

            // Scale ticks. Instrument panels read as instruments largely
            // because they are graduated — five marks turn an abstract bar
            // into something with a scale you can aim at.
            for (int i = 0; i <= 4; i++)
            {
                var tick = NewImage(_track, "Tick", out var tickImg, sliced: false);
                tickImg.color = new Color(1f, 1f, 1f, i == 0 || i == 4 ? 0.30f : 0.16f);
                tick.anchorMin = new Vector2(i / 4f, 0.5f);
                tick.anchorMax = new Vector2(i / 4f, 0.5f);
                tick.sizeDelta = new Vector2(2f, trackHeight + 16f);
                tick.anchoredPosition = Vector2.zero;
            }

            // Halo behind the knob: reads as emitted light when engaged, and
            // is what makes the control look powered rather than drawn.
            _halo = NewImage(row, "Halo", out _haloImg, sliced: false, circle: true);
            _haloImg.color = new Color(Accent.r, Accent.g, Accent.b, 0f);
            _halo.sizeDelta = new Vector2(knobSize * 1.9f, knobSize * 1.9f);
            _halo.anchorMin = _halo.anchorMax = new Vector2(0.5f, 0.5f);

            _knob = NewImage(row, "Knob", out _knobImg, sliced: false, circle: true);
            _knobImg.color = KnobCol;
            _knob.sizeDelta = new Vector2(knobSize, knobSize);
            _knob.anchorMin = _knob.anchorMax = new Vector2(0.5f, 0.5f);

            // Dark core inside the knob leaves a bright ring — a machined
            // bezel rather than a plain dot.
            var core = NewImage(_knob, "Core", out var coreImg, sliced: false, circle: true);
            coreImg.color = new Color(0.05f, 0.07f, 0.09f, 1f);
            core.anchorMin = core.anchorMax = new Vector2(0.5f, 0.5f);
            core.sizeDelta = new Vector2(knobSize * 0.52f, knobSize * 0.52f);

            SetValue(Value, notify: false);
        }

        /// <summary>Attaches the numeric readout this slider keeps updated.</summary>
        public void BindValueLabel(Text label) { _valueText = label; SetValue(Value, false); }

        public void SetValue(float v, bool notify)
        {
            v = Mathf.Clamp01(v);
            bool changed = !Mathf.Approximately(v, Value);
            Value = v;

            if (_fill != null) _fill.anchorMax = new Vector2(v, 1f);
            if (_knob != null && _track != null)
            {
                // Track is centred in the row, so offsetting from the row's
                // centre by (v-0.5)*trackWidth lands the knob on the value.
                var pos = new Vector2((v - 0.5f) * _track.rect.width, 0f);
                _knob.anchoredPosition = pos;
                if (_halo != null) _halo.anchoredPosition = pos; // glow follows the knob
            }
            if (_valueText != null) _valueText.text = v.ToString("0.00");

            if (changed && notify) onValueChanged.Invoke(v);
        }

        /// <summary>Offers a fingertip to this slider. Returns true if it took
        /// it, so one fingertip cannot drive two sliders at once.</summary>
        public bool TryDrive(Vector3 tipWorld)
        {
            if (_row == null) return false;

            Vector3 local = _row.InverseTransformPoint(tipWorld);

            // Depth is in metres; the canvas is scaled metres-per-pixel, so
            // convert the tolerance through the row's own scale.
            float scale = Mathf.Abs(_row.lossyScale.z) > 1e-6f ? Mathf.Abs(_row.lossyScale.z) : 1f;
            float depthLocal = touchDepthMeters / scale;

            var r = _row.rect;
            bool inside = local.x >= r.xMin && local.x <= r.xMax
                       && local.y >= r.yMin && local.y <= r.yMax
                       && Mathf.Abs(local.z) <= depthLocal;

            if (!inside)
            {
                if (_engaged) { _engaged = false; Restyle(); }
                return false;
            }

            _engaged = true;
            Restyle();

            // MAPPED IN THE TRACK'S OWN SPACE, VIA ITS OWN RECT.
            //
            // The previous version measured the fingertip in ROW space and
            // assumed that space was centred on zero. It is not: the row's
            // pivot is top-left, so its local x runs 0..width rather than
            // -half..+half. That put the left edge at 0.5 and the midpoint at
            // 1.0 — a value that looked plausible everywhere and was wrong
            // everywhere.
            //
            // Rect.xMin/xMax already account for whatever pivot a transform
            // has, so going through the track's own rect is correct for any
            // pivot and cannot drift if the layout changes again.
            Vector3 trackLocal = _track.InverseTransformPoint(tipWorld);
            var tr = _track.rect;
            float t = Mathf.InverseLerp(tr.xMin, tr.xMax, trackLocal.x);
            SetValue(t, notify: true);
            return true;
        }

        private void Restyle()
        {
            if (_fillImg != null) _fillImg.color = _engaged ? Accent : AccentDim;
            if (_knobImg != null) _knobImg.color = _engaged ? Accent : KnobCol;
            if (_knob != null)    _knob.localScale = Vector3.one * (_engaged ? 1.14f : 1f);
            if (_haloImg != null)
                _haloImg.color = new Color(Accent.r, Accent.g, Accent.b, _engaged ? 0.28f : 0f);
        }

        // ── Helpers ──────────────────────────────────────────────────────

        // ── Procedural sprites ───────────────────────────────────────────
        //
        // Generated rather than loaded. Unity's built-in UI skin sprites
        // ("UI/Skin/UISprite.psd") do not reliably resolve through
        // Resources.GetBuiltinResource in this project, and a missing sprite
        // silently falls back to a hard rectangle — which is precisely the
        // "looks awful" problem. Drawing them costs one 64×64 texture each,
        // once, and can't fail.
        private static Sprite _rounded, _circle;

        private static Sprite RoundedSprite => _rounded ??= MakeRounded(64, 18);
        private static Sprite CircleSprite  => _circle  ??= MakeCircle(96);

        /// <summary>Rounded rectangle with a 9-slice border, so it keeps its
        /// corner radius at any size the layout stretches it to.</summary>
        private static Sprite MakeRounded(int size, int radius)
        {
            var tex = NewTex(size);
            var px = new Color[size * size];
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                // Distance outside the inner rect whose corners are the arcs.
                float dx = Mathf.Max(radius - x - 0.5f, 0f, x + 0.5f - (size - radius));
                float dy = Mathf.Max(radius - y - 0.5f, 0f, y + 0.5f - (size - radius));
                float d = Mathf.Sqrt(dx * dx + dy * dy);
                // One-pixel feather, so edges are smooth instead of stepped.
                px[y * size + x] = new Color(1f, 1f, 1f, Mathf.Clamp01(radius - d + 0.5f));
            }
            tex.SetPixels(px); tex.Apply();

            float b = radius;
            return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f),
                                 100f, 0, SpriteMeshType.FullRect, new Vector4(b, b, b, b));
        }

        private static Sprite MakeCircle(int size)
        {
            var tex = NewTex(size);
            var px = new Color[size * size];
            float r = size * 0.5f, c = r - 0.5f;
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float d = Mathf.Sqrt((x - c) * (x - c) + (y - c) * (y - c));
                px[y * size + x] = new Color(1f, 1f, 1f, Mathf.Clamp01(r - d));
            }
            tex.SetPixels(px); tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f));
        }

        private static Texture2D NewTex(int size) => new Texture2D(size, size, TextureFormat.RGBA32, false)
        {
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp,
            hideFlags = HideFlags.HideAndDontSave
        };

        /// <summary>Creates one styled image on the panel.</summary>
        private static RectTransform NewImage(Transform parent, string name, out Image img,
                                              bool sliced, bool circle = false)
        {
            var go = new GameObject(name, typeof(Image));
            go.transform.SetParent(parent, false);
            img = go.GetComponent<Image>();
            img.raycastTarget = false;

            img.sprite = circle ? CircleSprite : RoundedSprite;
            img.type = circle ? Image.Type.Simple : Image.Type.Sliced;
            return go.GetComponent<RectTransform>();
        }
    }
}
