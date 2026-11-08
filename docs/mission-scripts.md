# Mission scripts as source

A mission's script (the FSM chunk in its `ADEF`) is bytecode. I26 turns it into a readable YAML source and back, byte for
byte: `i76 fsm-decompile` writes the source, `i76 fsm-compile` puts an edited source back into a copy of the mission, and
`i76 fsm-roundtrip` checks that every scripted mission of an install survives the trip unchanged. The code is in
`src/I76.Formats/Fsm` (`MissionScriptDecompiler`, `MissionScriptCompiler`, `MissionScriptYaml`); the YAML has a JSON Schema,
`src/I76.Formats/Fsm/mission-script.schema.json`, that editors use for validation and completion. This is the base for the
map editor's script view; the editor does not use it yet.

Status: all 90 scripted missions of the two games (Interstate '76 and Nitro Pack, `miss16` and `miss8`; 367,366
instructions) decompile to steps, with no raw instructions anywhere, and compile back to the identical file.

## The bytecode

The chunk holds, in order: the operation table (40-byte names; an `exec n` names its operation by its index here, and every
stock mission has the same 95), the actor table (a 40-byte label and the 8-byte name of the placed object it stands for, high
bits included), the sounds table (40-byte file names, `.wav` lines and `.smk` movies), the nav-path table (label and points),
the thread table (start instruction, parameter count, 40 data indices), the data cells (int32), the code (8-byte
instructions: a u32 opcode and a u32 operand).

A thread is created for each table entry at load, its parameters pointing at the data cells its entry lists, and every
thread runs once per frame, in table order, until it yields or ends. Threads talk through shared data cells; there is no
way to start or stop a thread from a script.

The code was written by a state-machine compiler, and every routine has one shape:

```
routine  := prologue 'jump S0' state+                 S0 = the next instruction
prologue := ('allocLocals n' | 'pushImm v')*          the locals: a run of n uninitialised ones, or one initialised to v
state    := rule* during 'yieldJump state'
rule     := cond 'branchIfFalse nextRule' stmt* ('jump state' | 'return n')
during   := one stmt                                  'exec null' when the state does nothing while waiting
cond     := A && B : A; branchIfFalse L; B; L:
            A || B : A; branchIfFalse +2; jump L; B; L:
            !A     : A; not
stmt     := args 'exec op' ['freeLocals k']  |  pushes 'call routine'
args     := per argument: 'argLocal -n' (a parameter: a pointer to a data cell), 'argLocalAddr n' (a local),
            or 'pushImm v; argLocalAddr t' (an immediate in a temporary slot t; 'freeLocals k' drops them after the operation)
```

What it means when it runs: each frame a thread tests its current state's rules in order; the first whose condition holds
runs its statements and moves to its target state **in the same frame**, whose rules are then tested at once; when no rule
holds, the `during` statement runs and the thread waits for the next frame. `return n` ends the thread. A rule that targets
its own state tests the state again at once (the original gives up on a thread after 1000 instructions in one frame).
Several tests consume the event they report (`isShot`, `isRammed`, `isAttacked`, `isArrived`, `isAtFollow`, `camIsArrived`,
`isKeypress`), so the order of rules and of the terms of a condition is behaviour, not style: the source keeps both exactly.

Stock content never runs a `call` (Nitro's missions hold one, in a routine no thread starts), and every mission begins with
the same library of routines (I76: 15, Nitro: 28), many of which no thread starts. The source keeps all of them.

## The source

```yaml
mission: T99
actors:                                   # the actor table, in order: key -> placement
  user: {object: vppirna1, id: 76707069726e6131}
  sheriff: {object: sherif1, id: 73686572696631c0}
paths:                                    # the nav-path table: reference only (edit paths with the path tool)
  road: {points: 4}
  road_2: {label: road, points: 4}        # a repeated label gets a unique key
sounds:                                   # the sounds table, in order: key -> file
  hello: hello.wav
globals:                                  # the data cells, in order: name -> initial value
  g0: actor.sheriff
  g1: 0
scripts:                                  # the routines, in code order
  escort:
    fields: [car, deaths]                 # the parameters, in stack order
    variables: {t: null, line: sound.hello}   # the locals: initial value, or null for none
    guards:                               # rules tested first in every step
      - when: isDead(car)
        then: inc(deaths)
        goto: stop
    steps:                                # one step = one state of the machine
      - do:
          - setSkill(car, 3, 3)
          - cbPrior(line, 1)
      - id: drive
        wait:                             # rules, first that holds wins
          - when: isAttacked(car) && !isEqualId(car, actor.user)
            then: startTimer(t)
            goto: fight
          - when: isWithinNav(path.road, car, 20)
            goto: restart
        while_waiting: goto(car, path.road, 30)   # the during action
      - wait_until: isArrived(car)        # one rule; then the next step
      - do: successAll(5)
        goto: stop
      - id: fight
        wait_until: timeGreater(t, 10)
        goto: drive
        while_waiting: attack(car, actor.user, 0)
threads:                                  # the thread table, in run order
  thread0: {script: escort, args: {car: g0, deaths: g1}}
```

### Steps

A step is one state. Its forms:

- `do: actions` — a single rule that always holds (the bytecode's `true` test): do the actions and go on.
- `wait_until: condition` with optional `then: actions` — a single rule: wait until it holds, then act and go on.
- `wait: [rules]` — several rules; each is `when: condition`, optional `then: actions`, and a `goto` or `steps`. A last
  `when: true` is an else.
- `while_waiting: actions` on any of them — the `during` action, run on each frame no rule holds (`do` steps carry one in
  some stock scripts although it can never run; it is kept). Left out: nothing (`null`).
- `id:` names a step for gotos; `description:` is a note.

A rule's `goto` is a step id, or:

| goto | goes to |
|---|---|
| (not written) | the step after this one; after the last step of a branch, the step after the choice; after the last step of a repeat, the repeat's first step; after the last step of the script, `stop` |
| `stop` | the thread ends |
| `restart` | this step again, tested again in the same frame |
| `next` | the next step of the same list |
| `continue` | the first step of the innermost repeat |
| `break` | the step after the innermost repeat |

Nesting, where the bytecode's layout allows it (the decompiler uses it only where it compiles to the same code):

- `- repeat: [steps]` — steps that start over when the last one is done.
- `steps:` in a `wait` rule — a branch: steps entered when that rule fires, laid out right after the step that holds the
  rule (branches in rule order), and continuing after that step when done.
- `guards:` on a script — rules every step starts with (the stock "if my car is dead, count it and stop" guard).

States are numbered in source order — a step, then the branches of its rules, then the next step — and that order is the
layout; the transitions are worked out from it, so any nesting compiles, and the decompiler picks one that needs few
explicit gotos. State machines whose loops have two entries (some AI behaviours) cannot nest; they keep their gotos, which is
what the original source had too.

Over the 90 scripted mission files of both games (2708 threads), 1692 threads run scripts that are plain step lists (no
repeat, branch or goto by id), 622 nested ones, 394 ones with gotos by id. Guards: 72 scripts of the I76 missions, none in
Nitro.

### Conditions and actions

An action is `operation(arg, ...)`. A condition combines tests with `!`, `&&`, `||` and parentheses: `!` binds tightest,
then `&&`, then `||`; `&&` and `||` group to the right (`a && b && c` is `a && (b && c)`, which is how the bytecode nests
them; `(a && b) && c` is a different tree and is written so). `isEqual(a, b)`, `isGreater(a, b)`, `isLesser(a, b)` may be
written `a == b`, `a > b`, `a < b`; there is no `!=`, `>=` or `<=` (write `!(a == b)`). `true` is the test that always holds.

Arguments compile differently by kind, and the source says which:

| written | is | bytecode |
|---|---|---|
| a field name | the cell a thread bound it to | `argLocal -n` |
| a variable name | the local (a pointer to it) | `argLocalAddr n` |
| `*variable` | the local's content used as the pointer (no stock script does it) | `argLocal n` |
| a number | an immediate | `pushImm v; argLocalAddr t` |
| `actor.KEY`, `path.KEY`, `sound.KEY` | that entry's index, an immediate | as a number |

The decompiler writes a number as a table reference where the operation's argument is an actor, a path or a sound and the
index is in the table; a global or a variable's initial value likewise when every use of it is one kind. That changes
nothing in the bytecode.

`- {call: script, args: [...]}` is a call (fields are forwarded, variables passed by address, numbers pushed). `asm:` in
place of `steps:` gives a script as raw instructions (`exec isDead`, `jump script3+5`, `pushImm 7`), for code that is not in
the state-machine shape; jump and call targets are `script`, `script+N` or `@N`. No stock script needs it.

### Threads, globals and tables

`globals` are the data cells in order, each a number or a table reference. A thread names its script and binds each of the
script's fields to a global (`args: {field: global}`); a list binds by position (for a thread that starts inside a script).
Actors are written with the 16 hex digits of the placement's 8-byte name (`id`); without an `id`, the compiler looks the
`object` name up among the mission's placements and needs exactly one. The path table comes from the mission the source is
compiled into: the source's `paths` must be that mission's labels in order, or be left out (then the mission's are used, keyed as the
decompiler keys them). Labels and sound files are at most 39
characters. `prototypes:` appears only when a mission's operation table is not the stock one; an operation the engine
knows but the table lacks is added to it.

## Names

The decompiler makes generic names: `script12`, `thread7`, `g17` (globals), `f0` (fields), `v0` (variables), `step4` (step
ids, made only where a goto needs one; the number is the step's state index). A **names sidecar** gives better ones, plus
aliases (display names) and descriptions, without changing a byte of the compiled script:

```yaml
mission: T01
fsm: 3fa9c0d2e1b4a6f7          # the FSM chunk these names are for (fsm-decompile --names-template writes it)
threads:
  thread3: {name: sheriff_escort, alias: Sheriff escort, description: drives the sheriff to the barn}
scripts:
  script15:
    name: escort
    alias: Escort along a path
    fields: {f0: car, f1: {name: route, description: the path it drives}}
    variables: {v0: timer}
    steps: {step3: {name: fight, description: fights back until the timer runs out}}
  "@1a2b3c4d5e6f": {name: death_line}   # a routine by its body hash: every mission's copy of a library routine
globals:
  g17: {name: sheriff_dead, description: set when the sheriff dies}
actors:
  taurus: {alias: Taurus, description: the player's partner}   # actors, paths, sounds: alias/description only
sounds:
  gdsgc28: "Sheriff: you're going too fast"
```

A note is a mapping (`name`, `alias`, `description`) or a plain string: the name, except for actors, paths and sounds, where
it is the description. Names must be identifiers and unique where they are used (fields and variables of one script share
one namespace); an unusable name is reported and the generic one kept. Entries keyed by generic names apply only when `fsm`
matches the chunk (or is left out); scripts keyed by `@hash` (the routine's instructions with jumps made relative and
operations named) apply to that routine in any mission. `--names` may be given more than once; later files win.
`fsm-decompile --names-template FILE` writes a sidecar listing every generic name, with the chunk's hash and each script's
body hash, to fill in.

## Commands

```
i76 [--install DIR] fsm-decompile MISSION [--names FILE]... [--out FILE.yaml] [--names-template FILE]
i76 fsm-compile FILE.yaml --into MISSION_FILE --out NEW_FILE
i76 fsm-roundtrip [--install DIR]... [--verbose]
```

`MISSION` is a file or a mission name the install finds. `fsm-compile` rewrites only the FSM chunk of a copy of
`MISSION_FILE` (every other byte stays; it never writes into an install). `fsm-roundtrip` decompiles, writes, reads and
compiles every scripted mission of the installs (default: `I76_INSTALL` and `I76_NITRO_INSTALL`) and reports each.

For editors: the first line of a decompiled file points the YAML language server at the schema
(`# yaml-language-server: $schema=...`).
