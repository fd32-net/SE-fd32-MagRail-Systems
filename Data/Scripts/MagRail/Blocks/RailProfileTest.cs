using Sandbox.ModAPI;
using VRage.Game;
using VRage.Game.Components;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRage.ObjectBuilders;
using VRageMath;

namespace MagRail
{
    // TEMPORARY design study: a plain block with the planned rail section (ModelSource/blocks/RailProfile.py)
    // to look at it in game. Only lights the glow strips. Remove together with the block definition.
    [MyEntityComponentDescriptor(typeof(MyObjectBuilder_CubeBlock), false, "MagRail_RailProfileTest")]
    public class RailProfileTest : MyGameLogicComponent
    {
        static readonly Color GlowColor = new Color(40, 190, 255);

        public override void Init(MyObjectBuilder_EntityBase objectBuilder)
        {
            NeedsUpdate |= MyEntityUpdateEnum.BEFORE_NEXT_FRAME | MyEntityUpdateEnum.EACH_100TH_FRAME;
        }

        public override void UpdateOnceBeforeFrame()
        {
            Light();
        }

        // Also every 100 ticks: a re-rendered block (damage, paint) gets its glow back.
        public override void UpdateAfterSimulation100()
        {
            Light();
        }

        void Light()
        {
            if (MyAPIGateway.Utilities.IsDedicated || Entity.Closed)
                return;
            ((IMyCubeBlock)Entity).SetEmissiveParts("Emissive", GlowColor, 1f);
        }
    }
}
