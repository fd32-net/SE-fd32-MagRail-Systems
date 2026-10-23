using System;
using System.Collections.Generic;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Definitions;
using Sandbox.ModAPI;
using VRage.Game.Components;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRage.ObjectBuilders;
using VRageMath;

namespace MagRail
{
    // Vanilla ship welders and grinders (any subtype, other mods' too): registered for span welding (SpanWelding), which
    // works their detector sphere on spans itself (the tools see only blocks). The block itself is left as it is.
    [MyEntityComponentDescriptor(typeof(MyObjectBuilder_ShipWelder), false)]
    public class ShipWelderLogic : ShipToolLogic
    {
        public override bool Welder { get { return true; } }
    }

    [MyEntityComponentDescriptor(typeof(MyObjectBuilder_ShipGrinder), false)]
    public class ShipGrinderLogic : ShipToolLogic
    {
        public override bool Welder { get { return false; } }
    }

    public abstract class ShipToolLogic : MyGameLogicComponent
    {
        static readonly Dictionary<string, IMyModelDummy> dummies = new Dictionary<string, IMyModelDummy>();

        public IMyShipToolBase Block { get; private set; }
        public abstract bool Welder { get; }
        // Server: when this welder last pulled Rail components over the conveyors (SpanWelding).
        public int PulledAt = int.MinValue / 2;
        // Client: sparks and sound where it works on a span.
        public readonly ToolEffects Effects = new ToolEffects();
        bool registered, sphereKnown;
        BoundingSphereD sphere;     // in the grid's coordinates

        public override void Init(MyObjectBuilder_EntityBase objectBuilder)
        {
            Block = (IMyShipToolBase)Entity;
            NeedsUpdate |= MyEntityUpdateEnum.BEFORE_NEXT_FRAME;
        }

        public override void UpdateOnceBeforeFrame()
        {
            try
            {
                if (Block.CubeGrid.Physics == null)
                    return;
                SpanWelding.ShipTools.Add(this);
                registered = true;
            }
            catch (Exception e)
            {
                Log.Error("ShipToolLogic.UpdateOnceBeforeFrame", e);
            }
        }

        public override void Close()
        {
            if (registered)
                SpanWelding.ShipTools.Remove(this);
            registered = false;
            Effects.Stop();
        }

        // Working: on (or shot from a cockpit), built, powered (the tool's own activation).
        public bool Working
        {
            get { return Block.IsActivated && Block.IsFunctional; }
        }

        // The tool's detector sphere in the world, as the game's (MyShipToolBase.LoadDummies: the model's
        // "detector_shiptool" dummy, SensorOffset along its Forward, SensorRadius). False: no such dummy (the tool
        // works on nothing either).
        public bool Sphere(out BoundingSphereD world)
        {
            world = new BoundingSphereD();
            if (!sphereKnown)
            {
                MyShipToolDefinition def = Block.SlimBlock.BlockDefinition as MyShipToolDefinition;
                if (def == null)
                    return false;
                dummies.Clear();
                Block.Model.GetDummies(dummies);
                foreach (KeyValuePair<string, IMyModelDummy> pair in dummies)
                    if (pair.Key.ToLowerInvariant().Contains("detector_shiptool"))
                    {
                        Matrix m = pair.Value.Matrix * Block.LocalMatrix;
                        sphere = new BoundingSphereD(m.Translation + m.Forward * def.SensorOffset, def.SensorRadius);
                        sphereKnown = true;
                        break;
                    }
                dummies.Clear();
                if (!sphereKnown)
                    return false;
            }
            world = new BoundingSphereD(Vector3D.Transform(sphere.Center, Block.CubeGrid.WorldMatrix), sphere.Radius);
            return true;
        }
    }
}
