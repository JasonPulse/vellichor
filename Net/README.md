# Net/ — bridge to the shared protocol stack

Owns the live server session and exposes `WorldState` to `../Render/`.

Does **not** re-implement the protocol. It references the shared `XiProtocol` class
library (extracted from the headless client — see `../docs/DESIGN.md` → "Integration")
and adapts its `WorldState` + capabilities to the renderer. Input (movement, actions)
flows back out through the same packet builders the headless client already uses.

Not needed for M0/M1 rendering work — those run standalone against the local corpus.
