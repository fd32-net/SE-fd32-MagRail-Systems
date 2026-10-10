using Sandbox.Engine.Physics;
using Sandbox.Game.Entities;
using VRage.Game.Components;
using VRage.Game.Entity;
using VRage.ModAPI;
using VRageMath;

namespace MagRail
{
    // Model entities for track pieces (mods cannot make meshes at runtime): not saved, not synced, not in
    // the game's pruning structure (tools and sensors do not see them). A piece of a span on a station
    // (or between stations) is a world entity; a piece of a span on a moving grid is a child of the grid
    // and moves with it, without collision: no collision layer meets characters but not ships, and a
    // body following a ship lags a frame behind and hits the ship itself (it stumbled in flight).
    // Vanilla doors avoid that with Havok filters and constraints, which mods cannot use.
    public static class PieceEntities
    {
        // Collision layer of static rail bodies, also of the blocks' rail parts: StaticCollisionLayer
        // (a number: a mod may not name MyPhysics).
        public const int StaticLayer = 13;

        static string modelFolder;

        public static void Load(string modPath)
        {
            modelFolder = modPath + "\\Models\\Cubes\\Large\\";
        }

        // Absolute path: a model entity is not a block and does not know the mod's folder.
        public static string Model(int lengthIndex)
        {
            return ModelOfLength(TrackLayout.Lengths[lengthIndex]);
        }

        public static string CollisionModel
        {
            get { return ModelOfLength(TrackLayout.CollisionLength); }
        }

        static string ModelOfLength(double length)
        {
            return modelFolder + "MagRail_Track_" + (int)(length * 1000 + 0.5) + ".mwm";
        }

        // The span's pieces are children of this grid (null: world entities).
        public static MyEntity ParentOf(Span s)
        {
            MyEntity grid = (MyEntity)s.Owner.Block.CubeGrid;
            return TrackNetwork.InGridFrame(s) && !s.Owner.Block.CubeGrid.IsStatic ? grid : null;
        }

        // m: piece matrix in the span's frame (frame: TrackNetwork.FrameMatrix; a child uses m alone).
        public static MyEntity Visual(string model, MatrixD m, MatrixD frame, MyEntity parent)
        {
            MyEntity e = new MyEntity();
            e.Init(null, model, null, null, null);
            Place(e, m, frame, parent);
            return e;
        }

        // Collision only (no render model), a static body in the world.
        public static MyEntity Collision(MatrixD m, MatrixD frame)
        {
            MyEntity e = new MyEntity();
            e.Init(null, null, null, null, CollisionModel);
            Place(e, m, frame, null);
            e.InitModelPhysics(RigidBodyFlag.RBF_STATIC, StaticLayer);
            return e;
        }

        static void Place(MyEntity e, MatrixD m, MatrixD frame, MyEntity parent)
        {
            e.Save = false;
            e.SyncFlag = false;
            e.IsPreview = true;
            e.Flags |= EntityFlags.IsNotGamePrunningStructureObject;
            if (parent == null)
            {
                e.WorldMatrix = m * frame;
                MyEntities.Add(e, true);
                return;
            }
            // Before AddChild: the grid then updates the piece whenever it moves.
            e.NeedsWorldMatrix = true;
            Matrix local = m;
            parent.Hierarchy.AddChildWithMatrix(e, ref local, true);
        }

        public static void Remove(MyEntity e)
        {
            if (e != null && !e.MarkedForClose)
                e.Close();
        }
    }
}
