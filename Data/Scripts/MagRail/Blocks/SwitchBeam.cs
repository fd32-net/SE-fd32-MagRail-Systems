using System;
using System.Collections.Generic;
using Sandbox.ModAPI;
using VRage.Game.Entity;
using VRageMath;

namespace MagRail
{
    // The Rail Switch beam, like a bending Transrapid switch: rigid segments (subparts "Beam0", "Beam1"... of the
    // block, not of the rail part: the placement preview shows only the block's own subparts) laid as chords of a
    // circular arc that leaves the entry straight and ends at the selected branch. The arc is in the rail part's
    // coordinates, so the beam follows the rail height.
    // Only a look: the branch switches at once, the beam end follows sideways at a fixed speed. With the model's
    // branches (arcs over the whole switch length) the arc's end direction is the branch port's.
    // Lamps over the branch ends: material "Emissive<SwitchBranch>" of the rail part; green = selected, yellow =
    // the beam is still moving there, red = closed, off while the switch is damaged.
    public class SwitchBeam
    {
        const float Speed = 1.5f;           // sideways speed of the beam end, m/s
        static readonly Color OpenColor = new Color(40, 230, 70), MovingColor = new Color(255, 200, 40), ClosedColor = new Color(255, 40, 30);

        // Branch port names by SwitchBranch.
        public static readonly string[] BranchPorts = { "branch_c", "branch_l", "branch_r" };

        // Per subtype, in the rail part's coordinates: from the entry port along the switch.
        public class Shape
        {
            public Vector3 Start, Along, Side, Up;
            public float Length;                                // along the switch to the branch ends
            public readonly float[] Offsets = new float[3];     // sideways offset of each branch end (SwitchBranch)
        }

        // entry, branches: port matrices in the rail part (Forward = outwards).
        public static Shape ShapeOf(Matrix entry, Matrix[] branches)
        {
            Shape s = new Shape { Start = entry.Translation, Along = entry.Backward, Up = entry.Up };
            s.Side = Vector3.Cross(s.Along, s.Up);
            s.Length = Vector3.Dot(branches[0].Translation - s.Start, s.Along);
            for (int i = 0; i < branches.Length; i++)
                s.Offsets[i] = Vector3.Dot(branches[i].Translation - s.Start, s.Side);
            return s;
        }

        readonly Shape shape;
        readonly MyEntitySubpart part;
        readonly List<MyEntitySubpart> segments = new List<MyEntitySubpart>();
        float offset, target;
        SwitchBranch branch;
        Matrix partLocal;           // rail part relative to the block

        // Placed at the branch at once (a new or reloaded block does not swing).
        public SwitchBeam(Shape shape, MyEntity block, MyEntitySubpart part, Matrix partLocal, SwitchBranch branch)
        {
            this.shape = shape;
            this.part = part;
            this.branch = branch;
            MyEntitySubpart segment;
            while (block.TryGetSubpart("Beam" + segments.Count, out segment))
                segments.Add(segment);
            offset = target = shape.Offsets[(int)branch];
            Place(partLocal);
        }

        public List<MyEntitySubpart> Segments
        {
            get { return segments; }
        }

        public bool Moving
        {
            get { return offset != target; }
        }

        public void Select(SwitchBranch branch)
        {
            this.branch = branch;
            target = shape.Offsets[(int)branch];
        }

        // Cheap (one render message per lamp): also sent every 100 ticks, so a damaged or re-rendered part
        // gets its colours back.
        public void ShowLights(bool working)
        {
            if (MyAPIGateway.Utilities.IsDedicated || part.Closed)
                return;
            for (int i = 0; i < shape.Offsets.Length; i++)
            {
                Color color = (int)branch != i ? ClosedColor : Moving ? MovingColor : OpenColor;
                part.SetEmissiveParts("Emissive" + i, color, working ? 1 : 0);
            }
        }

        // One tick of the movement; true when the beam has just arrived.
        public bool Step()
        {
            if (!Moving)
                return false;
            const float step = Speed / 60;
            offset = Math.Abs(target - offset) <= step ? target : offset + Math.Sign(target - offset) * step;
            Place();
            return !Moving;
        }

        // After the rail part moved.
        public void Place(Matrix partLocal)
        {
            this.partLocal = partLocal;
            Place();
        }

        void Place()
        {
            int n = segments.Count;
            // The chord to the end makes half the arc's turn with the start direction.
            float turn = 2 * (float)Math.Atan2(offset, shape.Length);
            Vector3 a = Point(0, turn);
            for (int i = 0; i < n; i++)
            {
                Vector3 b = Point((float)(i + 1) / n, turn);
                Matrix m = Matrix.CreateWorld(a, Vector3.Normalize(b - a), shape.Up) * partLocal;
                if (!segments[i].Closed)
                    segments[i].PositionComp.SetLocalMatrix(ref m);
                a = b;
            }
        }

        // Point of the arc at fraction t of its turn.
        Vector3 Point(float t, float turn)
        {
            if (Math.Abs(turn) < 1e-4f)
                return shape.Start + shape.Along * (shape.Length * t) + shape.Side * (offset * t);
            float r = shape.Length / (float)Math.Sin(turn), phi = turn * t;
            return shape.Start + shape.Along * (r * (float)Math.Sin(phi)) + shape.Side * (r * (1 - (float)Math.Cos(phi)));
        }
    }
}
