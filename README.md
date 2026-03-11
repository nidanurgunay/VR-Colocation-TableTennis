# XR Colocation Table Tennis

A multiplayer AR/VR table tennis game for **Meta Quest** headsets that uses **colocation** — two players wearing headsets in the same physical room see the same virtual table at the same physical position, without any external tracking setup.

Built as a research/prototype project at the University of Konstanz.

**Demo:** [Watch on YouTube](https://youtu.be/ll0tqw7C1IY)

---

## How It Works

### Colocation via Shared Spatial Anchors
Each player places one spatial anchor (via grip trigger) on their side of the real table. The host shares both anchors via `OVRColocationSession`. The client loads and localizes to those anchors, aligning its world space to the host's — no external trackers needed. The table is then spawned at the midpoint between the two anchors.

### Networking (Photon Fusion)
- Uses **Photon Fusion 1.1.0** in Host mode
- Ball physics run exclusively on the host (`[Networked]` position synced to client)
- Racket hits are sent as RPCs from client → host, which applies the velocity and resyncs
- Table position/rotation adjustments synced via explicit RPCs

### Passthrough AR
- `OVRPassthroughLayer` with a transparent camera background
- Players see the real room through the headset with the virtual table overlaid

### Controller Racket
- Pressing **B** (right hand) or **Y** (left hand) swaps the controller visual for a racket model
- Racket velocity is tracked manually each frame since kinematic rigidbodies report zero velocity to Unity's physics engine

---

## Tech Stack

| Component | Version |
|-----------|---------|
| Unity | 2022.3.62f1 |
| Render Pipeline | URP 14.0.12 |
| Meta XR SDK Core | 71.0.0 |
| Meta XR SDK Interaction (OVR) | 81.0.0 |
| Meta XR SDK Platform | 81.0.0 |
| Photon Fusion | 1.1.0 |
| Target Platform | Android (Meta Quest 2 / Quest 3) |

---

## Project Structure

```
Assets/Colocation/
├── Scenes/
│   ├── Colocation.unity            # Main entry scene
│   └── Demo2/Demo2_cube.unity      # Main multiplayer scene
└── Scripts/
    ├── ColocationManager.cs                 # Base class: anchor creation & sharing
    ├── AnchorGUIManager_AutoAlignment.cs    # UI wizard for colocation setup
    ├── PassthroughGameManager.cs            # AR game logic (table, ball, phases)
    └── TableTennis/
        ├── ControllerRacket.cs   # Attaches racket model to controller
        ├── NetworkedBall.cs      # Host-authoritative ball with Fusion sync
        └── GamePhaseDefinitions.cs
```

---

## Setup

### Requirements
- Two Meta Quest 2 or Quest 3 headsets on the **same Wi-Fi network**
- Unity 2022.3 LTS
- Meta XR SDK (via Unity Package Manager or Meta XR All-in-One package)
- Photon Fusion 1.1.0 SDK

### Steps

1. **Clone the repository**
   ```bash
   git clone https://github.com/nidanurgunay/Colocation-Table-Tennis.git
   ```

2. **Open in Unity 2022.3**

3. **Set your Photon App ID**
   - Create a free account at [photonengine.com](https://www.photonengine.com)
   - Create a new Fusion app and copy the App ID
   - In Unity: open `Assets/Photon/Fusion/Resources/PhotonAppSettings.asset` and paste your App ID

4. **Configure Meta XR**
   - Go to `Edit > Project Settings > Meta XR` and follow the setup wizard

5. **Build to both headsets**
   - `File > Build Settings > Android → Build And Run`

### Playing

1. Both headsets launch the app; one host, one joins
2. Use the on-screen wizard to place and share spatial anchors
3. Once both headsets are aligned, tap **Start AR Game**
4. Adjust the table with thumbsticks, confirm with **A/X**
5. Press **B** or **Y** to equip a racket, then hit the ball!

---

## Known Limitations

- Drift can accumulate over long sessions as SLAM tracking estimates diverge — periodic re-alignment helps but does not fully eliminate it
- Ball physics are host-authoritative; high network latency causes visible lag for the client
- The 16KB page-size alignment warnings on Android 15+ are in third-party SDK binaries (Photon/Meta) and require upstream fixes

---

## License

MIT License — see [LICENSE](LICENSE) for details.
