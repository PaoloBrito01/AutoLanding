# AutoLanding — Spaceflight Simulator Mod

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![Version](https://img.shields.io/badge/version-3.1.0-blue.svg)](../../releases)
[![BepInEx](https://img.shields.io/badge/BepInEx-5.4.x-green.svg)](https://github.com/BepInEx/BepInEx/releases)

Automatic rocket landing autopilot for **Spaceflight Simulator** (Steam/PC).  
Press **F8** to toggle. Works on any celestial body.

---

## Features

- 3-phase state machine: Coast → Braking → Final approach
- PID velocity control with feedforward hover compensation
- Reads local gravity in real time — works on the Moon, Mars, or custom planets
- Corrects horizontal drift automatically
- Emergency governor prevents suicide burns at high speed
- Draggable HUD overlay with real-time telemetry
- Compatible with any rocket with TWR ≥ ~1.1

## Requirements

- [BepInEx 5.4.x](https://github.com/BepInEx/BepInEx/releases) installed in your SFS game folder
- Spaceflight Simulator (Steam, PC)

## Installation

1. Install **BepInEx 5.4.x** if you haven't already — follow the [BepInEx installation guide](https://docs.bepinex.dev/articles/user_guide/installation/index.html)
2. Download `AutoLanding.zip` from the [Releases](../../releases) page and extract it
3. Copy `AutoLanding.dll` to:
   ```
   <SFS game folder>\BepInEx\plugins\AutoLanding.dll
   ```
4. Launch the game — BepInEx will load the mod automatically

## Usage

| Key | Action |
|-----|--------|
| **F8** | Toggle autopilot on / off |

Once activated, a **HUD window** appears in the top-left corner showing:

| Field | Description |
|-------|-------------|
| Estado | Current phase: Coast / Braking / Final / Landed |
| Altitude | Height above terrain (m) |
| VSpeed | Vertical speed (m/s, negative = descending) |
| HSpeed | Horizontal speed (m/s) |
| Throttle | Current throttle % |
| BurnAlt | Calculated braking altitude (m) |
| VProfile | Maximum safe speed at current altitude (m/s) |
| TWR | Thrust-to-weight ratio |

**Tips:**
- Activate during a suborbital arc with engines off for best results
- Works best with TWR ≥ 1.2; lower TWR increases braking distance
- The rocket must be pointing roughly upward on activation

## How it works

The autopilot runs a three-phase algorithm:

| Phase | Trigger | Behavior |
|-------|---------|----------|
| **Coast** | `h > burnAlt × 1.35` | Engines off; attitude controller keeps rocket vertical |
| **Braking** | `h ≤ burnAlt × 1.35` | Main deceleration burn; velocity target = `min(0.85 × vProfile, 0.32 × √(2gh))` |
| **Final** | `h < 25 m` | Smooth linear ramp from braking speed down to touchdown at 0.35 m/s |

An **emergency governor** overrides to full throttle if the burn margin drops below 85% or current speed reaches 90% of the safe velocity profile.

## Building from Source

1. Clone the repo
2. Copy `Directory.Build.props.example` → `Directory.Build.props`
3. Edit `Directory.Build.props` — set `<SFSGamePath>` to your SFS game folder
4. Build and deploy:
   ```powershell
   .\deploy.ps1
   ```
   Or just build without deploying:
   ```
   dotnet build -c Release AutoLanding
   ```

## License

[MIT](LICENSE)

<img width="800" height="450" alt="Spaceflight Simulator - 2026-05-24 23-45-58 (1)" src="https://github.com/user-attachments/assets/b65006a0-5da9-4858-a13a-03f76e7114d5" />



