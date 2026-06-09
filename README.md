# AutoLanding — Spaceflight Simulator Mod

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
2. Download `AutoLanding.dll` from the [Releases](../../releases) page
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
| Estado | Current phase: Coast / Freada / Final / Landed |
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

---

## Português

**Pouso automático para Spaceflight Simulator.** Pressione **F8** para ativar/desativar.  
Após ativar, o autopiloto assume o controle dos motores e pousa o foguete com velocidade de toque ≤ 0.35 m/s.  
Funciona em qualquer planeta (Lua, Marte, planetas customizados) — lê a gravidade local em tempo real.

**Instalação:** Instale BepInEx 5.4.x → baixe `AutoLanding.dll` da aba [Releases](../../releases) → copie para `BepInEx\plugins\`.
