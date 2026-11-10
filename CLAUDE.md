# The Tunnel

A shared mixed reality space for molecular biology on Meta Quest 3 and 3S, built in Unity 6000.6.7f1 (URP, OpenXR,
XR Interaction Toolkit 3, Netcode for GameObjects, Vivox). What's done is in Docs/STATUS.md and the plan in
Docs/TODO.md; how work is split between agents is in Docs/AGENTS.md.

## Where things are
- Assets/Tunnel/Scripts: runtime code (no assembly definition: Assembly-CSharp).
- Assets/Tunnel/Editor: builders under the Unity menu Tunnel > ..., which regenerate prefabs. Change the builder,
  then run it, rather than editing a generated prefab by hand.
- Assets/MRTabletopAssets/Scripts/Tunnel: tunnel geometry and panels, in the MRTTT assembly (namespace
  UnityEngine.XR.Templates.MRTTabletopAssets). Assembly-CSharp can use it, but it can't use Assembly-CSharp.
- Assets/Molecules: molecule models and their toys. Assets/StreamingAssets/Viewer: Viewer media (kept out of git).

## How code is written here
- Doc comments in plain language that say what the thing does for the person in the headset.
- Everything shared over the network also works locally when not connected (check IsSpawned).
- Debug.Log lines carry a tag for their area, such as [Paint], [RealMarker], [QR] or [Movie], so headset logs can be
  filtered with adb logcat.
- Unity is driven through the ai-game-developer MCP tools (script-execute, rql-query). Only one agent at a time uses
  the Editor; see Docs/AGENTS.md.

## Builds, versions and commits (the main session only)
- A build goes to Builds/TheTunnelDev.apk, signed with Keystore/thetunnel.keystore, alias thetunnel; the person at
  the Editor enters the passwords. The version (DD.MM.YYYY.N) is stamped automatically at build time.
- Before a build: delete Builds/TheTunnelDev_BackUpThisFolder_ButDontShipItWithYourGame, and make sure no open scene
  has unsaved changes.
- Start a build from a one-shot EditorApplication.update handler, never EditorApplication.delayCall, and wait for it
  by watching Logs/Editor.log.
- After a build, check the APK's signing certificate SHA-256 is
  0cedf7bcd59fbd1e71e1584a0f9e7cb6ea1177f8183c2e96998deb024e617286 before installing or uploading it.
- Commit messages say what changed for the user and end with the version. From Project Settings, commit only the
  bundleVersion and AndroidBundleVersionCode lines; its other local changes stay uncommitted.
