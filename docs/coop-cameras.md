# Co-op camera scenes: story or personal

The mission scripts hold the one camera with `pushCam` ... `popCam`, for the story's cut-scenes and for short moments of
the player's own alike. In a co-op game (docs/multiplayer.md "Co-op story missions") the first are everyone's and the
second one player's. This is the survey of every `pushCam` of every stock mission of both games (A01, S01–S07, T01–T17,
Nitro B01, P01–P19: 45 scripts, **190 scenes**), made with `i76 fsm-disasm` and `CoopCameras.Analyze`, and what the game does with
each.

## The rule

A scene is **personal** when it is triggered by one player's own action, frames that player's car, and is short and
alone: no dialogue but the player's own quip, no teleport, no order to any car, no objective or flag. By bytecode
(`src/I76.Sim/Fsm/CoopCameras.cs`, recognised as `CoopRoutines` are, not by name):

1. the `pushCam` is in a thread's own first routine, and the actor the scene watches is the player (a group slot);
2. the nearest condition before it, with no yield between, is on the player: `isAirborne` (a jump), or `isWithinSqNav` /
   `isWithinNav` / `isWithin` of a zone;
3. a zone alone is not enough (a story's cue at a place is the same code): the thread must do nothing else (the
   leave-the-map camera, a jump zone's own thread), or the trigger is a jump (`isAirborne`);
4. everything the scene can do until its `popCam` (or the thread's return) is a camera op on the player (or on no object:
   `camPosDir`), a timer, a condition on the player, the skip key, or `cbFromPrior` with the player as speaker.

Everything else is **story**, and so is anything the analysis cannot resolve (a `pushCam` in a called routine, a camera on a
local): the default when in doubt.

## What the game does

- A story scene is as before: the script holds the one camera, the lead is driven, the others wait where they are and form
  up behind the lead when it lets go; every screen shows it; only the host's Esc skips it.
- A personal scene runs on the thread's matched human (`Thread.Match`: the one who jumped, who reached the exit; a
  per-human leave-map thread's own seat). That thread's group arguments are that human alone while it lasts. The script's
  camera ops go to **that seat's own camera** (`ScriptHost.PersonalView`), the global camera and the lead are not touched:
  the other players keep their view and their controls, the AI and the story go on. The jumper's keys do nothing while it
  lasts, as the single player's do in a cut-scene (`SimSession.CutsceneOf`); their car is neither frozen nor given to the AI.
  The scene's CB line (S03's) is that player's radio alone (as a leave-map warning is). The snapshot's tail carries
  every seat's personal camera and each client shows its own seat's; the client's view is then the scene, and its Esc is
  the menu (a personal scene is no one's to skip: it ends by its own timer).
- A personal scene ends when its script pops the camera, its thread returns or starts over, its human leaves the game,
  dies and comes back, or is put back beside a teammate after driving off the map, or after 90 s whatever the script says.
- **The leave-the-map camera** (every mission that has exits, pc 2160 in Interstate '76's library thread and 486 in Nitro's,
  `isWithinSqNav(exit, player, 50 | 80)` then `camPosObj(exit, h, player)` until the player is 2500 away) used to take the
  global camera when any human reached an exit and, as the thread then returns without a `popCam`, kept it for good. It is
  personal now, and a human who reaches the exit is put back beside a teammate at once (the leave-map routine), which
  ends it.

## Counts


- 190 scenes in 45 scripts: **53 personal**, 137 story.
- Personal: 41 leave-the-map cameras (one per mission that runs an exit thread: S01, S03–S05, S07, T01–T17, P01–P19) and 12 jump cameras.
- Story: the scripts' intros, dialogue, plot cut-scenes, teleports, death cameras, finales, and Nitro's races' shared camera routine (in a called routine).

## The personal scenes

| Mission | Routine (threads) | pc | Trigger | Frames |
|---|---|---|---|---|
| S03 | @2180 (1) | 2241 | the player is airborne | camposobj on the player; the player's own CB line |
| S03 | @2180 (1) | 2260 | the player is airborne | camposobj on the player; the player's own CB line |
| S05 | @2180 (1) | 2233 | the player is airborne | camposobj on the player |
| T02 | @4525 (1) | 4536 | the player is in a zone | camposobj on the player |
| T05 | @4664 (1) | 4678 | the player is airborne | camposobj on the player |
| T06 | @4760 (1) | 4774 | the player is airborne | camposobj on the player |
| T06 | @4793 (1) | 4807 | the player is airborne | camobjdir on the player |
| T06 | @4835 (1) | 4849 | the player is airborne | camposdir |
| T06 | @4873 (1) | 4887 | the player is airborne | camposobj on the player |
| T06 | @4906 (1) | 4920 | the player is airborne | camposobj on the player |
| T10 | @3393 (1) | 3408 | the player is airborne | camposobj on the player |
| T12 | @4115 (1) | 4129 | the player is airborne | camposobj on the player |
| leave the map: S01, S03, S04, S05, S07, T01, T02, T03, T04, T05, T06, T07, T08, T09, T10, T11, T12, T13, T14, T15, T16, T17 | the exit threads | 2160 | the player is within 50 (Nitro 80) of an exit point | camposobj on the player |
| leave the map: P01, P02, P03, P04, P05, P06, P07, P08, P09, P10, P11, P12, P13, P14, P15, P16, P17, P18, P19 | the exit threads | 486 | the player is within 50 (Nitro 80) of an exit point | camposobj on the player |

## Ambiguous: story by default

A player's action triggers each of these and they frame the player's car, but they sit in a thread that runs the story as
well, or end on a flag another thread sets, or say more than the player's quip; each could be personal, and none is.

| Mission | pc | Trigger | What it is |
|---|---|---|---|
| A01 | 2160 | the player is in a zone | an exit-style watch on others (A01 has no exit thread running) |
| S03 | 2281 | the player is airborne | the third jump of S03: the circus fall, ends in failAllObj (the mission) |
| S06 | 2330 | the player is airborne | the jump to the last cop: held until another thread sets the flag that ends it |
| T02 | 4066 | the player is in a zone | a pass-by shot of the road as the player nears "looknow", in the story's thread |
| T05 | 3594 | the player is in a zone | the construction site's approach shot (a travelling camera with the player), in the story's thread |
| T06 | 4004 | the player is in a zone | the hill's fixed camera when the player reaches the gas station, in the story's thread |
| T07 | 2853 | the player is in a zone | the player nears the lens: camera on others and the story's lines |
| T07 | 3218 | the player is in a zone | the finale at the gate block: success, successAll |
| T15 | 2453 | the player is in a zone | the overlook shot, ends on the player's death |
| P18 | 3168 | the player is in a zone | the bridge scene: others, objectives, lines |

## Mid-mission movies

`playMovie` is in two scripts only, T05 (the player within 15 of the site: `playMovie`, then the lead is teleported) and T06;
Nitro has none. They are story: the movie stops the game for everyone (docs/multiplayer.md "Movies").

## The whole survey

Mission, the routine's entry pc (the threads that start there), the `pushCam`'s pc, what triggers it, what it frames and
does until its `popCam`, the verdict and why. Generated by `CoopCameras.Analyze`; `CoopCamerasTests` pins the counts.

| Mission | Routine (threads) | pc | Trigger | Frames / does | Verdict | Why |
|---|---|---|---|---|---|---|
| A01 | @137 (1) | 2160 | the player is in a zone | camposobj on others | Story | a zone's cue inside a thread that does other things too |
| A01 | @2180 (1) | 2189 | none (the script's own moment) | camobjdir on others + camposobj on the player | Story | begins unconditionally (the script's own moment) |
| S01 | @2152 (1) | 2160 | the player is in a zone | camposobj on the player | Personal | a short moment of the player's own, framing their car |
| S01 | @2354 (1) | 2358 | none (the script's own moment) | camtransobj on the player; camisarrived, killcb | Story | begins unconditionally (the script's own moment) |
| S02 | @0 (0) | 2160 | iswithinsqnav | camposobj on others | Story | in a called routine: whose scene it is, is not known here |
| S02 | @2279 (1) | 2288 | none (the script's own moment) | camposdir + camposobj on the player; cbfromprior | Story | begins unconditionally (the script's own moment) |
| S02 | @2279 (1) | 2351 | isequal | camf12 on others; isequal, the player's own CB line | Story | triggered by isequal, not by the player |
| S02 | @2971 (1) | 2986 | hplesser | camposdir; cbfromprior, cbprior, destroy, iscbempty, isdead, set | Story | triggered by hplesser, not by the player |
| S03 | @2152 (3) | 2160 | the player is in a zone | camposobj on the player | Personal | a short moment of the player's own, framing their car |
| S03 | @2180 (1) | 2241 | the player is airborne | camposobj on the player; the player's own CB line | Personal | a short moment of the player's own, framing their car |
| S03 | @2180 (1) | 2260 | the player is airborne | camposobj on the player; the player's own CB line | Personal | a short moment of the player's own, framing their car |
| S03 | @2180 (1) | 2281 | the player is airborne | camposobj on the player; failallobj, the player's own CB line | Story | does failallobj |
| S03 | @2387 (1) | 2391 | none (the script's own moment) | camposobj on the player; cbprior, isequal, the player's own CB line | Story | begins unconditionally (the script's own moment) |
| S04 | @2152 (6) | 2160 | the player is in a zone | camposobj on the player | Personal | a short moment of the player's own, framing their car |
| S04 | @2222 (1) | 2239 | none (the script's own moment) | camposobj on the player; isequal, killcb | Story | begins unconditionally (the script's own moment) |
| S04 | @3229 (1) | 3295 | isdead | camposdir; cbprior, failallobj, iscbempty, stopcb | Story | triggered by isdead, not by the player |
| S05 | @2152 (5) | 2160 | the player is in a zone | camposobj on the player | Personal | a short moment of the player's own, framing their car |
| S05 | @2180 (1) | 2189 | none (the script's own moment) | camposdir; isequal | Story | begins unconditionally (the script's own moment) |
| S05 | @2180 (1) | 2233 | the player is airborne | camposobj on the player | Personal | a short moment of the player's own, framing their car |
| S05 | @2334 (1) | 2407 | isequal | camposobj on others; cbprior, destroy, iscbempty, isdead, set | Story | triggered by isequal, not by the player |
| S05 | @2334 (1) | 2456 | isequal | camposobj on others; cbprior, destroy, iscbempty, isdead, set | Story | triggered by isequal, not by the player |
| S05 | @2334 (1) | 2586 | isequal | camposobj on others; cbprior, destroy, iscbempty, isdead, set | Story | triggered by isequal, not by the player |
| S06 | @1734 (9) | 2160 | iswithinsqnav | camposobj on others | Story | triggered by iswithinsqnav, not by the player |
| S06 | @2284 (1) | 2288 | none (the script's own moment) | camobjdir on the player; isequal | Story | begins unconditionally (the script's own moment) |
| S06 | @2284 (1) | 2330 | the player is airborne | camposobj on the player; isequal | Story | does isequal |
| S07 | @2152 (7) | 2160 | the player is in a zone | camposobj on the player | Personal | a short moment of the player's own, framing their car |
| S07 | @2227 (1) | 2231 | none (the script's own moment) | camobjobj on others + camtransobj on others; camisarrived, isequal | Story | begins unconditionally (the script's own moment) |
| S07 | @2227 (1) | 2280 | isequal | camposobj on others | Story | triggered by isequal, not by the player |
| T01 | @2152 (2) | 2160 | the player is in a zone | camposobj on the player | Personal | a short moment of the player's own, framing their car |
| T01 | @2180 (1) | 2189 | none (the script's own moment) | camobjobj on others + camposobj on others; isequal, killcb | Story | begins unconditionally (the script's own moment) |
| T01 | @2180 (1) | 2276 | iscbempty | camposobj on others; isequal, killcb, set | Story | triggered by iscbempty, not by the player |
| T02 | @2152 (4) | 2160 | the player is in a zone | camposobj on the player | Personal | a short moment of the player's own, framing their car |
| T02 | @4003 (1) | 4007 | none (the script's own moment) | camposdir + camposobj on the player | Story | begins unconditionally (the script's own moment) |
| T02 | @4003 (1) | 4066 | the player is in a zone | camposobj on the player | Story | a zone's cue inside a thread that does other things too |
| T02 | @4003 (1) | 4095 | isdead | camobjobj on others | Story | triggered by isdead, not by the player |
| T02 | @4525 (1) | 4536 | the player is in a zone | camposobj on the player | Personal | a short moment of the player's own, framing their car |
| T03 | @2152 (2) | 2160 | the player is in a zone | camposobj on the player | Personal | a short moment of the player's own, framing their car |
| T03 | @3101 (1) | 3105 | none (the script's own moment) | camposobj on the player | Story | begins unconditionally (the script's own moment) |
| T03 | @3101 (1) | 3135 | isequal | camf12 on others; isdead | Story | triggered by isequal, not by the player |
| T04 | @2152 (2) | 2160 | the player is in a zone | camposobj on the player | Personal | a short moment of the player's own, framing their car |
| T04 | @2271 (1) | 2277 | none (the script's own moment) | camobjobj on others + camobjobj on the player + camposobj on the player + camtransobj on others; camisarrived, cbfromprior, cbprior, iscbempty, killcb, reveal, set, true | Story | begins unconditionally (the script's own moment) |
| T05 | @2152 (7) | 2160 | the player is in a zone | camposobj on the player | Personal | a short moment of the player's own, framing their car |
| T05 | @3585 (1) | 3594 | the player is in a zone | camtransobj on the player | Story | a zone's cue inside a thread that does other things too |
| T05 | @3585 (1) | 3633 | timegreater | camposobj on the player; isequal | Story | triggered by timegreater, not by the player |
| T05 | @3585 (1) | 3699 | isequal | camf12 on others + camposdir; cbprior, isdead | Story | triggered by isequal, not by the player |
| T05 | @4580 (1) | 4595 | isattacked | camf12 on others; destroy, iscbempty, isdead, set | Story | triggered by isattacked, not by the player |
| T05 | @4664 (1) | 4678 | the player is airborne | camposobj on the player | Personal | a short moment of the player's own, framing their car |
| T06 | @2152 (4) | 2160 | the player is in a zone | camposobj on the player | Personal | a short moment of the player's own, framing their car |
| T06 | @3924 (1) | 3928 | none (the script's own moment) | camf12 on others + camposdir + camposobj on the player | Story | begins unconditionally (the script's own moment) |
| T06 | @3924 (1) | 4004 | the player is in a zone | camposdir | Story | a zone's cue inside a thread that does other things too |
| T06 | @3924 (1) | 4052 | isequal | camf12 on others | Story | triggered by isequal, not by the player |
| T06 | @3924 (1) | 4129 | isequal | camf12 on others; isdead | Story | triggered by isequal, not by the player |
| T06 | @4146 (1) | 4151 | isdead | camobjobj on others | Story | triggered by isdead, not by the player |
| T06 | @4760 (1) | 4774 | the player is airborne | camposobj on the player | Personal | a short moment of the player's own, framing their car |
| T06 | @4793 (1) | 4807 | the player is airborne | camobjdir on the player | Personal | a short moment of the player's own, framing their car |
| T06 | @4835 (1) | 4849 | the player is airborne | camposdir | Personal | a short moment of the player's own, framing their car |
| T06 | @4873 (1) | 4887 | the player is airborne | camposobj on the player | Personal | a short moment of the player's own, framing their car |
| T06 | @4906 (1) | 4920 | the player is airborne | camposobj on the player | Personal | a short moment of the player's own, framing their car |
| T07 | @2152 (1) | 2160 | the player is in a zone | camposobj on the player | Personal | a short moment of the player's own, framing their car |
| T07 | @2361 (1) | 2365 | none (the script's own moment) | camposobj on the player; isequal | Story | begins unconditionally (the script's own moment) |
| T07 | @2823 (1) | 2853 | the player is in a zone | camf12 on others; cbprior, iscbempty, isequal, killcb, reveal, the player's own CB line | Story | a zone's cue inside a thread that does other things too |
| T07 | @3198 (1) | 3218 | the player is in a zone | camobjobj on the player + camposobj on the player; cbprior, failallobj, iscbempty, isdead, set, success, successall, the player's own CB line | Story | a zone's cue inside a thread that does other things too |
| T08 | @2152 (4) | 2160 | the player is in a zone | camposobj on the player | Personal | a short moment of the player's own, framing their car |
| T08 | @2225 (1) | 2230 | none (the script's own moment) | camposobj on others; isequal | Story | begins unconditionally (the script's own moment) |
| T08 | @2366 (1) | 2386 | timegreater | camposobj on others | Story | triggered by timegreater, not by the player |
| T08 | @2407 (1) | 2413 | isdead | camposobj on others | Story | triggered by isdead, not by the player |
| T08 | @2833 (1) | 3032 | timegreater | camobjobj on the player; iscbempty, successall | Story | triggered by timegreater, not by the player |
| T09 | @2152 (2) | 2160 | the player is in a zone | camposobj on the player | Personal | a short moment of the player's own, framing their car |
| T09 | @2180 (1) | 2185 | none (the script's own moment) | camobjobj on others + camposobj on others + camposobj on the player; cbfromprior, cbprior, iscbempty, isequal | Story | begins unconditionally (the script's own moment) |
| T10 | @2152 (5) | 2160 | the player is in a zone | camposobj on the player | Personal | a short moment of the player's own, framing their car |
| T10 | @2180 (1) | 2185 | none (the script's own moment) | camposobj on the player | Story | begins unconditionally (the script's own moment) |
| T10 | @3393 (1) | 3408 | the player is airborne | camposobj on the player | Personal | a short moment of the player's own, framing their car |
| T11 | @2152 (6) | 2160 | the player is in a zone | camposobj on the player | Personal | a short moment of the player's own, framing their car |
| T11 | @2519 (1) | 2524 | none (the script's own moment) | camobjobj on others; set | Story | begins unconditionally (the script's own moment) |
| T12 | @2152 (4) | 2160 | the player is in a zone | camposobj on the player | Personal | a short moment of the player's own, framing their car |
| T12 | @3646 (1) | 3656 | none (the script's own moment) | camobjobj on others; cbprior, killcb, set | Story | begins unconditionally (the script's own moment) |
| T12 | @3646 (1) | 3750 | isequal | camposobj on others | Story | triggered by isequal, not by the player |
| T12 | @4115 (1) | 4129 | the player is airborne | camposobj on the player | Personal | a short moment of the player's own, framing their car |
| T13 | @2152 (4) | 2160 | the player is in a zone | camposobj on the player | Personal | a short moment of the player's own, framing their car |
| T13 | @3588 (1) | 3596 | isdead | camposdir; isdead, set | Story | triggered by isdead, not by the player |
| T14 | @2152 (4) | 2160 | the player is in a zone | camposobj on the player | Personal | a short moment of the player's own, framing their car |
| T14 | @2180 (1) | 2214 | none (the script's own moment) | camposobj on others | Story | begins unconditionally (the script's own moment) |
| T14 | @2937 (1) | 2950 | iswithin | camposobj on others | Story | triggered by iswithin, not by the player |
| T15 | @2152 (1) | 2160 | the player is in a zone | camposobj on the player | Personal | a short moment of the player's own, framing their car |
| T15 | @2414 (1) | 2419 | none (the script's own moment) | camobjobj on others; isequal | Story | begins unconditionally (the script's own moment) |
| T15 | @2414 (1) | 2453 | the player is in a zone | camposobj on the player; isdead | Story | a zone's cue inside a thread that does other things too |
| T16 | @2152 (2) | 2160 | the player is in a zone | camposobj on the player | Personal | a short moment of the player's own, framing their car |
| T16 | @3209 (1) | 3213 | none (the script's own moment) | camposobj on the player | Story | begins unconditionally (the script's own moment) |
| T17 | @2152 (1) | 2160 | the player is in a zone | camposobj on the player | Personal | a short moment of the player's own, framing their car |
| T17 | @3056 (1) | 3060 | none (the script's own moment) | camposobj on the player | Story | begins unconditionally (the script's own moment) |
| T17 | @3056 (1) | 3092 | isequal | camposobj on others | Story | triggered by isequal, not by the player |
| B01 | @0 (0) | 486 | iswithinsqnav | camposobj on others | Story | in a called routine: whose scene it is, is not known here |
| B01 | @2738 (0) | 2812 | none (the script's own moment) | camposobj on others; true | Story | in a called routine: whose scene it is, is not known here |
| B01 | @2830 (1) | 2843 | iskeypress | camtransobj on the player; cbfromprior, killcb, set, the player's own CB line, true | Story | triggered by iskeypress, not by the player |
| B01 | @2830 (1) | 3030 | isequal | camobjobj on others + camobjobj on the player + camposobj on others + camposobj on the player; cbfromprior, iscbempty, isequal, killcb, set, the player's own CB line, true | Story | triggered by isequal, not by the player |
| B01 | @2830 (1) | 3264 | isequal | camobjobj on others + camobjobj on the player; cbfromprior, iscbempty, killcb, set, the player's own CB line | Story | triggered by isequal, not by the player |
| B01 | @2830 (1) | 3279 | isequal | camobjobj on others + camobjobj on the player; cbfromprior, iscbempty, isequal, killcb, set, the player's own CB line | Story | triggered by isequal, not by the player |
| B01 | @2830 (1) | 3447 | isequal | camobjobj on others + camposobj on the player; cbfromprior, iscbempty | Story | triggered by isequal, not by the player |
| B01 | @2830 (1) | 3610 | isequal | camposobj on the player; isdead | Story | triggered by isequal, not by the player |
| P01 | @478 (4) | 486 | the player is in a zone | camposobj on the player | Personal | a short moment of the player's own, framing their car |
| P01 | @2738 (0) | 2812 | none (the script's own moment) | camposobj on others; true | Story | in a called routine: whose scene it is, is not known here |
| P01 | @2830 (1) | 2835 | none (the script's own moment) | camobjobj on others + camtransobj on the player; cbfromprior, iscbempty, the player's own CB line | Story | begins unconditionally (the script's own moment) |
| P01 | @2830 (1) | 2951 | isequal | camobjobj on others + camobjobj on the player; cbfromprior, iscbempty, isdead, set, the player's own CB line | Story | triggered by isequal, not by the player |
| P02 | @478 (2) | 486 | the player is in a zone | camposobj on the player | Personal | a short moment of the player's own, framing their car |
| P02 | @2738 (0) | 2812 | none (the script's own moment) | camposobj on others; true | Story | in a called routine: whose scene it is, is not known here |
| P02 | @2830 (1) | 2836 | none (the script's own moment) | camobjobj on others + camposobj on others + camtransobj on others; cbfromprior, iscbempty, isequal, the player's own CB line | Story | begins unconditionally (the script's own moment) |
| P02 | @2830 (1) | 3057 | isequal | camobjobj on others + camobjobj on the player; cbfromprior, iscbempty, isdead, set, the player's own CB line | Story | triggered by isequal, not by the player |
| P02 | @2830 (1) | 3078 | iswithinnav | camobjobj on the player; isdead | Story | triggered by iswithinnav, not by the player |
| P03 | @478 (3) | 486 | the player is in a zone | camposobj on the player | Personal | a short moment of the player's own, framing their car |
| P03 | @2738 (0) | 2812 | none (the script's own moment) | camposobj on others; true | Story | in a called routine: whose scene it is, is not known here |
| P03 | @2830 (1) | 2834 | none (the script's own moment) | camobjobj on others + camtransdir; cbfromprior, isequal, the player's own CB line | Story | begins unconditionally (the script's own moment) |
| P03 | @2830 (1) | 3041 | isequal | camobjobj on others + camobjobj on the player; cbfromprior, iscbempty, isdead, set, the player's own CB line | Story | triggered by isequal, not by the player |
| P04 | @478 (2) | 486 | the player is in a zone | camposobj on the player | Personal | a short moment of the player's own, framing their car |
| P04 | @2738 (0) | 2812 | none (the script's own moment) | camposobj on others; true | Story | in a called routine: whose scene it is, is not known here |
| P04 | @2830 (1) | 2835 | none (the script's own moment) | camobjobj on others + camposdir; cbfromprior, iscbempty, set, the player's own CB line | Story | begins unconditionally (the script's own moment) |
| P04 | @2830 (1) | 3057 | iswithinnav | camobjobj on others + camposobj on others; cbfromprior, success, the player's own CB line | Story | triggered by iswithinnav, not by the player |
| P05 | @478 (4) | 486 | the player is in a zone | camposobj on the player | Personal | a short moment of the player's own, framing their car |
| P05 | @2738 (0) | 2812 | none (the script's own moment) | camposobj on others; true | Story | in a called routine: whose scene it is, is not known here |
| P05 | @2830 (1) | 2835 | none (the script's own moment) | camobjobj on others + camposdir + camtransobj on the player; cbfromprior, isequal, the player's own CB line | Story | begins unconditionally (the script's own moment) |
| P05 | @2830 (1) | 3038 | iswithinenemy | camobjobj on others + camobjobj on the player; cbfromprior, cbprior, iscbempty, isdead, set, the player's own CB line | Story | triggered by iswithinenemy, not by the player |
| P06 | @478 (2) | 486 | the player is in a zone | camposobj on the player | Personal | a short moment of the player's own, framing their car |
| P06 | @2738 (0) | 2812 | none (the script's own moment) | camposobj on others; true | Story | in a called routine: whose scene it is, is not known here |
| P06 | @2830 (1) | 2895 | none (the script's own moment) | camobjobj on others + camposdir; isequal, killcb, set | Story | begins unconditionally (the script's own moment) |
| P06 | @2830 (1) | 3017 | iscbempty | camobjobj on others + camobjobj on the player; cbfromprior, iscbempty, killcb, set | Story | triggered by iscbempty, not by the player |
| P06 | @2830 (1) | 3205 | timegreater | camobjobj on others; isdead | Story | triggered by timegreater, not by the player |
| P06 | @4405 (1) | 4436 | isequal | camposobj on the player; set | Story | triggered by isequal, not by the player |
| P07 | @478 (2) | 486 | the player is in a zone | camposobj on the player | Personal | a short moment of the player's own, framing their car |
| P07 | @2738 (0) | 2812 | none (the script's own moment) | camposobj on others; true | Story | in a called routine: whose scene it is, is not known here |
| P07 | @2830 (1) | 2834 | none (the script's own moment) | camobjobj on others + camposobj on the player; cbfromprior, iscbempty, the player's own CB line | Story | begins unconditionally (the script's own moment) |
| P07 | @2830 (1) | 2996 | timegreater | camobjobj on others + camobjobj on the player; isdead, isequal | Story | triggered by timegreater, not by the player |
| P08 | @478 (4) | 486 | the player is in a zone | camposobj on the player | Personal | a short moment of the player's own, framing their car |
| P08 | @2738 (0) | 2812 | none (the script's own moment) | camposobj on others; true | Story | in a called routine: whose scene it is, is not known here |
| P08 | @2830 (1) | 2834 | none (the script's own moment) | camobjobj on others + camobjobj on the player + camtransobj on the player; camisarrived, cbfromprior, cbprior, iscbempty, the player's own CB line | Story | begins unconditionally (the script's own moment) |
| P08 | @2830 (1) | 3040 | iswithinnav | camobjobj on others + camobjobj on the player + camposobj on others; cbfromprior, cbprior, iscbempty, isdead, set, the player's own CB line | Story | triggered by iswithinnav, not by the player |
| P09 | @478 (3) | 486 | the player is in a zone | camposobj on the player | Personal | a short moment of the player's own, framing their car |
| P09 | @2738 (0) | 2812 | none (the script's own moment) | camposobj on others; true | Story | in a called routine: whose scene it is, is not known here |
| P09 | @2830 (1) | 2835 | none (the script's own moment) | camobjobj on others + camobjobj on the player + camposobj on the player; cbfromprior, cbprior, iscbempty, the player's own CB line | Story | begins unconditionally (the script's own moment) |
| P09 | @2830 (1) | 3064 | isequal | camobjobj on others; cbfromprior, cbprior, iscbempty, the player's own CB line | Story | triggered by isequal, not by the player |
| P09 | @2830 (1) | 3147 | isequal | camobjobj on others + camobjobj on the player; cbfromprior, cbprior, iscbempty, isdead, set, the player's own CB line | Story | triggered by isequal, not by the player |
| P09 | @2830 (1) | 3188 | isgreater | camtransobj on others; cbprior, isdead, set, stopcb | Story | triggered by isgreater, not by the player |
| P10 | @478 (3) | 486 | the player is in a zone | camposobj on the player | Personal | a short moment of the player's own, framing their car |
| P10 | @2738 (0) | 2812 | none (the script's own moment) | camposobj on others; true | Story | in a called routine: whose scene it is, is not known here |
| P10 | @2830 (1) | 2834 | none (the script's own moment) | camposobj on the player; cbprior, iscbempty, isequal, rand, set, the player's own CB line | Story | begins unconditionally (the script's own moment) |
| P10 | @2830 (1) | 3051 | isequal | camobjobj on others; cbfromprior, cbprior, iscbempty, isdead, set, the player's own CB line | Story | triggered by isequal, not by the player |
| P10 | @2830 (1) | 3068 | isequal | camobjobj on the player; isdead | Story | triggered by isequal, not by the player |
| P11 | @478 (4) | 486 | the player is in a zone | camposobj on the player | Personal | a short moment of the player's own, framing their car |
| P11 | @2738 (0) | 2812 | none (the script's own moment) | camposobj on others; true | Story | in a called routine: whose scene it is, is not known here |
| P11 | @2830 (1) | 2834 | none (the script's own moment) | camobjobj on the player + camposobj on the player; cbprior, iscbempty, set, the player's own CB line | Story | begins unconditionally (the script's own moment) |
| P11 | @2830 (1) | 3112 | isequal | camobjobj on the player; isdead | Story | triggered by isequal, not by the player |
| P11 | @2830 (1) | 3127 | timegreater | camobjobj on the player; isdead | Story | triggered by timegreater, not by the player |
| P12 | @478 (4) | 486 | the player is in a zone | camposobj on the player | Personal | a short moment of the player's own, framing their car |
| P12 | @2738 (0) | 2812 | none (the script's own moment) | camposobj on others; true | Story | in a called routine: whose scene it is, is not known here |
| P12 | @2830 (1) | 2836 | none (the script's own moment) | camobjobj on others; cbprior, killcb, the player's own CB line | Story | begins unconditionally (the script's own moment) |
| P12 | @2830 (1) | 3093 | isequal | camobjobj on others + camposobj on others; cbfromprior, cbprior, isdead, set, the player's own CB line | Story | triggered by isequal, not by the player |
| P13 | @478 (4) | 486 | the player is in a zone | camposobj on the player | Personal | a short moment of the player's own, framing their car |
| P13 | @2738 (0) | 2812 | none (the script's own moment) | camposobj on others; true | Story | in a called routine: whose scene it is, is not known here |
| P13 | @2830 (1) | 2837 | none (the script's own moment) | camobjobj on others + camobjobj on the player + camposdir + camtransobj on the player; cbprior, iscbempty, the player's own CB line | Story | begins unconditionally (the script's own moment) |
| P13 | @2830 (1) | 3065 | iswithinenemy | camobjobj on the player; cbprior, isdead, set, the player's own CB line | Story | triggered by iswithinenemy, not by the player |
| P14 | @478 (2) | 486 | the player is in a zone | camposobj on the player | Personal | a short moment of the player's own, framing their car |
| P14 | @2738 (0) | 2812 | none (the script's own moment) | camposobj on others; true | Story | in a called routine: whose scene it is, is not known here |
| P14 | @2830 (1) | 2873 | none (the script's own moment) | camobjobj on others + camposobj on the player + camtransdir; isequal, teleport | Story | begins unconditionally (the script's own moment) |
| P14 | @2830 (1) | 3076 | isdead | camobjobj on others + camobjobj on the player; cbprior, iscbempty, isdead, set, the player's own CB line | Story | triggered by isdead, not by the player |
| P15 | @478 (4) | 486 | the player is in a zone | camposobj on the player | Personal | a short moment of the player's own, framing their car |
| P15 | @2738 (0) | 2812 | none (the script's own moment) | camposobj on others; true | Story | in a called routine: whose scene it is, is not known here |
| P15 | @2830 (1) | 2835 | none (the script's own moment) | camobjobj on others + camtransobj on others; cbprior, iscbempty, killcb, set | Story | begins unconditionally (the script's own moment) |
| P15 | @2830 (1) | 2946 | isequal | camobjobj on the player; cbprior, isdead, set, the player's own CB line | Story | triggered by isequal, not by the player |
| P15 | @2830 (1) | 2992 | isequal | camobjobj on others + camobjobj on the player; cbprior, iscbempty, isdead, set | Story | triggered by isequal, not by the player |
| P15 | @2830 (1) | 3023 | isequal | camobjobj on others + camobjobj on the player; cbprior, iscbempty, isdead, set | Story | triggered by isequal, not by the player |
| P15 | @2830 (1) | 3054 | isequal | camobjobj on others + camobjobj on the player; cbprior, iscbempty, isdead, set | Story | triggered by isequal, not by the player |
| P16 | @478 (2) | 486 | the player is in a zone | camposobj on the player | Personal | a short moment of the player's own, framing their car |
| P16 | @2738 (0) | 2812 | none (the script's own moment) | camposobj on others; true | Story | in a called routine: whose scene it is, is not known here |
| P16 | @2830 (1) | 2834 | none (the script's own moment) | camobjobj on the player + camposdir + camposobj on the player; cbprior, iscbempty, the player's own CB line | Story | begins unconditionally (the script's own moment) |
| P16 | @2830 (1) | 2978 | isdead | camobjobj on the player; cbprior, isdead, set, the player's own CB line | Story | triggered by isdead, not by the player |
| P17 | @478 (3) | 486 | the player is in a zone | camposobj on the player | Personal | a short moment of the player's own, framing their car |
| P17 | @2738 (0) | 2812 | none (the script's own moment) | camposobj on others; true | Story | in a called routine: whose scene it is, is not known here |
| P17 | @2830 (1) | 2836 | none (the script's own moment) | camobjobj on others + camobjobj on the player; cbfromprior, iscbempty, the player's own CB line | Story | begins unconditionally (the script's own moment) |
| P17 | @2830 (1) | 3247 | isequal | camobjobj on others + camobjobj on the player + camtransobj on others; cbfromprior, iscbempty, the player's own CB line | Story | triggered by isequal, not by the player |
| P17 | @2830 (1) | 3262 | isequal | camobjobj on the player; isdead | Story | triggered by isequal, not by the player |
| P18 | @478 (2) | 486 | the player is in a zone | camposobj on the player | Personal | a short moment of the player's own, framing their car |
| P18 | @2738 (0) | 2812 | none (the script's own moment) | camposobj on others; true | Story | in a called routine: whose scene it is, is not known here |
| P18 | @2830 (1) | 2834 | none (the script's own moment) | camobjobj on others + camobjobj on the player + camposobj on the player; iscbempty, killcb, set, the player's own CB line | Story | begins unconditionally (the script's own moment) |
| P18 | @2830 (1) | 3168 | the player is in a zone | camobjobj on others; cbprior, iscbempty, killcb, reveal, set, success, the player's own CB line | Story | a zone's cue inside a thread that does other things too |
| P18 | @2830 (1) | 3313 | iswithinenemy | camobjobj on others + camobjobj on the player; cbfromprior, iscbempty, isdead, set, the player's own CB line | Story | triggered by iswithinenemy, not by the player |
| P19 | @478 (2) | 486 | the player is in a zone | camposobj on the player | Personal | a short moment of the player's own, framing their car |
| P19 | @2738 (0) | 2812 | none (the script's own moment) | camposobj on others; true | Story | in a called routine: whose scene it is, is not known here |
| P19 | @2830 (1) | 2836 | none (the script's own moment) | camobjobj on others + camobjobj on the player + camposobj on others + camposobj on the player; cbprior, isequal, killcb, the player's own CB line | Story | begins unconditionally (the script's own moment) |
| P19 | @2830 (1) | 3117 | isequal | camobjobj on others + camobjobj on the player + camposobj on others; isdead, isequal, pushcam, teleport | Story | triggered by isequal, not by the player |
| P19 | @2830 (1) | 3137 | isequal | camobjobj on others + camposobj on others; isdead | Story | triggered by isequal, not by the player |
| P19 | @2830 (1) | 3145 | isequal | camobjobj on others + camobjobj on the player; cbprior, iscbempty, isdead | Story | triggered by isequal, not by the player |
| P19 | @2830 (1) | 3206 | isdead | camobjobj on the player; isdead | Story | triggered by isdead, not by the player |
