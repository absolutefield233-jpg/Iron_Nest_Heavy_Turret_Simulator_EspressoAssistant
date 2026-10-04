using System;
using Il2Cpp;
using MelonLoader;
using UnityEngine;
using UnityEngine.InputSystem;

[assembly: MelonInfo(typeof(EspressoAssistant.EspressoAssistantMod), "Espresso Assistant", "1.0.0", "4Dfish")]
[assembly: MelonGame("Iron Nest", "Iron Nest Heavy Turret Simulator")]

namespace EspressoAssistant
{
    /// <summary>
    /// Brews a good espresso by working the machine the way a player would - moving the two
    /// dials and pressing the button - while a PID loop holds temperature and pressure on target.
    ///
    /// The loop is needed because the dials only set a target: the machine decays temperature and
    /// pressure and couples each to the other, so a dial set once drifts within seconds.
    ///
    /// Before anything else the mod identifies the machine for itself. It parks the dials at their
    /// base setting, samples how the readings sit, then steps each dial and samples the response.
    /// That trace is written to the log, which is what the loop gains are worked out from - the
    /// machine gets measured rather than guessed at, and nothing has to be brewed to do it.
    /// </summary>
    public class EspressoAssistantMod : MelonMod
    {
        /// <summary>F10 brews. F11 / F9 step the fallback gain preset.</summary>
        private const Key BrewKey = Key.F10;

        // ---------------------------------------------------------------------------------
        // Identification
        // ---------------------------------------------------------------------------------

        private enum IdPhase
        {
            Waiting,
            SettleAtBase,
            TempStep,
            PressureStep,
            Finished,
        }

        private const float SettleSeconds = 3f;
        private const float StepSeconds = 5f;
        private const float StepFraction = 0.06f;
        private const float SampleInterval = 0.2f;

        private IdPhase _idPhase = IdPhase.Waiting;
        private float _idPhaseTime;
        private float _idSampleTimer;
        private float _tempBaseDial;
        private float _pressureBaseDial;

        public override void OnInitializeMelon()
        {
            LoggerInstance.Msg("Espresso Assistant v1.0.0 loaded.");
            LoggerInstance.Msg("F10 = brew. The machine identifies its own response first; watch for [IDENT] lines.");
        }

        // ---------------------------------------------------------------------------------
        // Gains
        // ---------------------------------------------------------------------------------

        private struct Gains
        {
            public string Label;
            public float TempKp;
            public float TempKi;
            public float TempKd;
            public float PressureKp;
            public float PressureKi;
            public float PressureKd;
        }

        private static readonly Gains FinalGains = new Gains
        {
            Label = "final",
            TempKp = 1.6f, TempKi = 3.5f, TempKd = 0.5f,
            PressureKp = 16f, PressureKi = 1f, PressureKd = 0f,
        };

        private const float IntegralLimit = 200f;

        /// <summary>
        /// Low-pass on the derivative. The derivative is what stops the reading sailing past the
        /// target - which matters here because temperature overshoot leaks straight into pressure
        /// through the machine's coupling - but a raw derivative on a noisy reading is all spike,
        /// so it is smoothed over this window first.
        /// </summary>
        private const float DerivativeFilterSeconds = 0.15f;

        /// <summary>
        /// How far above the ideal the machine is taken before a shot starts.
        ///
        /// Measured from a real brew: the moment the shot begins the temperature falls about
        /// sixteen degrees and takes three seconds to climb back, so warming up TO the ideal
        /// means the shot spends its first seconds cold. Standing the machine above the ideal by
        /// roughly that drop puts the shock back on target instead of under it.
        /// </summary>
        /// <summary>How long the target takes to slide from the overshoot back to the ideal once a
        /// shot begins. Stepping it instantly made the loop slam the handle shut.</summary>
        private const float TargetRampSeconds = 4f;

        /// <summary>The warming-up stage stops early once the reading is this close, or after this
        /// many seconds, rather than waiting for an exact figure - the machine cools very slowly
        /// and waiting for precision there just burns time.</summary>
        private const float PreheatTempTolerance = 3f;
        private const float PreheatPressureTolerance = 0.5f;
        private const float PreheatMaxSeconds = 45f;

        // Measured on the machine: it heats at roughly 13 degrees a second and cools at about
        // half a degree a second. Overshooting therefore pushes it somewhere it cannot come back
        // from inside a twelve second shot, and the whole brew runs hot. The drop when a shot
        // starts is better answered by the loop's own fast heating, so there is no overshoot.
        /// <summary>
        /// Both readings are parked slightly BELOW their ideals before a shot. Pressure climbs on
        /// its own once the pump runs, and temperature climbs too while the loop drives it up, so
        /// starting low lets the shot carry both onto the target instead of over it.
        /// </summary>
        private const float PreheatOvershootTemp = -3f;

                /// <summary>
        /// Lowered on purpose. Pressure settles about 0.3 bar above the ideal through a shot and
        /// the handle cannot pull it back down once the pump is running, so the machine is warmed
        /// BELOW the ideal instead and the shot carries it up onto the target. A first attempt at
        /// -0.4 only bought 0.05 because the preheat's tolerance let it start at 8.9 without ever
        /// reaching the figure, so the tolerance is tightened along with it.
        /// </summary>
        private const float PreheatOvershootPressure = -1.4f;

        // ---------------------------------------------------------------------------------
        // Runtime
        // ---------------------------------------------------------------------------------

        private bool _assistActive;
        private bool _sawBrewing;
        private bool _stopped;
        private int _loggedDialInventory;
        private sealed class LoopState
        {
            public float Integral;
            public float PreviousError;
            public float Derivative;
            public bool HasPrevious;
        }

        private readonly LoopState _tempLoop = new LoopState();
        private readonly LoopState _pressureLoop = new LoopState();
        private int _controlLogFrames;

        /// <summary>Kept on, the assistant brews by itself over and over and steps the preset each
        /// time, so a set of gains can be judged from the log without anyone doing the brewing.</summary>
        private bool _autoTest = false;
        private DateTime _autoNextTryAt = DateTime.MinValue;

        private bool _preheating;
        private float _preheatElapsed;
        private float _preheatLogTimer;

        private float _tempAbsErrSum;
        private float _tempMaxErr;
        private float _pressureAbsErrSum;
        private float _pressureMaxErr;
        private int _errSamples;

        public override void OnUpdate()
        {
            float dt = Time.deltaTime;

            Keyboard keyboard = Keyboard.current;
            if (keyboard != null && keyboard[BrewKey].wasPressedThisFrame)
            {
                BeginAssist();
            }

            EspressoBrewingController machine = FindMachine();
            if (machine == null)
            {
                return;
            }

            string state = StateOf(machine);

            if (_preheating)
            {
                if (state == "Brewing")
                {
                    // Something else started the shot; fall through to holding it.
                    _preheating = false;
                    _assistActive = true;
                    _sawBrewing = true;
                }
                else
                {
                    _preheatElapsed += dt;
                    KeepDialsAtIdeal(machine, PreheatOvershootTemp, PreheatOvershootPressure, dt);

                    float tempGap = Math.Abs(safe(machine.idealTemperature) + PreheatOvershootTemp - safe(machine.simTemperature));
                    float pressureGap = Math.Abs(safe(machine.idealPressure) + PreheatOvershootPressure - safe(machine.simPressure));

                    _preheatLogTimer += dt;
                    if (_preheatLogTimer >= 1f)
                    {
                        _preheatLogTimer = 0f;
                        LoggerInstance.Msg("[PREHEAT] " + _preheatElapsed.ToString("0.0") + "s temp=" +
                                           safe(machine.simTemperature).ToString("0.0") + "/" + (safe(machine.idealTemperature) + PreheatOvershootTemp).ToString("0.0") +
                                           " (dial " + DialText(machine.temperatureDial) + ") pressure=" +
                                           safe(machine.simPressure).ToString("0.000") + "/" + (safe(machine.idealPressure) + PreheatOvershootPressure).ToString("0.000") +
                                           " (dial " + DialText(machine.pressureDial) + ")");
                    }

                    bool closeEnough = tempGap <= PreheatTempTolerance && pressureGap <= PreheatPressureTolerance;

                    // No shortcut for "it is already warm": being above the target is not the same
                    // as being on it, and the machine cools so slowly that a shot started hot stays
                    // hot for its whole length. Only genuinely being on target counts.
                    bool waited = _preheatElapsed >= PreheatMaxSeconds;

                    if (closeEnough || waited)
                    {
                        _preheating = false;
                        _assistActive = true;
                        _sawBrewing = false;
            _tempLoop.Integral = 0f;
            _tempLoop.HasPrevious = false;
            _tempLoop.Derivative = 0f;
            _pressureLoop.Integral = 0f;
            _pressureLoop.HasPrevious = false;
            _pressureLoop.Derivative = 0f;

                        _controlLogFrames = 0;
                        _tempAbsErrSum = 0f;
                        _tempMaxErr = 0f;
                        _pressureAbsErrSum = 0f;
                        _pressureMaxErr = 0f;
                        _errSamples = 0;

                        // The warming stage can leave a large integral behind; carrying it into
                        // the shot is what made the handle slam shut right at the start.

                        ClickBrewHandle(machine, true, "start");
                        LoggerInstance.Msg("[PREHEAT] ready after " + _preheatElapsed.ToString("0.0") + "s at temp=" +
                                           safe(machine.simTemperature).ToString("0.00") +
                                           " pressure=" + safe(machine.simPressure).ToString("0.000") +
                                           " (targets " + safe(machine.idealTemperature).ToString("0.00") + " / " +
                                           (safe(machine.idealPressure) + PreheatOvershootPressure).ToString("0.000") +
                                           ") - brewing with preset [" + FinalGains.Label + "].");
                    }
                    return;
                }
            }

            if (_assistActive)
            {
                if (state == "Brewing")
                {
                    _sawBrewing = true;
                    // Slide the target down instead of stepping it: the shot is already cooling
                    // the machine, so the loop only has to guide it to the ideal, not force it.
                    float blend = safe(machine.BrewElapsedSeconds) / TargetRampSeconds;
                    if (blend > 1f) blend = 1f;
                    if (blend < 0f) blend = 0f;

                    KeepDialsAtIdeal(machine, PreheatOvershootTemp * (1f - blend),
                                     PreheatOvershootPressure * (1f - blend), dt);
                    TrackError(machine);

                    // Release the handle once the gauge has run out the ideal time - in normal
                    // play that second press is what stops and scores the shot.
                    if (!_stopped && safe(machine.BrewElapsedSeconds) >= safe(machine.idealBrewSeconds))
                    {
                        _stopped = true;
                        ClickBrewHandle(machine, false, "stop");

                        // Park the dials low once the shot is over. The machine only loses pressure
                        // slowly, so starting that bleed immediately means the next preheat does not
                        // have to sit through it.
                        SetDialRaw(machine.temperatureDial, machine.temperatureDial != null ? machine.temperatureDial.minOutputValue : 0f);
                        SetDialRaw(machine.pressureDial, machine.pressureDial != null ? machine.pressureDial.minOutputValue : 0f);
                        LoggerInstance.Msg("[ESPRESSO] stopping at " + safe(machine.BrewElapsedSeconds).ToString("0.00") +
                                           "s (ideal " + safe(machine.idealBrewSeconds).ToString("0.00") + "s).");
                    }

                    _controlLogFrames++;
                    if (_controlLogFrames % 30 == 1)
                    {
                        LoggerInstance.Msg("[ESPRESSO] t=" + machine.BrewElapsedSeconds.ToString("0.0") +
                                           "s temp=" + safe(machine.simTemperature).ToString("0.00") + "/" + safe(machine.idealTemperature).ToString("0.00") +
                                           " (handle " + DialText(machine.temperatureDial) + ")" +
                                           " | pressure=" + safe(machine.simPressure).ToString("0.000") + "/" + safe(machine.idealPressure).ToString("0.000") +
                                           " (handle " + DialText(machine.pressureDial) + ")");
                    }
                    return;
                }

                if (_sawBrewing)
                {
                    _sawBrewing = false;
                    _assistActive = false;
                    ReportResult(machine);

                    _tempLoop.Integral = 0f;
                    _tempLoop.HasPrevious = false;
                    _tempLoop.Derivative = 0f;
                    _pressureLoop.Integral = 0f;
                    _pressureLoop.HasPrevious = false;
                    _pressureLoop.Derivative = 0f;
                    return;
                }
            }

            // Nothing brewing: identify the machine first, then keep shots coming by itself.
            if (_idPhase != IdPhase.Finished && state != "Brewing")
            {
                RunIdentification(machine, dt);
                return;
            }

            if (!_autoTest || _preheating || _assistActive || state == "Brewing")
            {
                return;
            }

            if (DateTime.UtcNow < _autoNextTryAt)
            {
                return;
            }

            if (state == "Ready")
            {
                BeginAssist();
                return;
            }

            // Not ready. Try to put a cup back in the machine so the next shot can start; if
            // there is no cup to be had, say so once and stop rather than spinning.
            if (TryReloadCup(machine))
            {
                _autoNextTryAt = DateTime.UtcNow.AddSeconds(1.5);
                return;
            }

            _autoTest = false;
            LoggerInstance.Msg("[AUTO] no cup available - automatic testing stopped after this run. " +
                               "Put a few cups next to the machine to let it sweep the presets.");
        }

        // ---------------------------------------------------------------------------------
        // Identification routine
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// Measures how the machine answers the dials. Both dials are parked at their base setting
        /// for a settling period, then one dial is stepped and the readings are sampled for a few
        /// seconds, then the other. The samples are the plant's step response.
        /// </summary>
        private void RunIdentification(EspressoBrewingController machine, float dt)
        {
            DialInteractable tempDial = machine.temperatureDial;
            DialInteractable pressureDial = machine.pressureDial;
            if (tempDial == null || pressureDial == null)
            {
                _idPhase = IdPhase.Finished;
                return;
            }

            if (_idPhase == IdPhase.Waiting)
            {
                _tempBaseDial = BaseDialValue(tempDial, machine.IdealTempMapped);
                _pressureBaseDial = BaseDialValue(pressureDial, machine.IdealPressureMapped);

                LoggerInstance.Msg("[IDENT] machine constants: tempMax=" + safe(machine.tempMax).ToString("0.00") +
                                   " pressureMax=" + safe(machine.pressureMax).ToString("0.000") +
                                   " tempInputScale=" + safe(machine.tempInputScale).ToString("0.00") +
                                   " pressureInputScale=" + safe(machine.pressureInputScale).ToString("0.00") +
                                   " thermalWarmup=" + safe(machine.thermalWarmupDuration).ToString("0.00") +
                                   " tempDecayCold=" + safe(machine.tempDecayRateCold).ToString("0.000") +
                                   " tempDecayWarm=" + safe(machine.tempDecayRateWarmed).ToString("0.000") +
                                   " pressureDecay=" + safe(machine.pressureDecayRate).ToString("0.000") +
                                   " tempToPressure=" + safe(machine.tempToPressureCoupling).ToString("0.000") +
                                   " pressureToTemp=" + safe(machine.pressureToTempCoupling).ToString("0.000") +
                                   " tempDialMax=" + safe(machine.tempDialMaxOutput).ToString("0.00") +
                                   " pressureDialMax=" + safe(machine.pressureDialMaxOutput).ToString("0.00"));

                LoggerInstance.Msg("[IDENT] dial ranges: temp=[" + safe(tempDial.minOutputValue).ToString("0.00") + ".." + safe(tempDial.maxOutputValue).ToString("0.00") + "]" +
                                   " pressure=[" + safe(pressureDial.minOutputValue).ToString("0.00") + ".." + safe(pressureDial.maxOutputValue).ToString("0.00") + "]" +
                                   " | base dial temp=" + _tempBaseDial.ToString("0.00") + " pressure=" + _pressureBaseDial.ToString("0.00"));

                _idPhase = IdPhase.SettleAtBase;
                _idPhaseTime = 0f;
                _idSampleTimer = 0f;
            }

            _idPhaseTime += dt;

            switch (_idPhase)
            {
                case IdPhase.SettleAtBase:
                    SetDialRaw(tempDial, _tempBaseDial);
                    SetDialRaw(pressureDial, _pressureBaseDial);
                    Sample(machine, "settle", dt);
                    if (_idPhaseTime >= SettleSeconds)
                    {
                        Advance(ref _idPhase, IdPhase.TempStep);
                    }
                    break;

                case IdPhase.TempStep:
                    SetDialRaw(tempDial, _tempBaseDial + StepFraction * (tempDial.maxOutputValue - tempDial.minOutputValue));
                    SetDialRaw(pressureDial, _pressureBaseDial);
                    Sample(machine, "tempStep", dt);
                    if (_idPhaseTime >= StepSeconds)
                    {
                        Advance(ref _idPhase, IdPhase.PressureStep);
                    }
                    break;

                case IdPhase.PressureStep:
                    SetDialRaw(tempDial, _tempBaseDial);
                    SetDialRaw(pressureDial, _pressureBaseDial + StepFraction * (pressureDial.maxOutputValue - pressureDial.minOutputValue));
                    Sample(machine, "pressureStep", dt);
                    if (_idPhaseTime >= StepSeconds)
                    {
                        SetDialRaw(tempDial, _tempBaseDial);
                        SetDialRaw(pressureDial, _pressureBaseDial);
                        _idPhase = IdPhase.Finished;
                        LoggerInstance.Msg("[IDENT] done. Send the log and the gains can be worked out from the trace.");
                    }
                    break;
            }
        }

        private static void Advance(ref IdPhase phase, IdPhase next)
        {
            phase = next;
        }

        private void Sample(EspressoBrewingController machine, string phase, float dt)
        {
            _idSampleTimer += dt;
            if (_idSampleTimer < SampleInterval)
            {
                return;
            }
            _idSampleTimer = 0f;

            LoggerInstance.Msg("[IDENT] " + phase +
                               " t=" + _idPhaseTime.ToString("0.0") +
                               " tempSim=" + safe(machine.simTemperature).ToString("0.0000") +
                               " tempMapped=" + safe(machine.MappedTemperature).ToString("0.0000") +
                               " tempDial=" + DialText(machine.temperatureDial) +
                               " | pressSim=" + safe(machine.simPressure).ToString("0.0000") +
                               " pressMapped=" + safe(machine.MappedPressure).ToString("0.0000") +
                               " pressDial=" + DialText(machine.pressureDial));
        }

        // ---------------------------------------------------------------------------------
        // Brewing
        // ---------------------------------------------------------------------------------


        /// <summary>
        /// Starts the machine warming rather than brewing straight away.
        ///
        /// The machine is cold and takes about ten seconds to come up to heat, while a shot lasts
        /// twelve - starting the shot immediately means the temperature never reaches the ideal and
        /// no amount of loop gain can fix that. So the dials are set and the machine is left to
        /// warm until the readings are actually on target, and only then does the shot begin.
        /// </summary>
        private void BeginAssist()
        {
            EspressoBrewingController machine = FindMachine();
            if (machine == null)
            {
                LoggerInstance.Warning("[ESPRESSO] No espresso machine in this scene.");
                return;
            }

            LogDialInventory(machine);

            string state = StateOf(machine);
            if (state == "Brewing")
            {
                LoggerInstance.Msg("[ESPRESSO] Already brewing; the assistant is holding the dials.");
                return;
            }
            if (state != "Ready")
            {
                LoggerInstance.Warning("[ESPRESSO] Machine is " + state + " - load a coffee can and a cup first.");
                return;
            }

            _assistActive = false;
            _preheating = true;
            _preheatElapsed = 0f;
            _preheatLogTimer = 1f;
            _stopped = false;

            _tempLoop.Integral = 0f;
            _tempLoop.HasPrevious = false;
            _tempLoop.Derivative = 0f;
            _pressureLoop.Integral = 0f;
            _pressureLoop.HasPrevious = false;
            _pressureLoop.Derivative = 0f;

            _controlLogFrames = 0;
            _tempAbsErrSum = 0f;
            _tempMaxErr = 0f;
            _pressureAbsErrSum = 0f;
            _pressureMaxErr = 0f;
            _errSamples = 0;

            float tempMax = safe(machine.tempMax);
            float pressureMax = safe(machine.pressureMax);
            float tempTargetMapped = tempMax > 0.0001f ? (safe(machine.idealTemperature) + PreheatOvershootTemp) / tempMax : 0f;
            float pressureTargetMapped = pressureMax > 0.0001f ? (safe(machine.idealPressure) + PreheatOvershootPressure) / pressureMax : 0f;

            SetDialRaw(machine.temperatureDial, BaseDialValue(machine.temperatureDial, tempTargetMapped));
            SetDialRaw(machine.pressureDial, BaseDialValue(machine.pressureDial, pressureTargetMapped));

            LoggerInstance.Msg("[PREHEAT] warming up to temp=" + safe(machine.idealTemperature).ToString("0.0") +
                               " pressure=" + safe(machine.idealPressure).ToString("0.000") +
                               " before the shot starts.");
        }

        /// <summary>
        /// Clicks the brew handle the way the game's own cursor would.
        ///
        /// Setting the handle's active flag only changed how the handle looked and left the
        /// machine alone - a shot "started" that way did not begin until the handle was clicked by
        /// hand nearly half a minute later. So the handle is now hovered and clicked through the
        /// same entry points the cursor manager uses, and whatever the game has wired to the
        /// handle runs by itself. One click starts a shot and the next one stops it, exactly as
        /// it does for a player.
        /// </summary>
        private void ClickBrewHandle(EspressoBrewingController machine, bool wantBrewing, string why)
        {
            LookAtTarget handle = machine.brewButton;
            if (handle == null)
            {
                LoggerInstance.Warning("[ESPRESSO] Brew handle not found; cannot " + why + ".");
                return;
            }

            // The handle is a latching switch whose own state can drift out of step with the
            // machine - it is a toggle, so a click made while it is already where we want it does
            // the opposite. The machine's own state is the authority, so what is wanted is checked
            // against that first and the click is skipped when there is nothing to do.
            string before = StateOf(machine);
            bool brewing = before == "Brewing";

            if (brewing == wantBrewing)
            {
                LoggerInstance.Msg("[ESPRESSO] no click needed (" + why + "): machine already " + before +
                                   ", handle isActive=" + handle.isActive);
                return;
            }

            try
            {
                Interactable target = handle.interactable;

                for (int attempt = 1; attempt <= 3; attempt++)
                {
                    handle.UpdateHover(true, true);
                    handle.HandleClickDownFromManager(target);
                    handle.HandleClickUpFromManager(target);

                    string after = StateOf(machine);
                    bool nowBrewing = after == "Brewing";
                    LoggerInstance.Msg("[ESPRESSO] click " + attempt + " (" + why + "): " + before +
                                       " -> " + after + ", handle isActive=" + handle.isActive);

                    if (nowBrewing == wantBrewing)
                    {
                        return;
                    }
                }

                LoggerInstance.Warning("[ESPRESSO] the handle would not " + why +
                                       " after three clicks (" + StateOf(machine) + ").");
            }
            catch (Exception e)
            {
                LoggerInstance.Error("[ESPRESSO] Could not click the brew handle: " + e);
            }
        }

        /// <summary>
        /// Puts a spare cup back into the machine's cup slot so the next shot can run. Finds a cup
        /// lying around and hands it to the slot through the slot's own PlaceItem.
        /// </summary>
        private bool TryReloadCup(EspressoBrewingController machine)
        {
            try
            {
                ItemSlot slot = machine.cupSlot;
                if (slot == null)
                {
                    return false;
                }

                // A cup full of coffee keeps the machine out of Ready, and the cup that is
                // already sitting in the slot is the one that just got filled. Emptying it is
                // what lets the next shot start - no need to take it off the machine at all.
                if (slot.HasItem)
                {
                    // Never empties the cup: that would throw the player's coffee away.
                    return machine._loadedCup != null;
                }

                foreach (EspressoCup cup in Resources.FindObjectsOfTypeAll<EspressoCup>())
                {
                    if (cup == null || cup.gameObject == null || cup._draggable == null)
                    {
                        continue;
                    }

                    slot.PlaceItem(cup._draggable);
                    LoggerInstance.Msg("[AUTO] put an emptied spare cup into the machine.");
                    return true;
                }

                return false;
            }
            catch (Exception e)
            {
                LoggerInstance.Warning("[AUTO] could not reload a cup: " + e.Message);
                return false;
            }
        }

        private void TrackError(EspressoBrewingController machine)
        {
            float tempError = Math.Abs(safe(machine.idealTemperature) - safe(machine.simTemperature));
            float pressureError = Math.Abs(safe(machine.idealPressure) - safe(machine.simPressure));

            _tempAbsErrSum += tempError;
            _pressureAbsErrSum += pressureError;
            if (tempError > _tempMaxErr) _tempMaxErr = tempError;
            if (pressureError > _pressureMaxErr) _pressureMaxErr = pressureError;
            _errSamples++;
        }

        /// <summary>
        /// Holds both readings on their targets. The targets carry an overshoot while the machine
        /// is warming and none once the shot is running, so the shock of starting lands on ideal.
        /// </summary>
        private void KeepDialsAtIdeal(EspressoBrewingController machine, float tempOvershoot, float pressureOvershoot, float dt)
        {
            if (dt <= 0f || dt > 0.25f)
            {
                dt = 0.02f;
            }

            Gains gains = FinalGains;

            float tempTarget = safe(machine.idealTemperature) + tempOvershoot;
            float pressureTarget = safe(machine.idealPressure) + pressureOvershoot;

            Control(machine.temperatureDial, tempTarget, safe(machine.simTemperature),
                    safe(machine.tempMax), gains.TempKp, gains.TempKi, gains.TempKd, _tempLoop, dt);
            Control(machine.pressureDial, pressureTarget, safe(machine.simPressure),
                    safe(machine.pressureMax), gains.PressureKp, gains.PressureKi, gains.PressureKd, _pressureLoop, dt);
        }

        // ---------------------------------------------------------------------------------
        // Dial plumbing
        // ---------------------------------------------------------------------------------

        private static void SetDialRaw(DialInteractable dial, float output)
        {
            if (dial == null)
            {
                return;
            }

            if (dial.minOutputValue < dial.maxOutputValue)
            {
                if (output < dial.minOutputValue) output = dial.minOutputValue;
                if (output > dial.maxOutputValue) output = dial.maxOutputValue;
            }

            dial.accumulatedValue = output;

            float span = dial.maxOutputValue - dial.minOutputValue;
            if (span > 0.0001f)
            {
                float t = (output - dial.minOutputValue) / span;
                dial.currentRotationAngle = dial.minRotationAngle + t * (dial.maxRotationAngle - dial.minRotationAngle);
            }

            try
            {
                dial.clampCenterValue = output;
            }
            catch
            {
                // Not fatal; the handle just stays where it was put this frame.
            }
        }

        private static float BaseDialValue(DialInteractable dial, float idealMapped)
        {
            return dial == null ? 0f : idealMapped * dial.maxOutputValue;
        }

        private static void Control(DialInteractable dial, float readingTarget, float actualReading,
                                    float readingMax, float kp, float ki, float kd, LoopState loop, float dt)
        {
            if (dial == null)
            {
                return;
            }

            float error = readingTarget - actualReading;

            float rawDerivative = loop.HasPrevious ? (error - loop.PreviousError) / dt : 0f;
            loop.PreviousError = error;
            loop.HasPrevious = true;
            float alpha = dt / (DerivativeFilterSeconds + dt);
            loop.Derivative += alpha * (rawDerivative - loop.Derivative);

            float candidate = loop.Integral + error * dt;
            if (candidate > IntegralLimit) candidate = IntegralLimit;
            if (candidate < -IntegralLimit) candidate = -IntegralLimit;

            float correction = kp * error + ki * candidate + kd * loop.Derivative;
            float readingToDial = readingMax > 0.0001f ? dial.maxOutputValue / readingMax : 1f;
            float targetMapped = readingMax > 0.0001f ? readingTarget / readingMax : 0f;

            float wanted = BaseDialValue(dial, targetMapped) + correction * readingToDial;
            float clamped = wanted;
            if (dial.minOutputValue < dial.maxOutputValue)
            {
                if (clamped < dial.minOutputValue) clamped = dial.minOutputValue;
                if (clamped > dial.maxOutputValue) clamped = dial.maxOutputValue;
            }

            // Anti-windup: while the handle is pinned against a stop the loop cannot do anything
            // more in that direction, so the integral is left alone. Letting it keep climbing is
            // what drove the handle to zero the moment a shot began.
            if (Math.Abs(clamped - wanted) < 0.0001f)
            {
                loop.Integral = candidate;
            }

            SetDialRaw(dial, clamped);
        }

        // ---------------------------------------------------------------------------------
        // Reporting
        // ---------------------------------------------------------------------------------

        private void ReportResult(EspressoBrewingController machine)
        {
            // Read the cup first: collecting it empties the slot, and reading afterwards is why
            // every report so far said "no cup".
            Gains gains = FinalGains;
            EspressoCup cup = machine._loadedCup;

            float tempMean = _errSamples > 0 ? _tempAbsErrSum / _errSamples : 0f;
            float pressureMean = _errSamples > 0 ? _pressureAbsErrSum / _errSamples : 0f;

            string cupText = cup == null
                ? "no cup"
                : ("quality=" + safe(cup.Quality).ToString("0.0") +
                   " temp=" + safe(cup.TemperatureScore).ToString("0.0") +
                   " pressure=" + safe(cup.PressureScore).ToString("0.0") +
                   " timing=" + safe(cup.TimingScore).ToString("0.0"));

            LoggerInstance.Msg("[TUNE] preset [" + gains.Label + "] -> " + cupText +
                               " | tempErr mean=" + tempMean.ToString("0.00") + " max=" + _tempMaxErr.ToString("0.00") +
                               " | pressureErr mean=" + pressureMean.ToString("0.000") + " max=" + _pressureMaxErr.ToString("0.000") +
                               " | samples=" + _errSamples);

            // The cup is deliberately left alone. An earlier version emptied it to keep an
            // automatic run going, which threw away the coffee the player had just made.
        }

        private void LogDialInventory(EspressoBrewingController machine)
        {
            if (_loggedDialInventory >= 1)
            {
                return;
            }
            _loggedDialInventory++;

            try
            {
                LoggerInstance.Msg("[ESPRESSO] idealTemp=" + safe(machine.idealTemperature).ToString("0.00") +
                                   " idealPressure=" + safe(machine.idealPressure).ToString("0.000") +
                                   " idealSeconds=" + safe(machine.idealBrewSeconds).ToString("0.00") +
                                   " tempMax=" + safe(machine.tempMax).ToString("0.00") +
                                   " pressureMax=" + safe(machine.pressureMax).ToString("0.000"));
            }
            catch
            {
                // Diagnostics only.
            }
        }

        // ---------------------------------------------------------------------------------
        // Status panel
        // ---------------------------------------------------------------------------------

        public override void OnGUI()
        {
            try
            {
                // Only shown while the assistant is actually working a shot. The rest of the
                // time the screen is left completely alone.
                if (!_preheating && !_assistActive)
                {
                    return;
                }

                EspressoBrewingController machine = FindMachine();
                if (machine == null)
                {
                    return;
                }

                EspressoCup cup = machine._loadedCup;
                bool showCup = cup != null && cup.IsInitialised;

                const float width = 330f;
                const float margin = 20f;
                const float lineHeight = 24f;

                float rows = showCup ? 5f : 4f;
                Rect box = new Rect(Screen.width - width - margin, margin, width, 42f + rows * lineHeight);
                GUI.Box(box, "\u5496\u5561\u673a\u52a9\u624b (F10)");

                float lx = box.x + 10f;
                float lw = box.width - 20f;
                float ly = box.y + 25f;

                Line(lx, lw, ref ly, "\u72b6\u6001\uff1a" + (_preheating ? "\u9884\u70ed\u4e2d" : "\u51b2\u716e\u4e2d"),
                     _preheating ? new Color(1f, 0.9f, 0.5f) : new Color(0.6f, 1f, 0.6f));

                float tempDiff = Math.Abs(safe(machine.simTemperature) - safe(machine.idealTemperature));
                Line(lx, lw, ref ly, "\u6e29\u5ea6\uff1a" + safe(machine.simTemperature).ToString("0.0") +
                                     " / " + safe(machine.idealTemperature).ToString("0.0") +
                                     "   " + tempDiff.ToString("0.00"),
                     tempDiff <= 1f ? new Color(0.55f, 1f, 0.55f) : new Color(1f, 0.85f, 0.5f));

                float pressureDiff = Math.Abs(safe(machine.simPressure) - safe(machine.idealPressure));
                Line(lx, lw, ref ly, "\u538b\u529b\uff1a" + safe(machine.simPressure).ToString("0.000") +
                                     " / " + safe(machine.idealPressure).ToString("0.000") +
                                     "   " + pressureDiff.ToString("0.000"),
                     pressureDiff <= 0.2f ? new Color(0.55f, 1f, 0.55f) : new Color(1f, 0.85f, 0.5f));

                Line(lx, lw, ref ly, "\u65f6\u95f4\uff1a" + safe(machine.BrewElapsedSeconds).ToString("0.00") + "s / " +
                                     safe(machine.idealBrewSeconds).ToString("0.00") + "s", null);

                if (showCup)
                {
                    Line(lx, lw, ref ly, "\u6210\u676f\uff1a" + safe(cup.Quality).ToString("0.0") +
                                         "\uff08\u6e29 " + safe(cup.TemperatureScore).ToString("0.0") +
                                         "  \u538b " + safe(cup.PressureScore).ToString("0.0") +
                                         "  \u65f6 " + safe(cup.TimingScore).ToString("0.0") + "\uff09",
                         new Color(0.7f, 1f, 1f));
                }
            }
            catch
            {
                // A status box is never worth an error.
            }
        }

        private static void Line(float x, float width, ref float y, string text, Color? colour)
        {
            Color previous = GUI.color;
            if (colour.HasValue)
            {
                GUI.color = colour.Value;
            }
            GUI.Label(new Rect(x, y, width, 21f), text);
            GUI.color = previous;
            y += 24f;
        }

        private static string DialText(DialInteractable dial)
        {
            return dial == null ? "n/a" : dial.accumulatedValue.ToString("0.00");
        }

        private static float safe(float value)
        {
            return float.IsNaN(value) || float.IsInfinity(value) ? 0f : value;
        }

        private static string StateOf(EspressoBrewingController machine)
        {
            try
            {
                return machine.CurrentState.ToString();
            }
            catch
            {
                return "Unknown";
            }
        }

        private static EspressoBrewingController FindMachine()
        {
            try
            {
                foreach (EspressoBrewingController machine in Resources.FindObjectsOfTypeAll<EspressoBrewingController>())
                {
                    if (machine != null && machine.gameObject != null)
                    {
                        return machine;
                    }
                }
            }
            catch
            {
                // Falls through to null.
            }
            return null;
        }
    }
}
