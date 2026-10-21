using System;
using Sandbox.Definitions;
using Sandbox.Engine.Physics;
using Sandbox.Game.Entities;
using VRage.Game;
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

        // The rail's copper strip (material "Emissive" of the piece models and the blocks' rail parts,
        // ModelSource/rail_profile.py; "MagRail_Copper" the striped one on trial): its colour and glow.
        // Default: dim neutral white, a background glow (line colours come later with the Rail Painter).
        public static readonly Color CopperColor = Color.White;
        const float CopperDim = 0.4f;

        public static void LightCopper(MyEntity e)
        {
            e.SetEmissiveParts("Emissive", CopperColor, CopperDim);
            e.SetEmissiveParts("MagRail_Copper", CopperColor, CopperDim);
        }

        static string modelFolder;

        public static void Load(string modPath)
        {
            modelFolder = modPath + "\\Models\\Cubes\\Large\\";
        }

        // Absolute path: a model entity is not a block and does not know the mod's folder.
        public static string Model(string name)
        {
            return modelFolder + name + ".mwm";
        }

        // Path in the mod's folder (FileExistsInModLocation).
        public static string ModelFile(string name)
        {
            return "Models\\Cubes\\Large\\" + name + ".mwm";
        }

        // The span's pieces are children of this grid (null: world entities).
        public static MyEntity ParentOf(Span s)
        {
            MyEntity grid = (MyEntity)s.Owner.Block.CubeGrid;
            return TrackNetwork.InGridFrame(s) && !s.Owner.Block.CubeGrid.IsStatic ? grid : null;
        }

        // m: piece matrix in the span's frame (frame: TrackNetwork.FrameMatrix; a child uses m alone);
        // color, skin: colour mask HSV and armour skin, as on a block.
        public static MyEntity Visual(string model, MatrixD m, MatrixD frame, MyEntity parent, Vector3 color, string skin)
        {
            MyEntity e = new MyEntity();
            e.Init(null, model, null, null, null);
            e.Render.EnableColorMaskHsv = true;
            Paint(e, color, skin, false);
            Place(e, m, frame, parent);
            // After Place: the render object exists once the entity is in the scene.
            LightCopper(e);
            return e;
        }

        // Collision only (no render model), a static body in the world; model: name (TrackLayout.CollisionModel).
        public static MyEntity Collision(string model, MatrixD m, MatrixD frame)
        {
            MyEntity e = new MyEntity();
            e.Init(null, null, null, null, Model(model));
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

        // Like MyCubeBlock.UpdateSkin: the skin's texture changes, and its fixed colour if it has one (gold...).
        // A new skin shows once the render objects are made again (redraw).
        public static void Paint(MyEntity e, Vector3 color, string skin, bool redraw)
        {
            MyDefinitionManager.MyAssetModifiers skinRender = default(MyDefinitionManager.MyAssetModifiers);
            if (!string.IsNullOrEmpty(skin))
            {
                skinRender = MyDefinitionManager.Static.GetAssetModifierDefinitionForRender(skin);
                MyAssetModifierDefinition def = MyDefinitionManager.Static.GetAssetModifierDefinition(
                    new MyDefinitionId(typeof(MyObjectBuilder_AssetModifierDefinition), skin));
                if (def != null && def.DefaultColor.HasValue)
                    color = def.DefaultColor.Value.ColorToHSVDX11();
            }
            e.Render.TextureChanges = skinRender.SkinTextureChanges;
            e.Render.MetalnessColorable = skinRender.MetalnessColorable;
            e.Render.ColorMaskHsv = color;
            if (redraw && e.InScene)
            {
                e.Render.RemoveRenderObjects();
                e.Render.AddRenderObjects();
            }
        }

        public static void Remove(MyEntity e)
        {
            if (e != null && !e.MarkedForClose)
                e.Close();
        }
    }
}
