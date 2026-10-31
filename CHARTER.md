# Charter

1. **Clean room.** The only sources ported are the project's own
   reverse-engineering tools and notes. Nothing is taken from third-party ports, leaked source or
   the GPL LZO library.
2. **No game files here.** Original executables, assets, extracted media and
   anything derived from them (goldens, traces, decode caches) are never
   committed. Godot's `res://` never holds game data; the install is read at
   runtime. A decode cache, if any, lives in `user://cache/`.
3. **Facts before names.** Fields whose meaning is not established keep
   their offset as their name (`Unk_0x38`). Open questions are recorded in
   the maintainers' private notes, not guessed at here.
4. **Faithful by default.** Behaviour matches the original executable. Each
   intentional difference sits behind an option and is listed in
   [docs/deviations.md](docs/deviations.md).
5. **Measured, not assumed.** Simulation code is accepted when it reproduces
   traces recorded from the real game, not when it looks right.
