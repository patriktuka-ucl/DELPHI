using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace Delphi.VR
{
    /// <summary>
    /// A world-space button the participant presses with a fingertip.
    ///
    /// WHY THIS EXISTS RATHER THAN A TWO-STEP VrTouchSlider:
    ///
    ///   The panels used to build their buttons as sliders with steps = 2, on
    ///   the reasoning that one fingertip mechanism is easier to keep working
    ///   than two. It is, but it was the wrong mechanism. A slider only raises
    ///   onValueChanged when the value CHANGES, and a two-step slider rounds to
    ///   0 at rest — so the left half of every button did nothing at all, the
    ///   right half worked once, and pressing it a second time did nothing
    ///   because the value was already 1. The button looked whole and behaved
    ///   like a sliver near the label. That is not a tuning problem, it is the
    ///   wrong abstraction: a button has no value, it has a moment.
    ///
    /// PRESS SEMANTICS: ON RELEASE, NOT ON CONTACT.
    ///
    ///   The finger entering the touch band starts a press and lights the
    ///   button; the press only FIRES when the finger leaves that band again.
    ///
    ///   Firing on contact reads as instant and is wrong, because a fingertip
    ///   does not stop at the surface — a normal poke goes straight through the
    ///   panel and comes back out, crossing the band TWICE. Fire on contact and
    ///   one poke is two presses: NEXT advances two questions and the
    ///   participant never sees the one in between. Release has one
    ///   unambiguous moment per poke however far through it goes.
    ///
    ///   Sliding sideways off the button CANCELS instead of firing, so a press
    ///   begun by accident can be taken back — the same escape a mouse gives.
    ///
    /// AND A LOCK AFTER EVERY PRESS, shared by every button on every panel.
    /// One poke is one action; a hand withdrawing through a second control must
    /// not trigger it, and no participant means to press twice in a tenth of a
    /// second.
    ///
    /// NOT INTERACTABLE MEANS NOT LIT. A disabled button ignores the fingertip
    /// AND stays grey, including under a hovering finger. The participant must
    /// never be able to make a control look live by touching it when pressing
    /// it would do nothing.
    /// </summary>
    public class VrTouchButton : MonoBehaviour
    {
        /// <summary>Every live button, so the fingertip driver can offer tips
        /// to all of them without a per-frame scene search.</summary>
        public static readonly List<VrTouchButton> Active = new();

        public UnityEvent onPressed = new();

        [Tooltip("How near the panel plane the fingertip must come to fire, in " +
                 "metres. Hand tracking is poor at depth, so this is generous.")]
        public float touchDepthMeters = 0.035f;

        [Tooltip("How far from the panel the fingertip can wander and still " +
                 "count as being at this button at all. Past this it is no " +
                 "longer claimed, so it is free to reach something else.")]
        public float releaseDepthMeters = 0.08f;

        [Tooltip("Dead time after ANY button fires, in seconds. Shared by every " +
                 "button, so a hand withdrawing through a second one cannot " +
                 "trigger it. Raise it if a single poke ever acts twice.")]
        public float repeatLockSeconds = 0.45f;

        [Tooltip("Shortest press that counts, in seconds. Rejects a fingertip " +
                 "that clipped the band while travelling somewhere else.")]
        public float minPressSeconds = 0.05f;

        [Tooltip("Extra hit area around the artwork, in canvas pixels. The " +
                 "target a finger has to hit should be bigger than the thing " +
                 "the eye has to read, not the same size.")]
        public float hitPaddingPixels = 24f;

        [Tooltip("Off greys the button out and makes it ignore fingertips " +
                 "entirely — no press, no highlight.")]
        public bool interactable = true;

        /// <summary>Primary buttons are the accented one on a page (NEXT,
        /// SUBMIT); secondary ones are quiet (BACK).</summary>
        public bool primary = true;

        private RectTransform _rect;
        private Image _bg;
        private Text _label;
        private bool _pressing;          // finger is in the band, press not yet committed
        private float _pressStart;
        private float _lastSeen;         // last frame a fingertip was offered
        private bool _lastInteractable = true;
        private float _flashUntil;

        /// <summary>Shared dead time. One poke is one action ACROSS THE PANEL,
        /// not per control — the hand that just pressed NEXT is on its way back
        /// through wherever BACK happens to be.</summary>
        private static float _lockUntil;

        private static readonly Color Accent      = new Color(0.24f, 0.83f, 0.76f);
        private static readonly Color AccentHot   = new Color(0.44f, 0.95f, 0.88f);
        private static readonly Color OnAccent    = new Color(0.03f, 0.09f, 0.10f);
        private static readonly Color Muted       = new Color(0.72f, 0.77f, 0.86f);
        private static readonly Color Disabled    = new Color(0.30f, 0.33f, 0.38f);
        private static readonly Color DisabledTxt = new Color(0.70f, 0.74f, 0.80f);

        private void OnEnable() { if (!Active.Contains(this)) Active.Add(this); Restyle(); }
        private void OnDisable() { Active.Remove(this); _pressing = false; }

        /// <summary>Adopts the artwork the panel already built. Call once,
        /// right after AddComponent.</summary>
        public void Build(RectTransform rect, Image background, Text label, bool isPrimary)
        {
            _rect = rect; _bg = background; _label = label; primary = isPrimary;
            Restyle();
        }

        public void SetLabel(string text)
        {
            if (_label != null && _label.text != text) _label.text = text;
        }

        /// <summary>Switches the button between the accented and the quiet
        /// look, repainting immediately.
        ///
        /// Writing `primary` on its own does nothing visible: Restyle only runs
        /// on a press or an interactable change, so a toggle group built by
        /// flipping the field would keep showing whichever option was selected
        /// when the panel was built. In a group where the accent IS the
        /// selection, that is a control that lies about the car's state.</summary>
        public void SetPrimary(bool value)
        {
            if (primary == value) return;
            primary = value;
            Restyle();
        }

        /// <summary>Offers a fingertip to this button. Returns true if it took
        /// it, so the same fingertip cannot also drive a slider or fire a
        /// second control behind this one.</summary>
        public bool TryPress(Vector3 tipWorld)
        {
            if (_rect == null) return false;
            _lastSeen = Time.unscaledTime;

            Vector3 local = _rect.InverseTransformPoint(tipWorld);
            var r = _rect.rect;
            float pad = Mathf.Max(0f, hitPaddingPixels);

            bool insideFace = local.x >= r.xMin - pad && local.x <= r.xMax + pad
                           && local.y >= r.yMin - pad && local.y <= r.yMax + pad;

            // Depth is in metres; the canvas is scaled metres-per-pixel, so the
            // tolerance has to come through the same scale. Unsigned, so a poke
            // that goes clean through the panel is measured the same on the far
            // side as on the near one.
            float scale = Mathf.Abs(_rect.lossyScale.z) > 1e-6f ? Mathf.Abs(_rect.lossyScale.z) : 1f;
            float depth = Mathf.Abs(local.z) * scale;
            bool inBand = insideFace && depth <= touchDepthMeters;

            // A PRESS IN PROGRESS IS RESOLVED FIRST, before any early return.
            // A hand withdrawing at an angle leaves the band and the rect on the
            // same frame, and checking the rect first would read that as sliding
            // off and throw the press away — the participant's most natural
            // withdrawal would be the one gesture that never worked.
            if (_pressing && !inBand)
            {
                // Pulled back = a press. Still at the surface but off the side =
                // they moved off it deliberately, which cancels.
                bool pulledBack = depth > touchDepthMeters;
                bool longEnough = Time.unscaledTime - _pressStart >= Mathf.Max(0f, minPressSeconds);

                _pressing = false;
                if (pulledBack && longEnough && interactable && Time.unscaledTime >= _lockUntil)
                {
                    _lockUntil = Time.unscaledTime + Mathf.Max(0f, repeatLockSeconds);
                    _flashUntil = Time.unscaledTime + 0.12f;
                    Restyle();
                    onPressed.Invoke();
                }
                else Restyle();

                return true;
            }

            if (!insideFace) return false;

            // Far enough out to be reaching for something else — let it go.
            if (depth >= Mathf.Max(releaseDepthMeters, touchDepthMeters)) return false;

            // In the button's airspace. Claim the fingertip even when the button
            // is dead or locked, so a finger over a greyed-out SUBMIT cannot
            // fall through to whatever lies behind it.
            if (!interactable || Time.unscaledTime < _lockUntil) return true;

            if (inBand && !_pressing)
            {
                _pressing = true;
                _pressStart = Time.unscaledTime;
                Restyle();              // lights up NOW; the action waits for release
            }
            return true;
        }

        private void Cancel()
        {
            if (!_pressing) return;
            _pressing = false;
            Restyle();
        }

        private void Update()
        {
            // A press with no finger on it any more is a tracking dropout, not
            // a press. Abandoning it is the safe reading: a lost hand must
            // never turn a page on the participant's behalf.
            if (_pressing && Time.unscaledTime - _lastSeen > 0.25f) Cancel();

            // The panel sets `interactable` from its own state every frame, and
            // a colour that only updated on press would lag a page turn.
            bool flashExpired = _flashUntil > 0f && Time.unscaledTime > _flashUntil;
            if (_lastInteractable != interactable || flashExpired)
            {
                if (flashExpired) _flashUntil = 0f;
                _lastInteractable = interactable;
                Restyle();
            }
        }

        private void Restyle()
        {
            if (_bg == null) return;
            bool flash = _flashUntil > 0f && Time.unscaledTime <= _flashUntil;

            if (!interactable)
            {
                _bg.color = primary ? Disabled : new Color(1f, 1f, 1f, 0.03f);
                if (_label != null) _label.color = primary ? DisabledTxt : new Color(0.45f, 0.48f, 0.53f);
                return;
            }

            if (primary)
            {
                _bg.color = flash ? Color.white : _pressing ? AccentHot : Accent;
                if (_label != null) _label.color = OnAccent;
            }
            else
            {
                _bg.color = flash ? new Color(1f, 1f, 1f, 0.35f)
                          : _pressing ? new Color(1f, 1f, 1f, 0.22f)
                          : new Color(1f, 1f, 1f, 0.10f);
                if (_label != null) _label.color = _pressing ? Color.white : Muted;
            }
        }
    }
}
