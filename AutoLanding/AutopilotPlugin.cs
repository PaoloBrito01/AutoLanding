using BepInEx;
using BepInEx.Logging;
using SFS.Parts.Modules;
using SFS.World;
using SFS.WorldBase;
using System;
using System.Linq;
using UnityEngine;

namespace AutoLanding
{
    internal sealed class PidController
    {
        public float Kp, Ki, Kd;
        public float MaxIntegral = 20f;

        private float _integral;
        private float _prevError;
        private bool _fresh = true;

        public void Reset()
        {
            _integral = 0f;
            _prevError = 0f;
            _fresh = true;
        }

        public float Update(float error, float dt)
        {
            float newIntegral = Mathf.Clamp(
                _integral + error * dt,
                -MaxIntegral,
                MaxIntegral);

            _integral = newIntegral;

            float deriv = _fresh
                ? 0f
                : (error - _prevError) / Mathf.Max(dt, 0.001f);

            _fresh = false;
            _prevError = error;

            return Kp * error + Ki * _integral + Kd * deriv;
        }
    }

    internal sealed class AngularController
    {
        private const float WN = 3.0f;
        private const float ZETA = 1.0f;
        private const float MAX_TORQUE_RATIO = 80f;

        private static float Kp => WN * WN;
        private static float Kd => 2f * ZETA * WN;

        // surfaceNormalDeg : ângulo world-space para apontar perpendicularmente à superfície.
        // signedHVel       : velocidade horizontal assinada no ref. de superfície (leste = +).
        //                    Positivo = inclina CW (empurra de volta contra o leste).
        public void Apply(Rigidbody2D rb2d, float surfaceNormalDeg, float signedHVel)
        {
            float inertia = Mathf.Max(rb2d.inertia, 0.001f);

            // 1 grau por m/s, limitado a ±8°. Sinal negativo porque:
            // hVel > 0 (leste) → inclina CW → componente de empuxo oposta ao leste.
            float lateralTilt = Mathf.Clamp(-signedHVel * 1.0f, -8f, 8f);
            float targetAngle = NormalizeDeg(surfaceNormalDeg + lateralTilt);
            float currentAngle = NormalizeDeg(rb2d.rotation);
            float angleError = NormalizeDeg(currentAngle - targetAngle);

            float angleRad = angleError * Mathf.Deg2Rad;
            float angVelRad = rb2d.angularVelocity * Mathf.Deg2Rad;

            float torque = -(Kp * angleRad + Kd * angVelRad) * inertia;

            float maxTorque = rb2d.mass * MAX_TORQUE_RATIO;

            rb2d.AddTorque(Mathf.Clamp(torque, -maxTorque, maxTorque));
        }

        public static float AngleDeg(Rigidbody2D rb2d)
            => NormalizeDeg(rb2d.rotation);

        private static float NormalizeDeg(float deg)
        {
            deg %= 360f;

            if (deg > 180f)
                deg -= 360f;

            if (deg < -180f)
                deg += 360f;

            return deg;
        }
    }

    [BepInPlugin("com.sfsmod.autolanding", "Auto Landing", "3.1.0")]
    public class AutoLandingPlugin : BaseUnityPlugin
    {
        public static ManualLogSource Log;

        private AutopilotBehaviour _autopilot;

        private void Awake()
        {
            Log = Logger;
            Logger.LogInfo("AutoLanding v3.1 — F8 = ativar/desativar");
        }

        private void Update()
        {
            if (!Input.GetKeyDown(KeyCode.F8))
                return;

            if (_autopilot == null)
            {
                _autopilot = gameObject.AddComponent<AutopilotBehaviour>();
                Logger.LogInfo("Autopilot ATIVADO");
            }
            else
            {
                Destroy(_autopilot);
                _autopilot = null;
                Logger.LogInfo("Autopilot DESATIVADO");
            }
        }
    }

    public class AutopilotBehaviour : MonoBehaviour
    {
        private const float FINAL_ALT = 25f;

        private const float LAND_ALT = 0.3f;

        private const float V_TOUCH = 0.35f;

        private const double SAFETY_FACTOR = 1.35;

        private const double EMERGENCY_ALT        = 200.0;
        private const double EMERGENCY_VD         = 30.0;
        private const double EMERGENCY_BURN_RATIO = 0.85;

        private readonly PidController _velPid = new PidController
        {
            Kp = 0.25f,
            Ki = 0.0f,
            Kd = 0.0f,
            MaxIntegral = 15f,
        };

        private readonly AngularController _angCtrl = new AngularController();

        private const float THR_RISE = 12f;
        private const float THR_FALL = 6f;

        private enum State
        {
            Coast,
            Freada,
            Final,
            Landed
        }

        private State _state = State.Coast;

        private float _throttle = 0f;
        private double _vProfile = 0;
        private double _burnAlt = 0;
        private float _twr = 0f;
        private double _hVel = 0;

        private Rect _windowRect = new Rect(10, 120, 320, 210);

        private void OnGUI()
        {
            if (GetRocket() == null)
                return;

            _windowRect = GUI.Window(
                98765,
                _windowRect,
                DrawHUD,
                "Auto Landing v3.1");
        }

        private void DrawHUD(int id)
        {
            var rocket = GetRocket();

            if (rocket == null)
                return;

            var loc = rocket.location.Value;

            double h = Math.Max(0, loc.GetTerrainHeight(true));
            double vSpd = loc.VerticalVelocity;

            int y = 20;

            void L(string s)
            {
                GUI.Label(new Rect(8, y, 300, 20), s);
                y += 20;
            }

            L($"Estado: {_state}");
            L($"Altitude: {h:F1} m");
            L($"VSpeed: {vSpd:F1} m/s");
            L($"HSpeed: {_hVel:F1} m/s");
            L($"Throttle: {_throttle * 100f:F0}%");
            L($"BurnAlt: {_burnAlt:F1} m");
            L($"VProfile: {_vProfile:F1} m/s");
            L($"TWR: {_twr:F2}");
            L($"G: {loc.planet.GetGravity(loc.Radius):F2} m/s²");

            GUI.DragWindow(new Rect(0, 0, 320, 20));
        }

        private void FixedUpdate()
        {
            var rocket = GetRocket();

            if (rocket == null || rocket.rb2d == null)
            {
                ResetAll();
                return;
            }

            if (_state == State.Landed)
                return;

            float dt = Time.fixedDeltaTime;

            var loc = rocket.location.Value;

            // Gravidade real do planeta na altitude atual (funciona na Lua, Marte, planetas customizados).
            float localG = (float)loc.planet.GetGravity(loc.Radius);

            double h = Math.Max(0, loc.GetTerrainHeight(true));
            double vd = -loc.VerticalVelocity;

            // Velocidade horizontal assinada no referencial de superfície.
            // posAngle: ângulo do vetor posição (centro do planeta → foguete).
            // A direção "leste" (CCW) é perpendicular ao vetor radial.
            double posAngle = loc.position.AngleRadians;
            _hVel = -loc.velocity.x * Math.Sin(posAngle)
                  +  loc.velocity.y * Math.Cos(posAngle);

            // Ângulo que o foguete deve ter (world-space) para apontar radialmente.
            float surfaceNormalDeg = (float)(posAngle * 180.0 / Math.PI) - 90f;

            var engines = rocket.partHolder.GetModules<EngineModule>();

            float maxThrust = engines.Sum(e => e.thrust.Value);
            float mass = rocket.rb2d.mass;

            if (mass <= 0 || maxThrust <= 0)
            {
                CutEngines(rocket);
                return;
            }

            // Inclinação real em relação à normal de superfície (não ao eixo global).
            // rb2d.rotation é absoluto (world-space); subtraímos a normal para obter
            // o ângulo de tilt real, independente de onde o foguete está no planeta.
            float worldAngle = AngularController.AngleDeg(rocket.rb2d);
            float tiltDeg    = worldAngle - surfaceNormalDeg;
            tiltDeg = ((tiltDeg + 180f) % 360f + 360f) % 360f - 180f;
            float angleRad   = Mathf.Abs(tiltDeg) * Mathf.Deg2Rad;

            float cosAngle = Mathf.Max(Mathf.Cos(angleRad), 0.5f);

            float maxAccel = maxThrust * localG / mass;
            float effectiveAccel = maxAccel * cosAngle;
            float netDecel = effectiveAccel - localG;

            _twr = maxAccel / localG;

            if (netDecel <= 0)
            {
                CutEngines(rocket);
                return;
            }

            // ==========================
            // DETECÇÃO DE POUSO
            // ==========================
            if (h < LAND_ALT && Math.Abs(vd) < 0.5)
            {
                _state = State.Landed;
                CutEngines(rocket);
                ResetAll();
                return;
            }

            // ==========================
            // PERFIL PRINCIPAL
            // ==========================
            _burnAlt = Math.Max(
                0,
                (vd * vd - V_TOUCH * V_TOUCH) / (2.0 * netDecel));

            _vProfile = Math.Sqrt(
                2.0 * netDecel * h + V_TOUCH * V_TOUCH);

            if (_state == State.Coast && h <= _burnAlt * SAFETY_FACTOR)
            {
                _state = State.Freada;
            }

            if (_state == State.Freada && h < FINAL_ALT)
            {
                _state = State.Final;
                _velPid.Reset();
            }

            switch (_state)
            {
                case State.Coast:
                    RunCoast(rocket, dt);
                    break;

                case State.Freada:
                    RunFreada(
                        rocket,
                        engines,
                        effectiveAccel,
                        vd,
                        h,
                        dt,
                        localG);
                    break;

                case State.Final:
                    RunFinal(
                        rocket,
                        engines,
                        effectiveAccel,
                        vd,
                        h,
                        dt,
                        localG);
                    break;
            }

            // ==========================
            // GOVERNADOR DE EMERGÊNCIA
            // ==========================
            // Dois critérios independentes (OR):
            //   1. burnAlt ≥ 85% de h: foguete consumiu quase todo o espaço de frenagem
            //      → dispara antes de 30 m/s para foguetes de baixo TWR (ex: TWR=1.2 a 73m/16ms → burnAlt/h=0.92)
            //   2. Limite absoluto: abaixo de 200m ainda acima de 30 m/s
            bool nearLimit   = _burnAlt > 0 && h > 1 && (_burnAlt / h) >= EMERGENCY_BURN_RATIO;
            bool absOverspeed = h < EMERGENCY_ALT && vd > EMERGENCY_VD;
            bool nearSuicide  = _vProfile > 0 && vd >= _vProfile * 0.90;

            if (_state != State.Coast && (nearLimit || absOverspeed || nearSuicide))
            {
                SmoothThrottle(1f, dt);
                ApplyThrottle(rocket, engines);
            }

            // ==========================
            // CONTROLE ANGULAR
            // ==========================
            _angCtrl.Apply(rocket.rb2d, surfaceNormalDeg, (float)_hVel);
        }

        private void RunCoast(Rocket rocket, float dt)
        {
            SmoothThrottle(0f, dt);

            if (_throttle < 0.01f)
            {
                CutEngines(rocket);
            }
            else
            {
                rocket.throttle.throttleOn.Value = true;
                rocket.throttle.throttlePercent.Value = _throttle;
            }
        }

        private void RunFreada(
            Rocket rocket,
            EngineModule[] engines,
            float effectiveAccel,
            double vd,
            double h,
            float dt,
            float localG)
        {
            double vSuicide  = _vProfile * 0.85;
            double vFreefall = Math.Sqrt(2.0 * localG * h) * 0.32;
            double targetSpeed = Math.Max((double)V_TOUCH, Math.Min(vSuicide, vFreefall));

            float speedErr = (float)(vd - targetSpeed);

            float hoverThrottle = localG / effectiveAccel;

            // PID roda sempre (sem reset ao ultrapassar alvo).
            // Clamp >= 0: só adiciona throttle acima do hover, nunca subtrai.
            // Evita os picos do reset discreto que causavam throttle piscando.
            float pid = Mathf.Max(0f, _velPid.Update(speedErr, dt));

            float targetThrottle = hoverThrottle + pid;

            // emergência
            if (h < FINAL_ALT * 3 && vd > targetSpeed * 1.3)
            {
                targetThrottle = 1f;
            }

            targetThrottle = Mathf.Clamp(targetThrottle, 0f, 1f);

            SmoothThrottle(targetThrottle, dt);

            ApplyThrottle(rocket, engines);
        }

        // ========================================
        // FINAL LANDING PHASE
        // ========================================
        private void RunFinal(
            Rocket rocket,
            EngineModule[] engines,
            float effectiveAccel,
            double vd,
            double h,
            float dt,
            float localG)
        {
            // Rampa linear contínua de vFreefall(FINAL_ALT) até V_TOUCH.
            // Sem degraus: o erro de velocidade varia suavemente com a altitude.
            // Usa localG → adapta-se à Lua, Marte etc.
            double vTop = 0.32 * Math.Sqrt(2.0 * localG * FINAL_ALT);
            double targetSpeed = Math.Max(V_TOUCH, V_TOUCH + (h / FINAL_ALT) * (vTop - V_TOUCH));

            float error = (float)(vd - targetSpeed);

            float hoverThrottle = localG / effectiveAccel;

            float pid = _velPid.Update(error, dt);

            // Permite PID levemente negativo para que o foguete acelere suavemente
            // se freou demais (sem cair em queda livre). Floor: no máximo 10% abaixo do hover.
            float floor = Mathf.Max(0.10f, hoverThrottle - 0.10f);
            float targetThrottle = Mathf.Clamp(hoverThrottle + pid, floor, 1f);

            if (h < 2.5 && vd > 1.2) targetThrottle = 1f;
            if (h < 0.8 && vd > 0.6) targetThrottle = 1f;

            SmoothThrottle(targetThrottle, dt);
            ApplyThrottle(rocket, engines);
        }

        private void SmoothThrottle(float target, float dt)
        {
            float rate = target > _throttle
                ? THR_RISE
                : THR_FALL;

            _throttle = Mathf.MoveTowards(
                _throttle,
                target,
                rate * dt);
        }

        private void ApplyThrottle(
            Rocket rocket,
            EngineModule[] engines)
        {
            foreach (var e in engines)
                e.engineOn.Value = true;

            rocket.throttle.throttleOn.Value = true;
            rocket.throttle.throttlePercent.Value = _throttle;
        }

        private static void CutEngines(Rocket rocket)
        {
            rocket.throttle.throttleOn.Value = false;
            rocket.throttle.throttlePercent.Value = 0f;
        }

        private void ResetAll()
        {
            _state = State.Coast;
            _throttle = 0f;
            _burnAlt = 0;
            _vProfile = 0;
            _twr = 0f;
            _hVel = 0;
            _velPid.Reset();
        }

        private static Rocket GetRocket()
            => PlayerController.main?.player?.Value as Rocket;
    }
}
