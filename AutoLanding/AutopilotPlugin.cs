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
        public float Kp;
        public float Ki;
        public float Kd;
        public float MaxIntegral = 20f;

        private float _integral;
        private float _prevError;
        private bool _firstUpdate = true;

        public void Reset()
        {
            _integral = 0f;
            _prevError = 0f;
            _firstUpdate = true;
        }

        public float Update(float error, float dt)
        {
            float newIntegral = Mathf.Clamp(
                _integral + error * dt,
                -MaxIntegral,
                MaxIntegral);

            _integral = newIntegral;

            float deriv = _firstUpdate
                ? 0f
                : (error - _prevError) / Mathf.Max(dt, 0.001f);

            _firstUpdate = false;
            _prevError = error;

            return Kp * error + Ki * _integral + Kd * deriv;
        }
    }

    internal sealed class AngularController
    {
        private const float Wn = 3.0f;
        private const float Zeta = 1.0f;
        private const float MaxTorqueRatio = 80f;
        private const float MaxTiltDeg = 8f;

        private static float Kp => Wn * Wn;
        private static float Kd => 2f * Zeta * Wn;

        // signedHVel: signed horizontal speed in surface frame (east = +).
        // Positive = tilt CW to push thrust against eastward drift.
        public void Apply(Rigidbody2D rb2d, float surfaceNormalDeg, float signedHVel)
        {
            float inertia = Mathf.Max(rb2d.inertia, 0.001f);

            // 1 deg per m/s of drift, clamped to ±MaxTiltDeg.
            // Negative sign: hVel > 0 (east) → tilt CW → thrust opposes east.
            float lateralTilt = Mathf.Clamp(-signedHVel, -MaxTiltDeg, MaxTiltDeg);
            float targetAngle = NormalizeDeg(surfaceNormalDeg + lateralTilt);
            float currentAngle = NormalizeDeg(rb2d.rotation);
            float angleError = NormalizeDeg(currentAngle - targetAngle);

            float angleRad = angleError * Mathf.Deg2Rad;
            float angVelRad = rb2d.angularVelocity * Mathf.Deg2Rad;

            float torque = -(Kp * angleRad + Kd * angVelRad) * inertia;
            float maxTorque = rb2d.mass * MaxTorqueRatio;

            rb2d.AddTorque(Mathf.Clamp(torque, -maxTorque, maxTorque));
        }

        public static float AngleDeg(Rigidbody2D rb2d)
            => NormalizeDeg(rb2d.rotation);

        private static float NormalizeDeg(float deg)
            => ((deg + 180f) % 360f + 360f) % 360f - 180f;
    }

    [BepInPlugin("com.sfsmod.autolanding", "Auto Landing", "3.1.0")]
    public class AutoLandingPlugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log = null!;

        private AutopilotBehaviour? _autopilot;

        private void Awake()
        {
            Log = Logger;
            Logger.LogInfo("AutoLanding v3.1 loaded — press F8 to toggle");
        }

        private void Update()
        {
            if (!Input.GetKeyDown(KeyCode.F8))
                return;

            if (_autopilot == null)
            {
                _autopilot = gameObject.AddComponent<AutopilotBehaviour>();
                Logger.LogInfo("Autopilot enabled");
            }
            else
            {
                Destroy(_autopilot);
                _autopilot = null;
                Logger.LogInfo("Autopilot disabled");
            }
        }
    }

    internal class AutopilotBehaviour : MonoBehaviour
    {
        // Phase thresholds
        private const float FinalAlt = 25f;
        private const float VTouch = 0.35f;
        private const double SafetyFactor = 1.35;

        // Emergency governor
        private const double EmergencyAlt = 200.0;
        private const double EmergencyVd = 30.0;
        private const double EmergencyBurnRatio = 0.85;
        private const double NearSuicideThreshold = 0.90;
        private const double MinAltitudeForBurnRatio = 1.0;

        // Braking phase targets
        private const double BrakingProfileMargin = 0.85;
        private const double FreefallFraction = 0.32;
        private const double BrakingEmergencyFactor = 1.3;

        // Final phase
        private const float ThrottleFloorMargin = 0.10f;
        private const float FinalFlareAlt1 = 2.5f;
        private const float FinalFlareVd1 = 1.2f;
        private const float FinalFlareAlt2 = 0.8f;
        private const float FinalFlareVd2 = 0.6f;

        // Misc
        private const float MinCosAngle = 0.5f;
        private const float ThrottleCutThreshold = 0.01f;
        private const float ThrRise = 12f;
        private const float ThrFall = 6f;

        private readonly PidController _velocityPid = new PidController
        {
            Kp = 0.25f,
            Ki = 0.0f,
            Kd = 0.0f,
            MaxIntegral = 15f,
        };

        private readonly AngularController _attitudeController = new AngularController();

        private enum State { Coast, Braking, Final, Landed }

        private State _state = State.Coast;
        private float _throttle = 0f;
        private double _velocityProfile = 0;
        private double _burnAltitude = 0;
        private float _thrustWeightRatio = 0f;
        private double _horizontalVelocity = 0;
        private double _prevDescentSpeed = 0;

        private Rocket? _cachedRocket;
        private EngineModule[] _cachedEngines = Array.Empty<EngineModule>();

        private Rect _windowRect = new Rect(10, 120, 320, 210);

        private void OnGUI()
        {
            if (GetRocket() == null)
                return;

            _windowRect = GUI.Window(98765, _windowRect, DrawHUD, "Auto Landing v3.1");
        }

        private void DrawHUD(int id)
        {
            var rocket = GetRocket();
            if (rocket == null)
                return;

            var loc = rocket.location.Value;
            double altitude = Math.Max(0, loc.GetTerrainHeight(true));
            double verticalSpeed = loc.VerticalVelocity;

            int y = 20;
            void DrawLine(string s) { GUI.Label(new Rect(8, y, 300, 20), s); y += 20; }

            DrawLine($"State:    {_state}");
            DrawLine($"Altitude: {altitude:F1} m");
            DrawLine($"VSpeed:   {verticalSpeed:F1} m/s");
            DrawLine($"HSpeed:   {_horizontalVelocity:F1} m/s");
            DrawLine($"Throttle: {_throttle * 100f:F0}%");
            DrawLine($"BurnAlt:  {_burnAltitude:F1} m");
            DrawLine($"VProfile: {_velocityProfile:F1} m/s");
            DrawLine($"TWR:      {_thrustWeightRatio:F2}");
            DrawLine($"G:        {loc.planet.GetGravity(loc.Radius):F2} m/s²");

            GUI.DragWindow(new Rect(0, 0, 320, 20));
        }

        private void FixedUpdate()
        {
            try { FixedUpdateInternal(); }
            catch (Exception ex)
            {
                AutoLandingPlugin.Log.LogError($"Autopilot error: {ex}");
                var rocket = GetRocket();
                if (rocket != null)
                    CutEngines(rocket);
                ResetAll();
            }
        }

        private void FixedUpdateInternal()
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

            // Real gravity at current altitude — works on Moon, Mars, custom planets.
            float gravity = (float)loc.planet.GetGravity(loc.Radius);

            double altitude = Math.Max(0, loc.GetTerrainHeight(true));
            double descentSpeed = -loc.VerticalVelocity;

            // Signed horizontal speed in surface frame.
            // posAngle: angle of position vector (planet center → rocket).
            // East direction (CCW) is perpendicular to the radial vector.
            double posAngle = loc.position.AngleRadians;
            _horizontalVelocity = -loc.velocity.x * Math.Sin(posAngle)
                                 +  loc.velocity.y * Math.Cos(posAngle);

            // World-space angle the rocket must have to point radially outward.
            float surfaceNormalDeg = (float)(posAngle * 180.0 / Math.PI) - 90f;

            var engines = GetEngines(rocket);
            float maxThrust = engines.Sum(e => e.thrust.Value);
            float mass = rocket.rb2d.mass;

            if (mass <= 0 || maxThrust <= 0)
            {
                CutEngines(rocket);
                return;
            }

            // Tilt relative to surface normal, not the global axis.
            // rb2d.rotation is world-space; subtract normal to get actual tilt
            // regardless of where the rocket is on the planet.
            float worldAngle = AngularController.AngleDeg(rocket.rb2d);
            float tiltDeg = worldAngle - surfaceNormalDeg;
            tiltDeg = ((tiltDeg + 180f) % 360f + 360f) % 360f - 180f;
            float angleRad = Mathf.Abs(tiltDeg) * Mathf.Deg2Rad;

            float cosAngle = Mathf.Max(Mathf.Cos(angleRad), MinCosAngle);
            float maxAccel = maxThrust * gravity / mass;
            float effectiveAccel = maxAccel * cosAngle;
            float netDecel = effectiveAccel - gravity;

            _thrustWeightRatio = maxAccel / gravity;

            if (netDecel <= 0)
            {
                CutEngines(rocket);
                return;
            }

            // Landing detection: was descending above VTouch, now nearly stopped.
            if (_state == State.Final && _prevDescentSpeed > VTouch && descentSpeed < VTouch * 0.5)
            {
                _state = State.Landed;
                _velocityPid.Reset();
                CutEngines(rocket);
                return;
            }

            // Main burn profile
            _burnAltitude = Math.Max(0, (descentSpeed * descentSpeed - VTouch * VTouch) / (2.0 * netDecel));
            _velocityProfile = Math.Sqrt(2.0 * netDecel * altitude + VTouch * VTouch);

            if (_state == State.Coast && altitude <= _burnAltitude * SafetyFactor)
                _state = State.Braking;

            if (_state == State.Braking && altitude < FinalAlt)
            {
                _state = State.Final;
                _velocityPid.Reset();
            }

            switch (_state)
            {
                case State.Coast:
                    RunCoast(rocket, dt);
                    break;

                case State.Braking:
                    RunBraking(rocket, engines, effectiveAccel, descentSpeed, altitude, dt, gravity);
                    break;

                case State.Final:
                    RunFinal(rocket, engines, effectiveAccel, descentSpeed, altitude, dt, gravity);
                    break;
            }

            // Emergency governor — two independent triggers (OR):
            //   1. burnAltitude ≥ 85% of altitude: almost out of braking room
            //      (fires before 30 m/s for low-TWR rockets, e.g. TWR=1.2 at 73m → burnAlt/h=0.92)
            //   2. Absolute overspeed: below 200m and still above 30 m/s
            bool nearLimit = _burnAltitude > 0
                && altitude > MinAltitudeForBurnRatio
                && (_burnAltitude / altitude) >= EmergencyBurnRatio;
            bool absOverspeed = altitude < EmergencyAlt && descentSpeed > EmergencyVd;
            bool nearSuicide = _velocityProfile > 0 && descentSpeed >= _velocityProfile * NearSuicideThreshold;

            if (_state != State.Coast && (nearLimit || absOverspeed || nearSuicide))
            {
                SmoothThrottle(1f, dt);
                ApplyThrottle(rocket, engines);
            }

            _attitudeController.Apply(rocket.rb2d, surfaceNormalDeg, (float)_horizontalVelocity);
            _prevDescentSpeed = descentSpeed;
        }

        private void RunCoast(Rocket rocket, float dt)
        {
            SmoothThrottle(0f, dt);

            if (_throttle < ThrottleCutThreshold)
                CutEngines(rocket);
            else
            {
                rocket.throttle.throttleOn.Value = true;
                rocket.throttle.throttlePercent.Value = _throttle;
            }
        }

        private void RunBraking(
            Rocket rocket,
            EngineModule[] engines,
            float effectiveAccel,
            double descentSpeed,
            double altitude,
            float dt,
            float gravity)
        {
            double vSuicide = _velocityProfile * BrakingProfileMargin;
            double vFreefall = Math.Sqrt(2.0 * gravity * altitude) * FreefallFraction;
            double targetSpeed = Math.Max(VTouch, Math.Min(vSuicide, vFreefall));

            float speedError = (float)(descentSpeed - targetSpeed);
            float hoverThrottle = gravity / effectiveAccel;

            // PID runs continuously (no reset on target crossing).
            // Clamped ≥ 0: only adds throttle above hover, never subtracts.
            // Avoids discrete-reset spikes that caused throttle flicker.
            float pid = Mathf.Max(0f, _velocityPid.Update(speedError, dt));

            float targetThrottle = hoverThrottle + pid;

            if (altitude < FinalAlt * 3 && descentSpeed > targetSpeed * BrakingEmergencyFactor)
                targetThrottle = 1f;

            SmoothThrottle(Mathf.Clamp(targetThrottle, 0f, 1f), dt);
            ApplyThrottle(rocket, engines);
        }

        private void RunFinal(
            Rocket rocket,
            EngineModule[] engines,
            float effectiveAccel,
            double descentSpeed,
            double altitude,
            float dt,
            float gravity)
        {
            // Continuous linear ramp from vFreefall(FinalAlt) down to VTouch.
            // No step-changes: velocity error varies smoothly with altitude.
            // Uses gravity so the ramp adapts to Moon, Mars, etc.
            double vTop = FreefallFraction * Math.Sqrt(2.0 * gravity * FinalAlt);
            double targetSpeed = Math.Max(VTouch, VTouch + (altitude / FinalAlt) * (vTop - VTouch));

            float speedError = (float)(descentSpeed - targetSpeed);
            float hoverThrottle = gravity / effectiveAccel;
            float pid = _velocityPid.Update(speedError, dt);

            // Allow slightly negative PID so the rocket can gently re-accelerate if it
            // braked too hard — floor prevents free-fall (at most ThrottleFloorMargin below hover).
            float floor = Mathf.Max(ThrottleFloorMargin, hoverThrottle - ThrottleFloorMargin);
            float targetThrottle = Mathf.Clamp(hoverThrottle + pid, floor, 1f);

            if (altitude < FinalFlareAlt1 && descentSpeed > FinalFlareVd1) targetThrottle = 1f;
            if (altitude < FinalFlareAlt2 && descentSpeed > FinalFlareVd2) targetThrottle = 1f;

            SmoothThrottle(targetThrottle, dt);
            ApplyThrottle(rocket, engines);
        }

        private void SmoothThrottle(float target, float dt)
        {
            float rate = target > _throttle ? ThrRise : ThrFall;
            _throttle = Mathf.MoveTowards(_throttle, target, rate * dt);
        }

        private void ApplyThrottle(Rocket rocket, EngineModule[] engines)
        {
            foreach (var e in engines)
                e.engineOn.Value = true;

            rocket.throttle.throttleOn.Value = true;
            rocket.throttle.throttlePercent.Value = _throttle;
        }

        private void CutEngines(Rocket rocket)
        {
            foreach (var e in _cachedEngines)
                e.engineOn.Value = false;

            rocket.throttle.throttleOn.Value = false;
            rocket.throttle.throttlePercent.Value = 0f;
        }

        private void ResetAll()
        {
            _state = State.Coast;
            _throttle = 0f;
            _burnAltitude = 0;
            _velocityProfile = 0;
            _thrustWeightRatio = 0f;
            _horizontalVelocity = 0;
            _prevDescentSpeed = 0;
            _cachedRocket = null;
            _velocityPid.Reset();
        }

        private EngineModule[] GetEngines(Rocket rocket)
        {
            if (!ReferenceEquals(rocket, _cachedRocket))
            {
                _cachedRocket = rocket;
                _cachedEngines = rocket.partHolder.GetModules<EngineModule>();
            }
            return _cachedEngines;
        }

        private static Rocket? GetRocket()
            => PlayerController.main?.player?.Value as Rocket;
    }
}
