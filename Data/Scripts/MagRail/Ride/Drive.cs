using System;
using System.Collections.Generic;
using Sandbox.ModAPI;
using Sandbox.ModAPI.Interfaces;
using VRage.Game.ModAPI;
using VRageMath;

namespace MagRail
{
    public enum DriveState
    {
        Idle,
        Driving,
        Braking,        // Space with Braking on
        Parked,         // the grid's hand brake
        Dampers,        // inertia dampeners (Z) on and nothing pushes: the engines' thrust stops the train
        NoController,   // the engines are asked to push but no controller gives the direction
        Sideways,       // the controller faces across the rail
    }

    // The drive of one grid on the rail (part of its RailBody), ported from the old Maglev (MaglevDrive,
    // MaglevPower, MaglevParking). Driven like wheels from a cockpit, seat or remote control (W/S, Space brake,
    // the controller's "Control wheels", main cockpit) with the native suspension controls: Propulsion, Power,
    // Speed Limit, Propulsion override, Braking, Parking.
    // - Thrust is game-like, as a thruster's: along the rail at the centre of mass, so an engine at one end never
    //   turns the grid. An engine has no direction of its own: W pushes the way the controller faces along the
    //   rail; a controller facing across the rail (more than 60 degrees off) is an error and nothing pushes.
    // - The controller is looked up on the grids mechanically joined to this one (rotor, hinge, piston), as the
    //   game's wheel control reaches. Every grid of a train pushes itself with its own engines: couplers carry
    //   little.
    // - Power is spent on thrust only, in proportion to the force (full thrust = the engine's max power), from the
    //   engine's own grid network; the thrust is scaled by the share the network supplied. Holding the rail,
    //   coasting, Space and parking are free.
    // - Parking is a brake along the rail, not a lock: a share of each parked engine's force. A grid nearly
    //   standing with nothing pushing it is held by a weak brake (rest hold), so free wagons do not roll into a
    //   braked locomotive.
    // - Inertia dampeners (the vanilla Z of the controller, on a grid with a Train Control Unit): with nothing
    //   pushing, the engines push against the grid's motion as W/S would (Propulsion, Power, power drawn) and
    //   stop it and hold it, as a ship's dampeners use its thrusters; off, the train coasts. Every grid of the
    //   train reads the controller's flag, so the train stops together.
    // - The numbers are the defaults of DriveTuning (temporary: tuned from the cockpit terminal).
    public class Drive
    {
        const double Step = 1.0 / 60.0;
        // Shares of the engine's force (HoverEngineLogic.Force, PLAN 10.1): thrust at 100% Power, the most of the
        // Space brake, parking. The Space brake of an engine is BrakeFactor times its thrust (Power slider), up to
        // BrakeShare: Power 10% brakes with 20% of the force, 50% and more with 100% (user decision 2026-10-11).
        public const double ThrustShare = 1.0, BrakeShare = 1.0, ParkShare = 1.5, BrakeFactor = 2.0;
        // Controller forward vs rail: |cos| below this counts as sideways (60 degrees).
        const double AlignMin = 0.5;
        // Thrust fades out over the last m/s below the speed limit.
        const double SpeedLimitBand = 1.0;
        const int ControllerRefreshTicks = 30, HandBrakeRefreshTicks = 10;

        readonly IMyCubeGrid grid;
        static readonly List<IMyShipController> scratch = new List<IMyShipController>();
        static readonly List<double> forces = new List<double>();
        static readonly List<IMyCubeGrid> groupScratch = new List<IMyCubeGrid>();

        // Controller a player drives the grid from (or null) and the one whose facing sets the direction: the
        // pilot, else the main cockpit, else any controller that controls wheels (Propulsion override without a
        // pilot).
        IMyShipController pilot, reference;
        int controllerTick = -999;
        // The reference controller's grid has a Train Control Unit (else it has no dampeners to read).
        bool unit;
        DriveTuning tuning = DriveTuning.Default;
        bool handBrake;
        int handBrakeTick = -999;
        // This tick's input.
        double move;
        bool brakeKey;
        // This tick's brakes are the parking brake (else Space); ticks the Space brake has been on.
        bool parked;
        int brakeTicks;
        // Along-rail speed written last tick while braking: the difference to this tick's speed is what gravity,
        // the carrier's acceleration and couplers added in between, and the brake cancels it up front (never more
        // than its force: static friction, not a lock).
        bool brakeHistory;
        double brakeWrittenAlong;

        // Last tick's state (terminal info, /magrail log): thrust given along the rail (N, plus = pathForward),
        // brake force (N), the network's share of the thrust asked (1 = full).
        public DriveState State;
        public double Force, BrakeForce, PowerShare = 1;
        // The brake force was cut to the acceleration limit (DriveTuningHud).
        public bool BrakeCapped;

        public IMyShipController Reference
        {
            get { return reference; }
        }

        // Terminal info: the train has a controller but no Train Control Unit on its grid (Z does nothing).
        public bool NoControlUnit
        {
            get { return reference != null && !unit; }
        }

        public Drive(IMyCubeGrid grid)
        {
            this.grid = grid;
        }

        public void ReadInput(int tick)
        {
            RefreshController(tick);
            move = 0;
            brakeKey = false;
            if (pilot != null)
            {
                Vector3 m = pilot.MoveIndicator;
                move = MathHelper.Clamp(-m.Z, -1f, 1f);
                brakeKey = m.Y > 0.5f;
            }
            // Parked train: W/S pushes no grid of it (else the wagons push the parked locomotive along).
            if (move != 0 && tuning.ParkStopsDrive && reference != null && !reference.MarkedForClose && reference.HandBrake)
                move = 0;
        }

        // Native ShouldBrake: Braking on, and Space held or the engine parked.
        bool Braking(HoverEngineLogic e, int tick)
        {
            return e.Block.Brake && (brakeKey || (e.Block.IsParkingEnabled && HandBrake(tick)));
        }

        // Along-rail velocity change of the engines' thrust this tick. along: the along speed about to be written
        // (relative to the rail, sign of pathForward). Sets every holding engine's power demand.
        public double Thrust(List<HoverEngineLogic> held, Vector3D pathForward, double along, double mass, double ramp, int tick)
        {
            State = DriveState.Idle;
            Force = 0;
            PowerShare = 1;
            BrakeForce = 0;
            bool anyInput = false;
            for (int i = 0; i < held.Count; i++)
            {
                HoverEngineLogic e = held[i];
                if (Braking(e, tick))
                {
                    BrakeForce += e.Force * (brakeKey
                        ? Math.Min(tuning.BrakeFactor * tuning.ThrustShare * PowerSlider(e), tuning.BrakeShare)
                        : tuning.ParkShare);
                    continue;
                }
                anyInput |= Input(e) != 0;
            }
            parked = BrakeForce > 0 && !brakeKey;
            if (BrakeForce > 0)
                State = parked ? DriveState.Parked : DriveState.Braking;
            if (!anyInput)
                return BrakeForce <= 0 && Dampers() ? Damp(held, along, mass, ramp) : 0;
            if (reference == null || reference.MarkedForClose)
            {
                State = DriveState.NoController;
                return 0;
            }
            double facing = Vector3D.Dot(reference.WorldMatrix.Forward, pathForward);
            if (Math.Abs(facing) < AlignMin)
            {
                State = DriveState.Sideways;
                return 0;
            }
            double sign = facing > 0 ? 1 : -1;

            forces.Clear();
            double sum = 0;
            for (int i = 0; i < held.Count; i++)
            {
                HoverEngineLogic e = held[i];
                double force = 0;
                if (!Braking(e, tick))
                {
                    force = e.Force * tuning.ThrustShare * MathHelper.Clamp(Input(e), -1.0, 1.0) * sign;
                    // The speed limit only holds back thrust that speeds the grid up, never braking.
                    double speedWithThrust = along * Math.Sign(force);
                    double limit = SpeedLimit(e);
                    if (force != 0 && speedWithThrust > 0 && limit < double.MaxValue)
                        force *= MathHelper.Clamp((limit - speedWithThrust) / SpeedLimitBand, 0.0, 1.0);
                }
                forces.Add(force);
                sum += force;
            }
            double max = tuning.MaxThrustAccel * mass;
            double scale = mass > 0 && Math.Abs(sum) > max ? max / Math.Abs(sum) : 1;
            if (State == DriveState.Idle)
                State = DriveState.Driving;
            return Push(held, scale, mass, ramp);
        }

        // Inertia dampeners: every engine with Propulsion on pushes against the grid's motion along the rail as W/S
        // would (its Power slider, the same power and limit), only as hard as it takes to stop the grid this tick,
        // so a stopped train stays held on a slope (and draws power for it).
        double Damp(List<HoverEngineLogic> held, double along, double mass, double ramp)
        {
            State = DriveState.Dampers;
            forces.Clear();
            double sum = 0;
            for (int i = 0; i < held.Count; i++)
            {
                HoverEngineLogic e = held[i];
                double force = e.Block.Propulsion ? e.Force * tuning.ThrustShare * PowerSlider(e) : 0;
                forces.Add(force);
                sum += force;
            }
            double needed = Math.Min(mass * Math.Abs(along) / (Step * ramp), tuning.MaxThrustAccel * mass);
            double scale = sum > needed ? needed / sum : 1;
            return Push(held, -Math.Sign(along) * scale, mass, ramp);
        }

        // Gives forces[i] * scale through the engines: each asks power for its force and gives the share its
        // network supplied. Returns the along-rail velocity change.
        double Push(List<HoverEngineLogic> held, double scale, double mass, double ramp)
        {
            double wanted = 0;
            for (int i = 0; i < held.Count; i++)
            {
                HoverEngineLogic e = held[i];
                double force = forces[i] * scale;
                if (force == 0)
                    continue;
                e.PowerWanted += (float)(Math.Abs(force) / (e.Force * tuning.ThrustShare) * e.MaxPower);
                wanted += Math.Abs(force);
                Force += force * e.PowerSupply;
            }
            PowerShare = wanted > 0 ? Math.Abs(Force) / wanted : 1;
            return mass > 0 ? Force / mass * Step * ramp : 0;
        }

        // Along-rail velocity change of the brakes this tick: the engines braking (Space, parking) or, on a grid
        // nearly standing that nothing pushes, the rest hold. carriedAlong: the along speed as it arrived this tick
        // (relative to the rail, turned with it); along: the along speed about to be written; continuous: the body
        // wrote last tick too.
        public double Brake(double carriedAlong, double along, double mass, double ramp, bool idle, bool continuous)
        {
            double force = BrakeForce;
            bool space = force > 0 && !parked;
            brakeTicks = space ? brakeTicks + 1 : 0;
            if (space && tuning.BrakeRise > 0)
                force *= Math.Min(1.0, DriveTuning.BrakeRiseStart + (1 - DriveTuning.BrakeRiseStart) * brakeTicks * Step / tuning.BrakeRise);
            if (force <= 0 && idle && Math.Abs(carriedAlong) < tuning.RestSpeed)
                force = mass * tuning.RestAccel;
            BrakeCapped = false;
            if (force <= 0 || mass <= 0)
            {
                brakeHistory = false;
                return 0;
            }
            double max = mass * (parked ? tuning.MaxParkAccel : tuning.MaxBrakeAccel);
            BrakeCapped = force > max;
            force = Math.Min(force, max);
            if (space && tuning.SoftStop > 0)
                force *= MathHelper.Clamp(Math.Abs(along) / tuning.SoftStop, DriveTuning.SoftStopFloor, 1.0);
            BrakeForce = Math.Min(BrakeForce, force);
            double maxChange = force / mass * Step * ramp;
            double disturbance = brakeHistory && continuous && tuning.Disturbance
                ? MathHelper.Clamp(carriedAlong - brakeWrittenAlong, -maxChange, maxChange) : 0;
            double change = MathHelper.Clamp(-(along + disturbance), -maxChange, maxChange);
            brakeWrittenAlong = along + change;
            brakeHistory = true;
            return change;
        }

        public void Release()
        {
            brakeHistory = false;
            brakeTicks = 0;
            BrakeCapped = false;
            State = DriveState.Idle;
            Force = 0;
            BrakeForce = 0;
            PowerShare = 1;
        }

        // The controller's inertia dampeners: the vanilla flag of its grid (it exists only with a thruster there, the
        // Train Control Unit). Read every tick, with a pilot or not, as a ship's dampeners work.
        bool Dampers()
        {
            return reference != null && !reference.MarkedForClose && unit && reference.DampenersOverride;
        }

        // An engine's push, -1..1: Propulsion override, else W/S times its Power slider; nothing with Propulsion off.
        double Input(HoverEngineLogic e)
        {
            IMyMotorSuspension s = e.Block;
            if (!s.Propulsion)
                return 0;
            double input = s.PropulsionOverride;
            if (input == 0)
                input = move * PowerSlider(e);
            return input;
        }

        static double PowerSlider(HoverEngineLogic e)
        {
            return MathHelper.Clamp(e.Block.Power / 100.0, 0.0, 1.0);      // the API gives the slider's 0..100
        }

        static double SpeedLimit(HoverEngineLogic e)
        {
            // The game shows the Speed Limit slider only with this world option; without it the slider cannot be
            // set, so it does not limit either. Not in the mod API: read through the terminal property (km/h).
            if (MyAPIGateway.Session.SessionSettings == null || !MyAPIGateway.Session.SessionSettings.AdjustableMaxVehicleSpeed)
                return double.MaxValue;
            float kmh = e.Block.GetValueFloat("Speed Limit");
            return kmh >= 360f ? double.MaxValue : kmh / 3.6;
        }

        // ---- controllers ----

        void RefreshController(int tick)
        {
            if (tick - controllerTick < ControllerRefreshTicks && tick >= controllerTick
                && (pilot == null || (!pilot.MarkedForClose && pilot.IsUnderControl)))
                return;
            controllerTick = tick;
            IMyShipController main = null, any = null, piloted = null;
            CollectGroupControllers();
            for (int i = 0; i < scratch.Count; i++)
            {
                IMyShipController s = scratch[i];
                if (s.MarkedForClose || !s.IsFunctional || !s.CanControlShip || !s.ControlWheels)
                    continue;
                if (s.IsMainCockpit)
                    main = s;
                if (any == null || s.EntityId < any.EntityId)
                    any = s;
                if (s.IsUnderControl && (piloted == null || s.IsMainCockpit))
                    piloted = s;
            }
            scratch.Clear();
            // With a main cockpit, only it drives (as the game's own ship control).
            if (main != null && piloted != main)
                piloted = null;
            pilot = piloted;
            reference = piloted ?? main ?? any;
            unit = reference != null && TrainControlLogic.OnGrid(reference.CubeGrid);
            tuning = DriveTuning.For(reference);
        }

        // Every ship controller on the grids mechanically joined to this one (not over connectors).
        void CollectGroupControllers()
        {
            scratch.Clear();
            groupScratch.Clear();
            MyAPIGateway.GridGroups.GetGroup(grid, GridLinkTypeEnum.Mechanical, groupScratch);
            var terminal = MyAPIGateway.TerminalActionsHelper.GetTerminalSystemForGrid(grid);
            if (terminal != null)
                terminal.GetBlocksOfType(scratch, s => groupScratch.Contains(s.CubeGrid));
            groupScratch.Clear();
        }

        // The grid's hand brake (P, the cockpit's Hand brake), read through any ship controller on it: the game
        // keeps it in the grid's wheel system, which the mod API only shows there. Only the grid's own counts.
        bool HandBrake(int tick)
        {
            if (tick - handBrakeTick < HandBrakeRefreshTicks && tick >= handBrakeTick)
                return handBrake;
            handBrakeTick = tick;
            handBrake = false;
            scratch.Clear();
            var terminal = MyAPIGateway.TerminalActionsHelper.GetTerminalSystemForGrid(grid);
            if (terminal != null)
                terminal.GetBlocksOfType(scratch, c => c.CubeGrid == grid);
            for (int i = 0; i < scratch.Count && !handBrake; i++)
                handBrake = scratch[i].HandBrake;
            scratch.Clear();
            return handBrake;
        }
    }
}
