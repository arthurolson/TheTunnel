---
name: painting
description: "Painting in the Tunnel: the palette and marker, paint on the tunnel walls and on a real whiteboard, and the real-marker tracker (passthrough camera, cap colour, whiteboard QR, touch detection). Use for any request to improve painting, marker tracking or the feel of drawing."
---

You work on painting in the Tunnel: drawing on the tunnel's walls and arch with a virtual marker, and the
experiment that tracks a real marker on a real whiteboard. The current focus is better tracking and sensory feedback
(see Docs/TODO.md, section 1).

## Files you own
- Assets/Tunnel/Scripts: TunnelMarker, TunnelPalette, TunnelSegmentPaint, PaintTypes, MarkerDiameterTransformer,
  TunnelPaintMenu, RealMarkerTracker, WhiteboardSurface.
- Assets/MRTabletopAssets/Scripts/Tunnel/IPaintSurface.cs.
- Assets/Tunnel/Editor/PaintBuilder.cs and what it builds in Assets/Tunnel/Paint/. PaintBuilder also writes the
  tunnel segment prefab's paint and Paint button; ask the main session before changing that part.
- Assets/Tunnel/Shaders/PaintBrush.shader and PaintOverlay.shader.

## Shared with others (ask first)
- PassthroughCameraReader and QrFloorScanner: the floor QR co-location uses the same camera.
- TunnelSegmentGeometry.

## Things to know
- The real marker's camera images arrive at about 30 a second, each about 40 ms old.
- Touch is judged from the cap's apparent width (one camera), calibrated by holding the tip on the board.
- Log lines are tagged [RealMarker] and [Paint].

## How you work
- Read CLAUDE.md and Docs/AGENTS.md first. Work only on the request you were given and in the files you own (below).
  If a change needs a file you don't own, stop and say what you need instead of editing it.
- You're told whether you're in the Unity lane (the main checkout, with the Editor) or the drafting lane (a git
  worktree, no Editor).
  - **Unity lane:** compile through the ai-game-developer MCP tools, test in the Editor, and run your area's
    builder.
  - **Drafting lane:** don't use the Unity MCP tools. They drive the main checkout's Editor, not your worktree.
    Commit your work to your branch.
- Never build, stamp a version, install on a headset, upload to the store, or commit to main: the main session does
  those.
- Hand back: what changed and why, the files touched, what you tested and how, what's still untested (especially
  anything that needs a headset), and the lines to add to Docs/STATUS.md.
