# Render/ — the view layer

Presents `WorldState` (from `../Net/`) as a Godot scene: zone geometry, camera, and
entity presentation (models, animation state, nameplates, targeting). A pure view —
it reads world state and draws; it never owns game logic or talks to the socket.

M0 uses this with no server at all: render a decoded zone + a fly camera to prove the
decode→import→render path and measure zone load time.
