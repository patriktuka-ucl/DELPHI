using System.Collections.Generic;
using UnityEngine;

namespace Delphi.Simulation
{
    /// <summary>
    /// Builds one session's route by choosing a sequence of tiles and placing
    /// each instance so its entry (t=0) lines up with the previous tile's exit
    /// (t=1) — matching both position and facing direction.
    ///
    /// Per battery (one parameter set / one BO evaluation):
    ///   [filler]  [event, event, event — SHUFFLED — each followed by filler]
    /// The filler that opens a battery is where the parameter change for that
    /// battery hides (nothing is being exercised there, so it's imperceptible).
    ///
    /// This class only sequences and positions tiles — it does NOT drive the
    /// car across them. That's a separate "multi-tile follower" component,
    /// the natural next step once this is confirmed working.
    /// </summary>
    public class RouteStitcher : MonoBehaviour
    {
        [Header("Tile pools (drag your tile prefabs in)")]
        public List<RouteTile> fillerTiles  = new();
        public List<RouteTile> redLightTiles = new();
        public List<RouteTile> catchUpTiles  = new();
        public List<RouteTile> cornerTiles   = new();

        [Header("Session shape")]
        [Tooltip("How many batteries (parameter sets / BO evaluations) to lay down.")]
        public int batteryCount = 3;
        [Tooltip("Random seed — same seed reproduces the same session order.")]
        public int seed = 12345;

        [Header("Build now")]
        public bool buildOnStart = true;

        // The final ordered, placed sequence — read this to drive the car later.
        public readonly List<RouteTile> OrderedTiles = new();

        public struct EventInfo { public TileKind kind; public int battery; public RouteTile tile; }
        public readonly List<EventInfo> Events = new();

        private System.Random _rng;
        private readonly List<GameObject> _spawned = new();

        private void Start()
        {
            if (buildOnStart) Build();
        }

        [ContextMenu("Build Session")]
        public void Build()
        {
            Clear();
            _rng = new System.Random(seed);

            Vector3 cursorPos = transform.position;
            Quaternion cursorRot = transform.rotation;

            for (int b = 0; b < batteryCount; b++)
            {
                // Opens the battery — the parameter change for this battery
                // hides here, since no parameter is exercised on plain road.
                PlaceTile(PickRandom(fillerTiles), ref cursorPos, ref cursorRot, b);

                foreach (var kind in ShuffledEventOrder())
                {
                    PlaceTile(PickRandom(PoolFor(kind)), ref cursorPos, ref cursorRot, b);
                    // Short filler after each event for irregular spacing and
                    // clean recovery room before the next scored moment.
                    PlaceTile(PickRandom(fillerTiles), ref cursorPos, ref cursorRot, b);
                }
            }

            Debug.Log($"[RouteStitcher] Built {batteryCount} batteries — " +
                      $"{OrderedTiles.Count} tiles total, {Events.Count} scored events.");
        }

        private void PlaceTile(RouteTile prefab, ref Vector3 cursorPos, ref Quaternion cursorRot, int battery)
        {
            if (prefab == null)
            {
                Debug.LogWarning("[RouteStitcher] A tile pool has an empty slot — skipping.");
                return;
            }

            var inst = Instantiate(prefab.gameObject, transform);
            _spawned.Add(inst);
            var tile = inst.GetComponent<RouteTile>();

            // 1. Rotate the whole tile so its entry direction matches the cursor's.
            //    FromToRotation directly solves "rotate THIS vector onto THAT
            //    vector" with no assumption about the tile's own transform
            //    orientation — safer than composing LookRotations, which
            //    silently drifted when a tile's root rotation didn't already
            //    match its curve's tangent direction.
            Vector3 entryFwd  = ForwardAt(tile, 0f);
            Vector3 targetFwd = cursorRot * Vector3.forward;
            Quaternion rotDelta = Quaternion.FromToRotation(entryFwd, targetFwd);
            inst.transform.rotation = rotDelta * inst.transform.rotation;

            // 2. Translate so the (now-rotated) entry lands exactly on the cursor.
            Vector3 rotatedEntryPos = tile.EntryPosition;
            inst.transform.position += (cursorPos - rotatedEntryPos);

            OrderedTiles.Add(tile);
            if (tile.IsEvent)
                Events.Add(new EventInfo { kind = tile.kind, battery = battery, tile = tile });

            // 3. Advance the cursor to this tile's exit, for the next tile to align to.
            cursorPos = tile.ExitPosition;
            cursorRot = Quaternion.LookRotation(ForwardAt(tile, 1f), Vector3.up);
        }

        // Estimates facing direction at the start/end of a tile's curve by
        // sampling a point just inside it.
        private Vector3 ForwardAt(RouteTile tile, float t)
        {
            float h = 0.03f;
            Vector3 a = tile.Evaluate(t <= 0.5f ? t : t - h);
            Vector3 b = tile.Evaluate(t <= 0.5f ? t + h : t);
            Vector3 d = b - a;
            d.y = 0f;
            return d.sqrMagnitude > 1e-6f ? d.normalized : tile.transform.forward;
        }

        private IEnumerable<TileKind> ShuffledEventOrder()
        {
            var order = new List<TileKind> { TileKind.RedLight, TileKind.CatchUp, TileKind.Corner };
            for (int i = order.Count - 1; i > 0; i--)
            {
                int j = _rng.Next(i + 1);
                (order[i], order[j]) = (order[j], order[i]);
            }
            return order;
        }

        private List<RouteTile> PoolFor(TileKind kind) => kind switch
        {
            TileKind.RedLight => redLightTiles,
            TileKind.CatchUp  => catchUpTiles,
            TileKind.Corner   => cornerTiles,
            _                 => fillerTiles
        };

        private RouteTile PickRandom(List<RouteTile> pool)
        {
            if (pool == null || pool.Count == 0) return null;
            return pool[_rng.Next(pool.Count)];
        }

        private void Clear()
        {
            foreach (var go in _spawned) if (go != null) Destroy(go);
            _spawned.Clear();
            OrderedTiles.Clear();
            Events.Clear();
        }
    }
}