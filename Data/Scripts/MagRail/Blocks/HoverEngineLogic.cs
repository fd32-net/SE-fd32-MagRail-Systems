using System;
using Sandbox.Common.ObjectBuilders;
using Sandbox.ModAPI;
using VRage.Game.Components;
using VRage.ModAPI;
using VRage.ObjectBuilders;
using VRageMath;

namespace MagRail
{
    // A hover engine: a wheel suspension without a wheel (RotorPart names no block group, so the game spawns
    // none). For now a sensor only: its guide point and the cursor on the rail under it; the physics comes
    // next. Numbers are the old Maglev's, so the ported physics keeps them.
    [MyEntityComponentDescriptor(typeof(MyObjectBuilder_MotorSuspension), false,
        "MagRail_HoverLarge", "MagRail_HoverSmall", "MagRail_HoverMini")]
    public class HoverEngineLogic : MyGameLogicComponent
    {
        // The rail is captured within this distance of the hover point, searched this far around the engine.
        public const double CaptureDistance = 4.0;
        const double SearchMargin = 12.0, MinSearch = 14.0;

        public IMyMotorSuspension Block { get; private set; }
        public readonly TrackCursor Cursor = new TrackCursor();
        bool registered;

        public override void Init(MyObjectBuilder_EntityBase objectBuilder)
        {
            Block = (IMyMotorSuspension)Entity;
            NeedsUpdate |= MyEntityUpdateEnum.BEFORE_NEXT_FRAME;
        }

        public override void UpdateOnceBeforeFrame()
        {
            try
            {
                // Projections and placement previews have no physics: no logic for them.
                if (Block.CubeGrid.Physics == null || MagRailSession.Instance == null)
                    return;
                MagRailSession.Instance.Engines.Add(this);
                registered = true;
            }
            catch (Exception e)
            {
                Log.Error("HoverEngineLogic.UpdateOnceBeforeFrame", e);
            }
        }

        public override void Close()
        {
            if (registered && MagRailSession.Instance != null)
                MagRailSession.Instance.Engines.Remove(this);
            registered = false;
        }

        // Height of the guide point over the rail point, m (a setting later).
        public double HoverHeight
        {
            get { return Block.CubeGrid.GridSizeEnum == VRage.Game.MyCubeSize.Small ? 1.2 : 2.5; }
        }

        public double SearchDistance
        {
            get { return Math.Max(MinSearch, HoverHeight + SearchMargin); }
        }

        // The point held over the rail: the block centre (offsets come with the settings).
        public Vector3D GuidePoint
        {
            get { return Block.WorldMatrix.Translation; }
        }
    }
}
