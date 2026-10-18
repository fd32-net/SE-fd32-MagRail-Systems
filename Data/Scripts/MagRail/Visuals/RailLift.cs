using System;
using Sandbox.Game.Entities;
using Sandbox.ModAPI;
using VRage.Game.Entity;
using VRage.ModAPI;
using VRageMath;

namespace MagRail
{
    // The lift of a straight rail block (TrackStyle.Lifts; user's design 2026-10-16): the block's model has a base of
    // the Truss Pillar's end face with four pivots, its rail part an ear under each end with two pivots
    // (ModelSource/blocks/TrackBlocks.py). Four telescopic struts (Strut, models blocks/RailLift.py) run
    // from the base's pivots to the ears' pivots, placed again only when the rail moves. Client only; only near the
    // camera. The base fits the recess under the rail between its feet, so the rail goes down to MinHeight.
    // Free Rail: the base's upper half (subpart Turn, with its pivots) turns with the head's yaw; the head tilts and
    // banks about HeadPivot, the middle of its ear pivots.
    public class RailLift
    {
        // Lowest rail point over the bottom of the cell, m (user, 2026-10-16): the recess ceiling (0.11 under the rail
        // point) clears the base's pivots (centre 0.08 + 0.05).
        public const float MinHeight = 0.25f;

        // Pivot centres (TrackBlocks.py PIVOT_BASE / PIVOT_EAR) in SE coordinates (Blender x, z, -y): the base's in its
        // top front / back edges (block, from the bottom of the cell), the ears' in their bottoms, EarInset from the
        // rail end (rail part, from the rail point).
        const float PivotX = 0.15f, BasePivotY = 0.08f, BaseHalf = 0.25f, EarPivotY = -0.14f, EarInset = 0.102f;

        // The Free Rail head's tilt and bank centre in the rail part.
        public static readonly Vector3 HeadPivot = new Vector3(0, EarPivotY, 0);
        // Struts exist within this camera distance, m (removed beyond HideDistance).
        const double ShowDistance = 150, HideDistance = 170;

        readonly TrackBlockLogic logic;
        Strut[] struts;

        public RailLift(TrackBlockLogic logic)
        {
            this.logic = logic;
        }

        // The rail part moved (partLocal: its matrix in the block), or the regular check: struts near the camera,
        // following the rail. railHalf: half the rail part's length; turn: the base's turn (Free Rail), else identity.
        public void Update(Matrix partLocal, float railHalf, Matrix turn, bool moved)
        {
            if (MyAPIGateway.Utilities.IsDedicated)
                return;
            var camera = MyAPIGateway.Session.Camera;
            IMyEntity block = logic.Block;
            double dist = camera != null ? Vector3D.Distance(camera.WorldMatrix.Translation, block.WorldMatrix.Translation)
                : double.MaxValue;
            if (dist > (struts != null ? HideDistance : ShowDistance))
            {
                Remove();
                return;
            }
            MyEntity grid = (MyEntity)logic.Block.CubeGrid;
            if (struts != null && struts[0].Parent != grid)
                Remove();
            bool fresh = struts == null;
            if (fresh)
            {
                struts = new Strut[4];
                for (int i = 0; i < struts.Length; i++)
                    struts[i] = new Strut(StrutLook.Lift, grid);
            }
            if (fresh || moved)
                Place(partLocal, railHalf, turn);
        }

        void Place(Matrix partLocal, float railHalf, Matrix turn)
        {
            Matrix blockLocal = logic.Block.PositionComp.LocalMatrixRef;
            float bottom = -logic.Block.CubeGrid.GridSize / 2;
            int i = 0;
            for (int sz = -1; sz <= 1; sz += 2)
                for (int sx = -1; sx <= 1; sx += 2)
                {
                    Vector3 a = Vector3.Transform(new Vector3(sx * PivotX, 0, sz * BaseHalf), turn)
                        + new Vector3(0, bottom + BasePivotY, 0);
                    Vector3 b = Vector3.Transform(new Vector3(sx * PivotX, EarPivotY, sz * (railHalf - EarInset)), partLocal);
                    struts[i++].Place(a, b, blockLocal);
                }
        }

        public void Remove()
        {
            if (struts == null)
                return;
            foreach (Strut s in struts)
                s.Remove();
            struts = null;
        }
    }
}
