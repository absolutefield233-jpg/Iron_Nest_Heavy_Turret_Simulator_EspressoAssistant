using System;
using Il2Cpp;
using MelonLoader;
using UnityEngine;
using UnityEngine.InputSystem;

[assembly: MelonInfo(typeof(EspressoAssistant.EspressoAssistantMod), "Espresso Assistant", "1.1.1", "4Dfish")]
[assembly: MelonGame("Iron Nest", "Iron Nest Heavy Turret Simulator")]

namespace EspressoAssistant
{
    /// <summary>
    /// Brews a good espresso by working the machine the way a player would - moving the two dials
    /// and clicking the brew handle - with a PID loop holding temperature and pressure on target.
    ///
    /// The loop runs in TWO STAGES with separate gains, because the machine behaves like two
    /// different systems:
    ///
    ///   Warming - the dials are in charge. The readings follow them closely in both directions,
    ///   so the only job is to arrive without swinging past the mark.
    ///
    ///   Brewing - the dials lose much of their authority. A shot brings its own heat and its own
    ///   pressure, both pushing the readings up whatever the dials do, so this stage needs its own,
    ///   stronger loop pulling the other way.
    ///
    /// Warming therefore parks both readings a little BELOW their ideals, so the shot's own push
    /// carries them up onto the target instead of past it.
    /// </summary>
    public class EspressoAssistantMod : MelonMod
    {
        /// <summary>F10 brews. No other key is taken.</summary>
        private const Key BrewKey = Key.F10;

        public override void OnSceneWasLoaded(int buildIndex, string sceneName)
        {
            // A run cannot survive a scene change - the machine it was driving may not exist in the
            // new one - so a half-finished warm-up is dropped rather than carried across.
            if (_preheating || _brewing)
            {
                LoggerInstance.Msg("[ESPRESSO] Scene changed to '" + sceneName + "'; the shot in progress is abandoned.");
            }
            AbandonRun();
        }

        public override void OnInitializeMelon()
        {
            LoggerInstance.Msg("Espresso Assistant v1.1.1 loaded. Press F10 at the espresso machine.");
            LoggerInstance.Msg("Load a coffee grounds can and a cup first; the machine must read Ready.");
        }

        // =================================================================================
        // Control gains - one set per stage
        // =================================================================================

        private struct Gains
        {
            public float TempKp, TempKi, TempKd;
            public float PressureKp, PressureKi, PressureKd;

            /// <summary>
            /// How much of the "dial reading equals the number on the dial" starting guess to use
            /// before the loop's own correction is added. The guess is badly wrong on this machine:
            /// a temperature dial of 47 holds 93 degrees and a pressure dial of 1.5 holds 9 bar, so
            /// leaning on it whole keeps both dials far above where they need to be. Zero means
            /// unset, which is read as 1 (the old behaviour) so untouched entries are unaffected.
            /// </summary>
            public float TempFf, PressureFf;
        }

        /// <summary>Reads a feed-forward factor, treating an unset field as 1.</summary>
        private static float Ff(float value)
        {
            return value > 0.0001f ? value : 1f;
        }

        /// <summary>
        /// Warming. The dials rule here, so the loop only has to bring the readings down onto the
        /// mark and keep them from swinging. Soft deliberately: a hard push overshoots and then has
        /// to be undone, and that wobble is exactly what this stage must not have.
        /// </summary>
        /// <summary>
        /// One test case: which gains the warming stage uses and which the brewing stage uses.
        /// The list is laid out so that only ONE stage changes between neighbouring entries - the
        /// first half walks the warming gains with the brewing ones held still, the second half
        /// does the opposite. Since every shot logs both stages separately, one sweep therefore
        /// tunes both without them getting tangled up in each other.
        /// </summary>
        private sealed class Candidate
        {
            public Gains Preheat;
            public Gains Brew;

            /// <summary>
            /// Warming runs in two parts. While a reading is still further from its mark than the
            /// corresponding lead, its dial is pinned wide open and the loop is out of the way;
            /// that gets the reading up at the machine's own full speed, which no gain setting can
            /// beat. Once both readings are inside their leads the loops take over and only have to
            /// hold. The lead is the braking distance: the machine coasts a little after the dial
            /// is pulled back, so handing over exactly at the mark always overshoots.
            /// </summary>
            public bool CoarsePhase;
            public float TempLead;
            public float PressureLead;

            /// <summary>
            /// Where warming should leave the readings, relative to the ideals. The temperature one
            /// is the number this whole round is about: it sets the temperature the shot starts
            /// from, and the shot then adds its own heat on top of that.
            /// </summary>
            public float PreheatTempOffset;
            public float PreheatPressureOffset;

        }

        /// <summary>
        /// The configuration. Every number in here was measured on the machine itself, one sweeps at a
        /// time, and the notes say what each is for - none of them are guesses that can be nudged
        /// without going back and measuring again.
        /// </summary>
        private static readonly Candidate Setup = new Candidate
        {
            // Warming runs in two parts. While a reading is further from its mark than its lead, that
            // dial is held wide open: a dial against its stop is the machine's own fastest heating
            // rate and no gain setting beats it. The loops then only trim the last stretch. The lead
            // must stay small - hand over too early and the loops, whose starting guess sits near the
            // holding value, crawl the remaining degrees.
            CoarsePhase = true,
            TempLead = 3f,
            PressureLead = 0.8f,

            // Warming parks both readings on the ideals rather than below them. Parking the pressure
            // lower costs the whole of the first second of the shot while the pump climbs to the
            // mark; parking it higher overshoots. The temperature behaves the same way.
            PreheatTempOffset = 0f,
            PreheatPressureOffset = 0f,

            // TempFf / PressureFf scale the loop's starting guess, which assumes a dial showing N
            // holds N of the reading. The real figures are about 0.70 of the reading for temperature
            // while warming and 0.45 while brewing, and 0.15 for pressure. Left at 1 the guess parks
            // both dials far too high, which is what used to make the shot overshoot.
            Preheat = new Gains
            {
                TempKp = 1.2f, TempKi = 2.0f, TempKd = 1.0f,
                PressureKp = 10f, PressureKi = 0.5f, PressureKd = 0.5f,
                TempFf = 0.70f, PressureFf = 0.15f,
            },
            Brew = new Gains
            {
                TempKp = 108f, TempKi = 117f, TempKd = 58f,
                PressureKp = 50f, PressureKi = 6f, PressureKd = 3f,
                TempFf = 0.45f, PressureFf = 0.15f,
            },
        };


        private Gains PreheatGains
        {
            get { return Setup.Preheat; }
        }

        private Gains BrewGains
        {
            get { return Setup.Brew; }
        }

        private Candidate Current
        {
            get { return Setup; }
        }


        private const float IntegralLimit = 200f;
        private const float DerivativeFilterSeconds = 0.15f;

        // =================================================================================
        // Stage targets
        // =================================================================================

        // Where each reading is parked before a shot starts now lives on the test case itself,
        // because this round is about walking that number.

        /// <summary>
        /// Hand-over window, deliberately tighter than the coarse-run lead and tighter than the
        /// tolerances above. When it was the same number the stage ended the moment a reading
        /// clipped the edge of the band, so it parked a full tolerance short of where it had been
        /// told to park and every shot began cold.
        /// </summary>
        private const float SettleTempBand = 0.8f;
        private const float SettlePressureBand = 0.25f;
        private const float PreheatMaxSeconds = 25f;

        /// <summary>How long a reading may sit perfectly still before the machine is called dead.</summary>
        private const float PreheatStallSeconds = 3f;

        /// <summary>How far from the mark a still reading has to be for that to mean anything.</summary>
        private const float PreheatStallError = 10f;

        /// <summary>
        /// A gap between two frames longer than this means the game was paused, throttled or
        /// unfocused. Game time cannot see that - it hands out small deltas throughout - so the
        /// clock is used instead.
        /// </summary>
        private const float PauseGapSeconds = 2f;

        /// <summary>
        /// How long both readings must stay inside their tolerances before warming counts as done.
        /// Without this the stage ends on the first frame that clips the edge of the band, which
        /// hands a cold machine straight to the shot.
        /// </summary>
        private const float InBandHoldSeconds = 0.4f;

        // =================================================================================
        // Runtime
        // =================================================================================

        private sealed class LoopState
        {
            public float Integral;
            public float PreviousError;
            public float Derivative;
            public bool HasPrevious;

            public void Reset()
            {
                Integral = 0f;
                PreviousError = 0f;
                Derivative = 0f;
                HasPrevious = false;
            }
        }

        private readonly LoopState _tempLoop = new LoopState();
        private readonly LoopState _pressureLoop = new LoopState();

        private bool _preheating;
        private bool _brewing;
        private bool _sawBrewing;
        private bool _stopped;
        private float _preheatElapsed;
        private bool _suspended;
        private bool _hasBrewT;
        private float _lastBrewT;
        private float _brewStep;
        private bool _reportedBadReading;
        private DateTime _lastFrameUtc = DateTime.MinValue;
        private DateTime _lastReadingChangeUtc = DateTime.MinValue;
        private float _lastSeenTemp;
        private float _lastSeenPressure;
        private float _tempDialClampSaved;
        private float _pressureDialClampSaved;
        private bool _clampSaved;

        private bool _coarseDone;
        private float _inBandSeconds;
        private int _runsCompleted;

        // Last finished cup, kept purely so the panel can show it while the next one runs.
        private bool _hasLastResult;
        private float _lastQuality, _lastTempScore, _lastPressureScore, _lastTimingScore;

        private float _brewTempAbsErr, _brewTempMaxErr, _brewPressureAbsErr, _brewPressureMaxErr;
        private int _brewSamples;

        public override void OnUpdate()
        {
            float dt = Time.deltaTime;

            // Real time, not game time: a game that has been throttled still hands out small deltas
            // throughout, so game time alone cannot tell that it stopped.
            DateTime now = DateTime.UtcNow;
            bool paused = _lastFrameUtc != DateTime.MinValue && (now - _lastFrameUtc).TotalSeconds > PauseGapSeconds;
            bool focused = Application.isFocused;
            _lastFrameUtc = now;

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

            // Standing off. While the game is in the background the dials are left holding the current
            // targets and nothing else is touched: left wide open the boiler runs away by the time the
            // player is back, shut down it goes cold, and either way the shot is wasted. Left holding,
            // a shot that was still good simply carries on where it left off.
            if (_preheating || _brewing)
            {
                if (!focused || paused)
                {
                    if (!_suspended)
                    {
                        _suspended = true;
                        LoggerInstance.Msg("[ESPRESSO] Game went to the background; holding the dials until you are back.");
                    }
                    // Deliberately does NOT touch the dials. Writing them while the game is in the
                    // background is one of the two things that differ between the build where the
                    // machine survived a trip to the background and the one where it did not, and
                    // this build exists to tell those two apart.
                    _lastSeenTemp = safe(machine.simTemperature);
                    _lastSeenPressure = safe(machine.simPressure);
                    _lastReadingChangeUtc = now;      // time spent standing off is not a stall
                    return;
                }

                if (_suspended)
                {
                    _suspended = false;
                }
            }
            else
            {
                _suspended = false;
            }

            // Remember whether the readings are still moving. A stopped machine reports exactly the
            // same state and the same zeros as a cold one, so this is the only honest tell.
            // The machine's own numbers going non-finite is the one failure that cannot be worked
            // around: it never recovers, and only a fresh game clears it. Say so plainly instead of
            // grinding away at a machine that is not there.
            if ((_preheating || _brewing) && !_reportedBadReading &&
                (IsBad(machine.simTemperature) || IsBad(machine.simPressure)))
            {
                _reportedBadReading = true;
                LoggerInstance.Warning("[ESPRESSO] The machine's readings have gone non-finite (temp=" +
                                       Raw(machine.simTemperature) + " pressure=" + Raw(machine.simPressure) +
                                       "). Nothing can be done with it until the game is restarted - this is " +
                                       "the machine's own state, not something the automation can clear.");
                AbandonRun();
                return;
            }

            float seenTemp = safe(machine.simTemperature);
            float seenPressure = safe(machine.simPressure);
            if (Math.Abs(seenTemp - _lastSeenTemp) > 0.5f || Math.Abs(seenPressure - _lastSeenPressure) > 0.05f)
            {
                _lastSeenTemp = seenTemp;
                _lastSeenPressure = seenPressure;
                _lastReadingChangeUtc = now;
            }

            if (_preheating)
            {
                RunPreheat(machine, state, dt);
                return;
            }

            if (_brewing)
            {
                RunBrew(machine, state, dt);
                return;
            }
        }

        // =================================================================================
        // Stage 1 - warming
        // =================================================================================

        /// <summary>
        /// Brings both readings to their parked positions, holds them there until they have
        /// settled, then starts the shot.
        /// </summary>
        private void RunPreheat(EspressoBrewingController machine, string state, float dt)
        {
            if (state == "Brewing")
            {
                // Something started the shot underneath us; go straight to holding it.
                _preheating = false;
                EnterBrewStage();
                return;
            }

            _preheatElapsed += dt;

            float tempTarget = safe(machine.idealTemperature) + Current.PreheatTempOffset;
            float pressureTarget = safe(machine.idealPressure) + Current.PreheatPressureOffset;
            float tempErr = tempTarget - safe(machine.simTemperature);
            float pressureErr = pressureTarget - safe(machine.simPressure);

            // Coarse run. While a reading is still further from its mark than its lead, that dial is
            // pinned wide open and the loop is kept out of it: the dial against its stop is the
            // machine's own fastest heating rate, and no gain setting can beat it. The loop then has
            // only the last stretch to trim, which is the part it is actually good at.
            bool tempCoarse = false;
            bool pressureCoarse = false;
            if (Current.CoarsePhase && !_coarseDone)
            {
                tempCoarse = tempErr > Current.TempLead;
                pressureCoarse = pressureErr > Current.PressureLead;
                if (!tempCoarse && !pressureCoarse)
                {
                    _coarseDone = true;
                    _tempLoop.Reset();
                    _pressureLoop.Reset();
                }
            }

            Gains pGains = PreheatGains;

            if (tempCoarse)
            {
                SetDialRaw(machine.temperatureDial, machine.temperatureDial != null ? machine.temperatureDial.maxOutputValue : 0f);
            }
            else
            {
                Control(machine.temperatureDial, tempTarget, safe(machine.simTemperature), safe(machine.tempMax),
                        pGains.TempKp, pGains.TempKi, pGains.TempKd, _tempLoop, dt, Ff(pGains.TempFf));
            }

            if (pressureCoarse)
            {
                SetDialRaw(machine.pressureDial, machine.pressureDial != null ? machine.pressureDial.maxOutputValue : 0f);
            }
            else
            {
                Control(machine.pressureDial, pressureTarget, safe(machine.simPressure), safe(machine.pressureMax),
                        pGains.PressureKp, pGains.PressureKi, pGains.PressureKd, _pressureLoop, dt, Ff(pGains.PressureFf));
            }


            // A reading that has stopped moving while still a long way from the mark means the
            // machine is no longer running. It must be a rolling window, not a "has it ever moved"
            // flag: the machine works for a while and then stops, and only the recent past says so.
            if (Math.Abs(tempErr) > PreheatStallError &&
                (DateTime.UtcNow - _lastReadingChangeUtc).TotalSeconds >= PreheatStallSeconds)
            {
                LoggerInstance.Warning("[ESPRESSO] The reading has sat at " + safe(machine.simTemperature).ToString("0.00") +
                                       " without moving, so the machine has stopped responding. Giving up without " +
                                       "brewing and handing the dials back - press F10 again when it is running.");
                AbandonRun();
                return;
            }

            bool onTarget = Math.Abs(tempErr) <= SettleTempBand && Math.Abs(pressureErr) <= SettlePressureBand;
            _inBandSeconds = onTarget ? _inBandSeconds + dt : 0f;

            // Running out of time is not a reason to brew. If warming could not reach the mark in
            // this long then something is wrong with the machine, and pressing the handle anyway
            // only ruins a cup.
            if (_preheatElapsed >= PreheatMaxSeconds)
            {
                LoggerInstance.Warning("[ESPRESSO] Warming did not settle within " + PreheatMaxSeconds +
                                       "s (stuck near " + safe(machine.simTemperature).ToString("0.00") +
                                       " of " + tempTarget.ToString("0.00") + "). Giving up without brewing.");
                AbandonRun();
                return;
            }

            if (onTarget && _inBandSeconds >= InBandHoldSeconds)
            {
                // Checked again here, not just when F10 was pressed: the cup can come out during the
                // warm-up, and pressing the handle then just wastes the heat.
                string gone = MissingSupplies(machine);
                if (gone.Length > 0)
                {
                    LoggerInstance.Warning("[ESPRESSO] " + gone + " went missing during the warm-up; stopping here " +
                                           "rather than brewing into nothing. Load it and press F10 again.");
                    AbandonRun();
                    return;
                }

                _preheating = false;
                EnterBrewStage();
                ClickBrewHandle(machine, true, "start");
            }
        }

        // =================================================================================
        // Stage 2 - brewing
        // =================================================================================

        private void RunBrew(EspressoBrewingController machine, string state, float dt)
        {
            if (state != "Brewing")
            {
                if (_sawBrewing)
                {
                    _brewing = false;

                    // If the automation did not press the handle off itself, then the machine ended
                    // the brew on its own - and the one way that happens is the cup being taken away.
                    // The lever is then certainly still down, so put it back. The missing supplies
                    // also make this the safe moment to risk a click: with nothing to brew into, a
                    // click that landed the wrong way cannot start a shot.
                    if (!_stopped && MissingSupplies(machine).Length > 0)
                    {
                        ResetBrewHandle(machine);
                    }
                    _stopped = true;

                    ReportResult(machine);
                    _runsCompleted++;

                    // Park both dials so the pressure the shot built up starts bleeding off at once,
                    // which is what makes the next shot warm up quickly. The cup is left exactly where
                    // it is - the drink belongs to the player and is never touched.
                    SetDialRaw(machine.temperatureDial, machine.temperatureDial != null ? machine.temperatureDial.minOutputValue : 0f);
                    SetDialRaw(machine.pressureDial, machine.pressureDial != null ? machine.pressureDial.minOutputValue : 0f);
                    RestoreDialCentre(machine);
                }
                return;
            }

            _sawBrewing = true;

            // The shot is worth nothing without its cup. The game usually ends the brew by itself when
            // the cup goes, but if it has not, stop it - and either way the lever must not be left
            // down.
            if (!_stopped && MissingSupplies(machine).Length > 0)
            {
                _stopped = true;
                LoggerInstance.Warning("[ESPRESSO] The cup or the grounds were taken away mid-shot; stopping the brew " +
                                       "and putting the handle back.");
                ClickBrewHandle(machine, false, "stop (supplies removed)");
                return;
            }

            // The same tell as in warming, and the same answer. A shot cannot be rescued once the
            // machine has stopped, so it is dropped rather than kept pressing at a dead handle.
            if (Math.Abs(safe(machine.idealTemperature) - safe(machine.simTemperature)) > PreheatStallError &&
                (DateTime.UtcNow - _lastReadingChangeUtc).TotalSeconds >= PreheatStallSeconds)
            {
                LoggerInstance.Warning("[ESPRESSO] The machine stopped responding part way through the shot. " +
                                       "Giving up on it - this cup is lost, press F10 when the machine is back.");
                AbandonRun();
                return;
            }

            ApplyStage(machine, BrewGains, 0f, 0f, dt);

            float tempErr = safe(machine.idealTemperature) - safe(machine.simTemperature);
            float pressureErr = safe(machine.idealPressure) - safe(machine.simPressure);
            _brewTempAbsErr += Math.Abs(tempErr);
            _brewPressureAbsErr += Math.Abs(pressureErr);
            if (Math.Abs(tempErr) > _brewTempMaxErr) _brewTempMaxErr = Math.Abs(tempErr);
            if (Math.Abs(pressureErr) > _brewPressureMaxErr) _brewPressureMaxErr = Math.Abs(pressureErr);
            _brewSamples++;


            // How far the brew clock moves each frame. The stop can only be asked for on a frame
            // boundary, so the best attainable is whichever boundary sits closest to the mark - and
            // that means asking half a frame EARLY rather than waiting until the clock has already
            // gone past it. Waiting always overshoots by up to a whole frame, and the score falls
            // away as the frame rate drops: at 41 fps a frame is 24ms, at 60 fps it is 17ms, and the
            // game's timing window is only 4 seconds wide.
            float brewT = safe(machine.BrewElapsedSeconds);
            if (_hasBrewT)
            {
                float step = brewT - _lastBrewT;
                if (step > 0f && step <= 0.5f)      // ignore pauses and stalled frames
                {
                    _brewStep = _brewStep <= 0f ? step : _brewStep * 0.75f + step * 0.25f;
                }
            }
            _lastBrewT = brewT;
            _hasBrewT = true;

            float lead = _brewStep > 0f ? Math.Min(_brewStep * 0.5f, 0.25f) : 0f;

            if (!_stopped && brewT >= safe(machine.idealBrewSeconds) - lead)
            {
                _stopped = true;
                float ideal = safe(machine.idealBrewSeconds);
                float atClick = safe(machine.BrewElapsedSeconds);

                ClickBrewHandle(machine, false, "stop");

                // Park the dials so the pressure the shot built up starts bleeding off straight
                // away; that is what lets the next warm-up be quick.
                SetDialRaw(machine.temperatureDial, machine.temperatureDial != null ? machine.temperatureDial.minOutputValue : 0f);
                SetDialRaw(machine.pressureDial, machine.pressureDial != null ? machine.pressureDial.minOutputValue : 0f);
            }
        }

        /// <summary>
        /// Clears the machine for the next shot WITHOUT throwing the coffee away.
        ///
        /// The finished cup is taken off the machine exactly as a player would take it - coffee and
        /// all, so it can still be drunk - and an empty spare is put in its place. An earlier
        /// version emptied the cup in place instead, which quietly destroyed the drink.
        /// </summary>
        /// <summary>Switches the loop over to the brewing gains and targets.</summary>
        private void EnterBrewStage()
        {
            _brewing = true;
            _sawBrewing = false;
            _stopped = false;
            _hasBrewT = false;
            _brewStep = 0f;

            // Nothing carries over between stages: the warming integral describes a machine the
            // shot has just changed, and carrying it in would skew the first seconds badly.
            _tempLoop.Reset();
            _pressureLoop.Reset();
        }

        private void BeginAssist()
        {
            EspressoBrewingController machine = FindMachine();
            if (machine == null)
            {
                LoggerInstance.Warning("[ESPRESSO] No espresso machine in this scene.");
                return;
            }

            string state = StateOf(machine);
            if (state == "Brewing")
            {
                LoggerInstance.Msg("[ESPRESSO] Already brewing; holding the dials.");
                return;
            }
            if (state != "Ready")
            {
                LoggerInstance.Warning("[ESPRESSO] Machine is " + state + " - load a coffee can and a cup first.");
                return;
            }

            string missing = MissingSupplies(machine);
            if (missing.Length > 0)
            {
                LoggerInstance.Warning("[ESPRESSO] The machine has no " + missing +
                                       " in it - load it and press F10 again.");
                return;
            }


            _preheating = true;
            _brewing = false;
            _sawBrewing = false;
            _stopped = false;
            _preheatElapsed = 0f;
            _brewTempAbsErr = _brewTempMaxErr = _brewPressureAbsErr = _brewPressureMaxErr = 0f;
            _brewSamples = 0;
            _tempLoop.Reset();
            _pressureLoop.Reset();

            float tempTarget = safe(machine.idealTemperature) + Current.PreheatTempOffset;
            float pressureTarget = safe(machine.idealPressure) + Current.PreheatPressureOffset;

            // Loading a cup resets both dials to the top of their travel, so warming would otherwise
            // open from the worst possible position. Park them at the bottom and let the stage drive
            // up from there.
            _coarseDone = false;
            _inBandSeconds = 0f;
            _lastSeenTemp = safe(machine.simTemperature);
            _lastSeenPressure = safe(machine.simPressure);
            _lastReadingChangeUtc = DateTime.UtcNow;

            // Driving a dial means writing its clamp centre, and the game uses that field to decide
            // how far the dial's output range may reach. It is remembered here so the machine is not
            // left with a value this mod happened to leave behind.
            _tempDialClampSaved = machine.temperatureDial != null ? safe(machine.temperatureDial.clampCenterValue) : 0f;
            _pressureDialClampSaved = machine.pressureDial != null ? safe(machine.pressureDial.clampCenterValue) : 0f;
            _clampSaved = true;
            SetDialRaw(machine.temperatureDial, machine.temperatureDial != null ? machine.temperatureDial.minOutputValue : 0f);
            SetDialRaw(machine.pressureDial, machine.pressureDial != null ? machine.pressureDial.minOutputValue : 0f);

        }

        // =================================================================================
        // The loop
        // =================================================================================

        /// <summary>
        /// Drives both dials for the current stage. The offsets shift the targets below the ideals;
        /// they are zero while brewing.
        /// </summary>
        private void ApplyStage(EspressoBrewingController machine, Gains gains,
                                float tempOffset, float pressureOffset, float dt)
        {
            if (dt <= 0f || dt > 0.25f)
            {
                dt = 0.02f;
            }

            float tempTarget = safe(machine.idealTemperature) + tempOffset;
            float pressureTarget = safe(machine.idealPressure) + pressureOffset;

            Control(machine.temperatureDial, tempTarget, safe(machine.simTemperature), safe(machine.tempMax),
                    gains.TempKp, gains.TempKi, gains.TempKd, _tempLoop, dt, Ff(gains.TempFf));
            Control(machine.pressureDial, pressureTarget, safe(machine.simPressure), safe(machine.pressureMax),
                    gains.PressureKp, gains.PressureKi, gains.PressureKd, _pressureLoop, dt, Ff(gains.PressureFf));
        }

        private static void Control(DialInteractable dial, float target, float actual, float readingMax,
                                    float kp, float ki, float kd, LoopState loop, float dt, float feedForward)
        {
            if (dial == null)
            {
                return;
            }

            float error = target - actual;

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

            float wanted = DialValueFor(dial, target, readingMax) * feedForward + correction * readingToDial;

            // If the reading has gone non-finite then so has everything derived from it. Drop the
            // loop's memory of it and leave the dial alone rather than write the poison through.
            if (IsBad(wanted) || IsBad(candidate))
            {
                loop.Reset();
                return;
            }
            float clamped = wanted;
            if (dial.minOutputValue < dial.maxOutputValue)
            {
                if (clamped < dial.minOutputValue) clamped = dial.minOutputValue;
                if (clamped > dial.maxOutputValue) clamped = dial.maxOutputValue;
            }

            // Anti-windup: while the handle is pinned against a stop the loop can do nothing more
            // in that direction, so the integral is left alone rather than run up.
            if (Math.Abs(clamped - wanted) < 0.0001f)
            {
                loop.Integral = candidate;
            }

            SetDialRaw(dial, clamped);
        }

        // =================================================================================
        // Dial plumbing
        // =================================================================================

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

            // A non-finite value must never reach a dial. The game compares the dial's value to its
            // limits, and every comparison against NaN is false, so a NaN written here leaves the
            // dial in a state where nothing - including the player - can move it again. Only a
            // freshly started game clears that.
            if (IsBad(output))
            {
                return;
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

        /// <summary>The handle position that corresponds to a reading, in the dial's own units.</summary>
        private static float DialValueFor(DialInteractable dial, float reading, float readingMax)
        {
            if (dial == null)
            {
                return 0f;
            }
            float mapped = readingMax > 0.0001f ? reading / readingMax : 0f;
            return mapped * dial.maxOutputValue;
        }

        // =================================================================================
        // Brew handle
        // =================================================================================

        /// <summary>
        /// Clicks the brew handle the way the game's own cursor would, then checks the machine and
        /// clicks again if nothing happened. The handle is a latching switch that can end up out of
        /// step with the machine, so the machine's own state is what counts, not the handle's.
        /// </summary>
        /// <summary>
        /// Presses the brew handle once, to throw it back off.
        /// <para>
        /// Deliberately unconditional, and deliberately not asked to check anything first. The handle
        /// carries no field that says where the lever is - isActive reads true whether it is up or
        /// down, because its position lives in an animation - so the only trustworthy record that it
        /// was left thrown is this mod's own. Call it only where the automation knows that is the
        /// case; it is a toggle, and a click in the wrong circumstances would throw it down instead.
        /// </para>
        /// </summary>
        private void ResetBrewHandle(EspressoBrewingController machine)
        {
            LookAtTarget handle = machine != null ? machine.brewButton : null;
            if (handle == null)
            {
                return;
            }

            try
            {
                Interactable target = handle.interactable;
                handle.UpdateHover(true, true);
                handle.HandleClickDownFromManager(target);
                handle.HandleClickUpFromManager(target);

                // It is a toggle, so check the click did not land the other way and start a shot.
                string after = StateOf(machine);
                if (after == "Brewing")
                {
                    LoggerInstance.Warning("[ESPRESSO] The handle reset landed the wrong way and started a brew; " +
                                           "putting it back again.");
                    ClickBrewHandle(machine, false, "undo the accidental start");
                }
            }
            catch (Exception e)
            {
                LoggerInstance.Warning("[ESPRESSO] could not put the brew handle back: " + e.Message);
            }
        }

        private void ClickBrewHandle(EspressoBrewingController machine, bool wantBrewing, string why)
        {
            LookAtTarget handle = machine.brewButton;
            if (handle == null)
            {
                LoggerInstance.Warning("[ESPRESSO] Brew handle not found; cannot " + why + ".");
                return;
            }

            string before = StateOf(machine);
            if ((before == "Brewing") == wantBrewing)
            {
                return;   // the machine is already where it needs to be
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
                    if ((after == "Brewing") == wantBrewing)
                    {
                        return;
                    }
                }

                LoggerInstance.Warning("[ESPRESSO] the handle would not " + why + " after three clicks (" + StateOf(machine) + ").");
            }
            catch (Exception e)
            {
                LoggerInstance.Error("[ESPRESSO] Could not click the brew handle: " + e);
            }
        }

        // =================================================================================
        // Reporting
        // =================================================================================

        /// <summary>
        /// Reads the finished cup's scores, remembers them for the panel, and writes one line to the
        /// log. This is the only thing the mod prints during normal use.
        /// </summary>
        private void ReportResult(EspressoBrewingController machine)
        {
            EspressoCup cup = machine._loadedCup;

            if (cup == null || !cup.IsInitialised)
            {
                LoggerInstance.Msg("[ESPRESSO] Shot finished, but there was no cup in the machine to read.");
                return;
            }

            _lastQuality = safe(cup.Quality);
            _lastTempScore = safe(cup.TemperatureScore);
            _lastPressureScore = safe(cup.PressureScore);
            _lastTimingScore = safe(cup.TimingScore);
            _hasLastResult = true;

            LoggerInstance.Msg("[ESPRESSO] Cup read: quality " + _lastQuality.ToString("0.0") +
                               "  (temperature " + _lastTempScore.ToString("0.0") +
                               ", pressure " + _lastPressureScore.ToString("0.0") +
                               ", timing " + _lastTimingScore.ToString("0.0") + ")");
        }

        // =================================================================================
        // Status panel - visible only while working a shot
        // =================================================================================

        public override void OnGUI()
        {
            try
            {
                if (!_preheating && !_brewing)
                {
                    return;
                }

                EspressoBrewingController machine = FindMachine();
                if (machine == null)
                {
                    return;
                }

                const float width = 390f;
                const float margin = 20f;
                const float lineHeight = 22f;
                const float rows = 6f;

                Rect box = new Rect(Screen.width - width - margin, margin, width, 42f + rows * lineHeight + 6f);
                GUI.Box(box, "\u5496\u5561\u673a\u52a9\u624b V1.1.1 (F10)");

                float lx = box.x + 10f;
                float lw = box.width - 20f;
                float ly = box.y + 24f;

                Line(lx, lw, ref ly, "\u9636\u6bb5\uff1a" + (_preheating ? "\u9884\u70ed\u4e2d" : "\u51b2\u716e\u4e2d") +
                                     "    \u5df2\u5b8c\u6210 " + _runsCompleted + " \u676f",
                     _preheating ? new Color(1f, 0.9f, 0.5f) : new Color(0.6f, 1f, 0.6f));

                // The target shown is the one the current stage is actually working to: warning
                // holds both readings on the ideals, brewing works to them directly.
                float tempTarget = safe(machine.idealTemperature) + (_preheating ? Current.PreheatTempOffset : 0f);
                float pressureTarget = safe(machine.idealPressure) + (_preheating ? Current.PreheatPressureOffset : 0f);

                float tempDiff = Math.Abs(safe(machine.simTemperature) - tempTarget);
                Line(lx, lw, ref ly, "\u6e29\u5ea6 " + safe(machine.simTemperature).ToString("0.00") +
                                     " / " + tempTarget.ToString("0.00") + "   \u8bef\u5dee " + tempDiff.ToString("0.00"),
                     tempDiff <= 1f ? new Color(0.55f, 1f, 0.55f) : new Color(1f, 0.85f, 0.5f));

                float pressureDiff = Math.Abs(safe(machine.simPressure) - pressureTarget);
                Line(lx, lw, ref ly, "\u538b\u529b " + safe(machine.simPressure).ToString("0.000") +
                                     " / " + pressureTarget.ToString("0.000") + "   \u8bef\u5dee " + pressureDiff.ToString("0.000"),
                     pressureDiff <= 0.2f ? new Color(0.55f, 1f, 0.55f) : new Color(1f, 0.85f, 0.5f));

                Line(lx, lw, ref ly, "\u65f6\u95f4 " + safe(machine.BrewElapsedSeconds).ToString("0.00") + "s / " +
                                     safe(machine.idealBrewSeconds).ToString("0.00") + "s", null);

                Line(lx, lw, ref ly, "\u8f6c\u76d8 \u6e29 " + DialText(machine.temperatureDial) + "  \u538b " + DialText(machine.pressureDial) +
                                     "   \u624b\u67c4 " + (machine.brewButton != null && machine.brewButton.isActive ? "ON" : "off"), null);

                if (_hasLastResult)
                {
                    Line(lx, lw, ref ly, "\u4e0a\u676f\u54c1\u8d28 " + _lastQuality.ToString("0.0") +
                                         "  (\u6e29 " + _lastTempScore.ToString("0.0") +
                                         " \u538b " + _lastPressureScore.ToString("0.0") +
                                         " \u65f6 " + _lastTimingScore.ToString("0.0") + ")",
                         new Color(0.7f, 1f, 0.7f));
                }
                else
                {
                    Line(lx, lw, ref ly, "\u4e0a\u676f\u54c1\u8d28 \u5c1a\u65e0", null);
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

        // =================================================================================
        // Helpers
        // =================================================================================

        private static string DialText(DialInteractable dial)
        {
            return dial == null ? "n/a" : dial.accumulatedValue.ToString("0.00");
        }

        /// <summary>
        /// Hands back a usable number. Non-finite values are reported as zero so the arithmetic
        /// downstream cannot spread them - but note that this <b>hides</b> them, so anything meant to
        /// diagnose a machine should print the raw value instead (see <see cref="Raw"/>).
        /// </summary>
        private static float safe(float value)
        {
            return IsBad(value) ? 0f : value;
        }

        /// <summary>True for the values that quietly poison everything they touch: NaN and the infinities.</summary>
        private static bool IsBad(float value)
        {
            return float.IsNaN(value) || float.IsInfinity(value);
        }

        /// <summary>Prints a float honestly, so a non-finite value reads as one instead of as zero.</summary>
        private static string Raw(float value)
        {
            if (float.IsNaN(value)) return "NaN";
            if (float.IsPositiveInfinity(value)) return "+Inf";
            if (float.IsNegativeInfinity(value)) return "-Inf";
            return value.ToString("0.000");
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

        /// <summary>
        /// The espresso machine that is actually running, or null.
        /// <para>
        /// FindObjectsOfTypeAll also hands back uninstantiated templates and objects belonging to
        /// scenes that are no longer loaded. Driving one of those does nothing at all - its readings
        /// stay at zero and its dials at whatever the asset was authored with - so a live, active
        /// object is the only thing worth returning.
        /// </para>
        /// </summary>
        private static EspressoBrewingController FindMachine()
        {
            try
            {
                foreach (EspressoBrewingController machine in Resources.FindObjectsOfTypeAll<EspressoBrewingController>())
                {
                    if (machine == null || machine.gameObject == null)
                    {
                        continue;
                    }
                    if (!machine.gameObject.scene.IsValid() || !machine.gameObject.scene.isLoaded)
                    {
                        continue;   // a template, or left over from a scene that has been unloaded
                    }
                    if (!machine.gameObject.activeInHierarchy)
                    {
                        continue;
                    }
                    return machine;
                }
            }
            catch
            {
                // Falls through to null.
            }
            return null;
        }

        /// <summary>
        /// Whatever the machine is missing before a shot is worth running, as text - empty when it
        /// has everything. The game calls itself Ready with either the cup or the grounds missing,
        /// and a shot brewed into an empty machine only burns the heat and the water.
        /// </summary>
        private static string MissingSupplies(EspressoBrewingController machine)
        {
            try
            {
                bool hasCup = machine != null && machine.cupSlot != null && machine.cupSlot.HasItem;
                bool hasGrounds = machine != null && machine.groundsSlot != null && machine.groundsSlot.HasItem;

                if (hasCup && hasGrounds)
                {
                    return "";
                }
                if (!hasCup && !hasGrounds)
                {
                    return "a cup and coffee grounds";
                }
                return hasCup ? "coffee grounds" : "a cup";
            }
            catch
            {
                return "";   // cannot tell - never block the player over that
            }
        }

        /// <summary>Stops whatever run is in progress, leaving the machine exactly as it is.</summary>
        private void AbandonRun()
        {
            _suspended = false;
            _reportedBadReading = false;
            _preheating = false;
            _brewing = false;
            _sawBrewing = false;
            _stopped = false;
            RestoreDialCentre(FindMachine());
        }

        /// <summary>
        /// Puts the remembered clamp centre back on both dials. Called whenever a run ends, however
        /// it ends, so the machine is handed back the way it was found.
        /// </summary>
        private void RestoreDialCentre(EspressoBrewingController machine)
        {
            if (!_clampSaved)
            {
                return;
            }
            _clampSaved = false;

            if (machine == null)
            {
                return;
            }
            try
            {
                if (machine.temperatureDial != null) machine.temperatureDial.clampCenterValue = _tempDialClampSaved;
                if (machine.pressureDial != null) machine.pressureDial.clampCenterValue = _pressureDialClampSaved;
            }
            catch
            {
                // Nothing here is worth an error.
            }
        }
    }
}
