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
    // (ModelSource/blocks/TrackBlocks.py). Four telescopic struts run from the base's pivots to the ears' pivots,
    // drawn here: render-only model entities (blocks/RailLift.py), children of the grid like the pieces of a span on a
    // moving grid (PieceEntities), placed again only when the rail moves. A strut is a sleeve and a middle section of
    // fixed length and a rod that stretches over the rest (as the Coupling Device's pistons). Client only; only near
    // the camera. The base fits the recess under the rail between its feet, so the rail goes down to MinHeight.
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
        // blocks/RailLift.py: section lengths; the rod model is 1 m long and stretched.
        const float SleeveLength = 0.6f, MiddleLength = 0.6f;
        // Collapsed, the middle sticks out of the sleeve by Step; extended, it stays Overlap inside it, and the rod
        // Overlap inside the middle.
        const float Step = 0.05f, Overlap = 0.08f;
        // Struts exist within this camera distance, m (removed beyond HideDistance).
        const double ShowDistance = 150, HideDistance = 170;

        static readonly string[] models = { "MagRail_LiftSleeve", "MagRail_LiftMiddle", "MagRail_LiftRod" };

        readonly TrackBlockLogic logic;
        // Per strut: sleeve, middle, rod.
        MyEntity[] parts;
        MyEntity parent;

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
            if (dist > (parts != null ? HideDistance : ShowDistance))
            {
                Remove();
                return;
            }
            MyEntity grid = (MyEntity)logic.Block.CubeGrid;
            if (parts != null && parent != grid)
                Remove();
            bool fresh = parts == null;
            if (fresh)
            {
                parent = grid;
                parts = new MyEntity[12];
                for (int i = 0; i < parts.Length; i++)
                    parts[i] = Spawn(models[i % 3]);
            }
            if (fresh || moved)
                Place(partLocal, railHalf, turn, fresh);
        }

        void Place(Matrix partLocal, float railHalf, Matrix turn, bool fresh)
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
                    Strut(i, a, b, blockLocal, fresh);
                    i += 3;
                }
        }

        // Sleeve from a, the middle sliding out of it, the rod to b (as CouplingParts.Place, three sections).
        void Strut(int first, Vector3 a, Vector3 b, Matrix blockLocal, bool fresh)
        {
            Vector3 delta = b - a;
            float len = delta.Length();
            Vector3 dir = len > 1e-4f ? delta / len : Vector3.Up;
            Vector3 side = Vector3.Cross(dir, Vector3.Right);
            if (side.LengthSquared() < 1e-6f)
                side = Vector3.Cross(dir, Vector3.Forward);
            side.Normalize();

            float extra = len - SleeveLength - 2 * Step;
            float back = Math.Max(0, -extra);
            extra = Math.Max(0, extra);
            float end = SleeveLength - back;
            Set(first, a, dir, side, -back, 1, blockLocal, fresh);
            end += Step + Math.Min(extra, MiddleLength - Step - Overlap);
            Set(first + 1, a, dir, side, end - MiddleLength, 1, blockLocal, fresh);
            float rodStart = Math.Min(end - Overlap, len - Overlap);
            Set(first + 2, a, dir, side, rodStart, Math.Max(len - rodStart, 1e-3f), blockLocal, fresh);
        }

        // Section `index` from `from` along dir from origin (block coordinates), stretched along its Up by `scale`.
        void Set(int index, Vector3 origin, Vector3 dir, Vector3 side, float from, float scale, Matrix blockLocal, bool fresh)
        {
            Matrix local = Matrix.CreateScale(1, scale, 1) * Matrix.CreateWorld(origin + dir * from, side, dir) * blockLocal;
            MyEntity e = parts[index];
            if (fresh)
                parent.Hierarchy.AddChildWithMatrix(e, ref local, true);
            else
                e.PositionComp.SetLocalMatrix(ref local);
        }

        static MyEntity Spawn(string model)
        {
            MyEntity e = new MyEntity();
            e.Init(null, PieceEntities.Model(model), null, null, null);
            e.Save = false;
            e.SyncFlag = false;
            e.IsPreview = true;
            e.Flags |= EntityFlags.IsNotGamePrunningStructureObject;
            e.NeedsWorldMatrix = true;
            return e;
        }

        public void Remove()
        {
            if (parts == null)
                return;
            foreach (MyEntity e in parts)
                PieceEntities.Remove(e);
            parts = null;
            parent = null;
        }
    }
}
