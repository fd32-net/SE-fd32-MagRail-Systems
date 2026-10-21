using System;
using VRage.Game.Entity;
using VRage.ModAPI;
using VRageMath;

namespace MagRail
{
    // The look of a telescopic strut: its section models (round tubes along their Up from their origin) and their
    // lengths, which must match the models.
    // Three sections (the rail lift, blocks/RailLift.py: MagRail_Lift*): <prefix>Sleeve and <prefix>Middle of fixed
    // length, <prefix>Rod 1 m long and stretched over the rest.
    // Two sections (an engine's struts, from its model): <prefix>Sleeve of fixed length from the first point and
    // <prefix>Rod from inside the sleeve's end to the second, stretched over that (its length: the model's).
    public class StrutLook
    {
        public static readonly StrutLook Lift = Three("MagRail_Lift", 0.6f, 0.6f);

        public string Sleeve, Middle, Rod;      // Middle null: two sections
        public float SleeveLength, MiddleLength, RodLength;

        public static StrutLook Three(string prefix, float sleeveLength, float middleLength)
        {
            return new StrutLook
            {
                Sleeve = prefix + "Sleeve", Middle = prefix + "Middle", Rod = prefix + "Rod",
                SleeveLength = sleeveLength, MiddleLength = middleLength,
            };
        }

        public static StrutLook Two(string prefix, float sleeveLength, float rodLength)
        {
            return new StrutLook { Sleeve = prefix + "Sleeve", Rod = prefix + "Rod", SleeveLength = sleeveLength, RodLength = rodLength };
        }
    }

    // A telescopic strut between two points (the rail lift, an engine's struts), StrutLook. Three sections: a sleeve and
    // a middle section of fixed length and a rod that stretches over the rest, as the Coupling Device's pistons.
    // Render-only model entities, children of a grid like the pieces of a span on a moving grid (PieceEntities).
    // Client only.
    public class Strut
    {
        // Collapsed, the middle sticks out of the sleeve by Step; extended, it stays Overlap inside it, and the rod
        // Overlap inside the middle (two sections: inside the sleeve).
        const float Step = 0.05f, Overlap = 0.08f;

        readonly StrutLook look;
        readonly MyEntity parent;
        readonly MyEntity[] parts;
        bool added;

        public Strut(StrutLook look, MyEntity parent)
        {
            this.look = look;
            this.parent = parent;
            parts = look.Middle == null ? new[] { Spawn(look.Sleeve), Spawn(look.Rod) }
                : new[] { Spawn(look.Sleeve), Spawn(look.Middle), Spawn(look.Rod) };
        }

        public MyEntity Parent
        {
            get { return parent; }
        }

        // Placed at least once (its sections are in the world).
        public bool Placed
        {
            get { return added; }
        }

        // Sleeve from a, (the middle sliding out of it,) the rod to b; a and b in a frame that toParent places in the
        // parent grid (a block's local matrix).
        public void Place(Vector3 a, Vector3 b, Matrix toParent)
        {
            Vector3 delta = b - a;
            float len = delta.Length();
            Vector3 dir = len > 1e-4f ? delta / len : Vector3.Up;
            Vector3 side = Vector3.Cross(dir, Vector3.Right);
            if (side.LengthSquared() < 1e-6f)
                side = Vector3.Cross(dir, Vector3.Forward);
            side.Normalize();

            if (look.Middle == null)
            {
                Set(0, a, dir, side, 0, 1, toParent);
                float start = Math.Min(look.SleeveLength, len) - Overlap;
                Set(1, a, dir, side, start, Math.Max(len - start, 1e-3f) / look.RodLength, toParent);
                added = true;
                return;
            }
            float extra = len - look.SleeveLength - 2 * Step;
            float back = Math.Max(0, -extra);
            extra = Math.Max(0, extra);
            float end = look.SleeveLength - back;
            Set(0, a, dir, side, -back, 1, toParent);
            end += Step + Math.Min(extra, look.MiddleLength - Step - Overlap);
            Set(1, a, dir, side, end - look.MiddleLength, 1, toParent);
            float rodStart = Math.Min(end - Overlap, len - Overlap);
            Set(2, a, dir, side, rodStart, Math.Max(len - rodStart, 1e-3f), toParent);
            added = true;
        }

        // Section `index` from `from` along dir from origin, stretched along its Up by `scale`.
        void Set(int index, Vector3 origin, Vector3 dir, Vector3 side, float from, float scale, Matrix toParent)
        {
            Matrix local = Matrix.CreateScale(1, scale, 1) * Matrix.CreateWorld(origin + dir * from, side, dir) * toParent;
            MyEntity e = parts[index];
            if (!added)
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
            foreach (MyEntity e in parts)
                PieceEntities.Remove(e);
        }
    }
}
