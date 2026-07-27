# Narration audio

Drop the final narration `.mp3` files in this folder. File names must match
exactly (case-insensitive on Windows, but keep the casing anyway — the project
is also opened on macOS/Linux, which is not).

`NarrationController.FileName()` in
`Assets/Scripts/Session/NarrationController.cs` is the authoritative mapping.
If a recording is renamed, change it there — the Inspector's "Auto-assign by
file name" button reads that same method, so the two can't disagree.

| File | When it plays |
|---|---|
| `00_intro.mp3` | Session start — welcome and task briefing. |
| `0x_meditation.mp3` | After **every** condition intro, immediately before that drive. Shared clip, played 3×. **This is also the baseline** — see below. |
| `01a_explicit.mp3` | Intro to the **first** drive, when it's the Explicit condition. |
| `01b_implicit.mp3` | Intro to the **first** drive, when it's the Implicit condition. |
| `01c_explore.mp3` | Intro to the **first** drive, when it's the Explore (FreeRoam) condition. |
| `02_trialEval1.mp3` | After drive 1 has parked — cues the evaluation questionnaire. |
| `0x_breakAsk.mp3` | After evaluations 1 and 2. Shared clip, played 2×. |
| `03a/b/c_*.mp3` | Intro to the **second** drive, by condition. |
| `04_trialEval2.mp3` | After drive 2 has parked. |
| `05a/b/c_*.mp3` | Intro to the **third** drive, by condition. |
| `06_trialEval3.mp3` | After drive 3 has parked. |
| `07_closing.mp3` | Session complete. (The interview happens in person, after this.) |
| `extra_emergencyStop.mp3` | Researcher hits EMERGENCY STOP. Cuts off whatever was playing. |
| `extra_exploreNudge.mp3` | Explore condition only. Researcher's PLAY EXPLORE NUDGE button, or automatically after `exploreNudgeIdleSeconds` of no slider activity if that's set above 0 on SessionController. |

Which of `a`/`b`/`c` plays in each slot follows the counterbalancing order
(1–6) picked in the researcher UI — e.g. order 4 is Explicit → FreeRoam →
Implicit, so it plays `01a_explicit`, `03c_explore`, `05b_implicit`.

## The meditation is the baseline

There is no separate stationary baseline phase. The physiological reference
means for each condition are accumulated during a window near the end of the
meditation track, while the music is still playing:

```
0:00 ─────────────── music ──────────────── 1:50 ══ measured ══ 2:00 ── tail ── 2:05
                                                 (10s averaged)      (5s ignored)
```

So `0x_meditation.mp3` should be **2:05 long**. The window is derived from the
clip's actual length at runtime (`length − 15s` to `length − 5s`), so a
different duration still works — it just moves the window. Both numbers are on
SessionController under **Baseline — measured DURING the meditation**.

The 5-second tail exists so the last moments — where the participant may
already be anticipating the drive — stay out of the reference.

At the end of the window the per-channel averages are printed to the Console as
`[Trial] BASELINE for condition N`.

This is why the meditation repeats before every condition rather than playing
once: each condition needs its own reference means.

## Import settings

For spoken narration, select the clips and set:

- **Load Type**: Decompress On Load (they're short and need to start instantly)
- **Preload Audio Data**: on
- **Force To Mono**: on, unless a recording is deliberately stereo

`0x_meditation` is the long one — Streaming or Compressed In Memory is fine
for it instead.

## Deliberately silent moments

These transitions have no recording and play nothing. That's intended — they
are the researcher speaking to the participant in person:

- self-parking after each drive
- granting a break / calling the participant back
- resuming after an emergency stop
