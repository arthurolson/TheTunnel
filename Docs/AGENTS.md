# The Tunnel: agents working in parallel

Three agents, one per area of [TODO.md](TODO.md), each owning its own files. The definitions are in
`.claude/agents/`, and the rules every agent follows are in the project's [CLAUDE.md](../CLAUDE.md).

| Agent | Area | Owns |
|---|---|---|
| `painting` | Painting: tracking and sensory feedback | The palette and marker, paint on the tunnel and the whiteboard, the real-marker tracker, the Paint builder and paint shaders |
| `movie` | 3D movies and scenarios in containers | Containers, the 3D movie and its panel, binding sites and antibodies, the molecule and virion builders |
| `viewer-browser` | Viewer and browser | The Viewer panel, its media and controls, the browser panels, keyboards and clipboard, the Viewer builder and shader |

## The one constraint: a single Unity Editor

There is one Unity Editor for this project, and compiling, testing in the Editor and building all happen there.
So agents can write code in parallel, but only one at a time can be compiling and testing in Unity:

- **Unity lane (one agent at a time):** works in the main checkout; compiles, tests in the Editor and runs its
  area's builder.
- **Drafting lane (any number):** works in its own git worktree on its own branch; reads, designs and writes code,
  without Unity. The main session then merges the branch, compiles and tests it.
- **The main session** does every build, version stamp, commit to main, install and store upload, one build at a
  time.

Areas run fully in parallel once each has its detailed request. When two requests need the Editor at once, one
drafts while the other tests.

## Files no single agent owns

These are touched by more than one area, so changes to them go through the main session:
- The TS Menu and its layout (TunnelMenu, TunnelMenuLayout).
- The tunnel segment prefab (TG_TunnelSegment), the prefab registry (TunnelPrefabs.asset) and the network prefab
  list.
- The scene (SampleScene).
- The passthrough camera reader, shared by the real marker and the floor QR co-location (PassthroughCameraReader,
  QrFloorScanner, CoLocation).
- The tunnel geometry (TunnelSegmentGeometry, used by painting and by mixed reality boxes).
- Project Settings, packages and the build.
