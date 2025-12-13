# Copilot Instructions for Colocation VR Project

## Project Overview
- **Purpose:** Multi-user VR colocation using spatial anchors, networked object spawning, and real-time alignment. Built in Unity, using Photon Fusion for networking and Oculus/MetaXR for spatial anchors.
- **Key Directories:**
  - `Assets/Colocation/Scripts/`: Core colocation, alignment, and networked object logic
  - `Assets/Photon/`: Photon Fusion networking libraries and integration
  - `Assets/MetaXR/`, `Assets/Oculus/`: Platform-specific SDKs

## Architecture & Patterns
- **Colocation Flow:**
  - `ColocationManager` manages session advertisement/discovery and anchor sharing/alignment.
  - `AlignmentManager` aligns the user's camera rig to a shared anchor.
  - `AnchorAutoGUIManager` provides UI for auto-alignment and cube spawning, inherits from `ColocationManager`.
  - `CubeSpawner` spawns networked cubes using Photon Fusion RPCs; only the host has state authority to spawn.
  - `NetworkedCube` synchronizes grab state and authority transfer across clients.
- **Networking:**
  - Uses Photon Fusion (`FUSION2` define) for all networked interactions.
  - All networked objects must have a `NetworkObject` component.
  - Authority checks: Only the state authority (host) can spawn or change networked objects; others use RPCs to request actions.
- **Spatial Anchors:**
  - Uses Oculus/MetaXR APIs for anchor creation, sharing, and localization.
  - Anchors are shared via group UUIDs and used to align all users in the same space.

## Developer Workflows
- **Build:**
  - Use Unity Editor (no custom build scripts detected).
  - Ensure `FUSION2` scripting define is set for networking features.
- **Run/Debug:**
  - Start in Editor or build to device (Oculus/Meta).
  - Use the in-game UI (`AnchorAutoGUIManager`) to start sessions, align, and spawn cubes.
- **Testing:**
  - No automated tests found; manual testing via multi-device sessions is standard.

## Project Conventions
- **RPCs:**
  - Use `[Rpc(RpcSources.All, RpcTargets.StateAuthority)]` for all client-to-host requests.
  - Methods like `RPC_RequestSpawnCube`, `RPC_RequestGrab` are always private and only called via public wrappers.
- **Component Discovery:**
  - Use `FindObjectOfType` or serialized fields for references (e.g., `AlignmentManager`, `CubeSpawner`).
- **Logging:**
  - Use `Debug.Log` with clear tags (e.g., `[CubeSpawner]`, `[NetworkedCube]`).
- **Prefabs:**
  - Networked prefabs must be registered with Photon Fusion and referenced via `NetworkPrefabRef`.

## Integration Points
- **Photon Fusion:**
  - All networked actions and object spawns go through Fusion's `Runner` and RPC system.
- **Oculus/MetaXR:**
  - Anchor creation, sharing, and localization use `OVRSpatialAnchor` and `OVRColocationSession` APIs.
- **UI:**
  - Main user actions are exposed via Unity UI buttons in `AnchorAutoGUIManager`.

## Examples
- To spawn a cube: `CubeSpawner.SpawnCube()` (calls RPC if not host)
- To align to anchor: `AlignmentManager.AlignUserToAnchor(anchor)`

---
For new features, follow the authority/RPC pattern and keep all networked state changes on the host. Reference and extend the managers in `Assets/Colocation/Scripts/` for core logic.
