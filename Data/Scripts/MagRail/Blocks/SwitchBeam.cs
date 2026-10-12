using System;
using System.Collections.Generic;
using VRage.Game.Entity;
using VRageMath;

namespace MagRail
{
    // The Rail Switch's flexible rail, like a bending Transrapid switch: a beam of fixed length clamped at the end of
    // the entry module that bends into a circular arc, its end turned by the selected branch's angle. Rigid
    // segments (subparts "Beam0", "Beam1"... of the block, not of the rail part: the placement preview shows only the
    // block's own subparts) laid as the arc's chords; the last one is the end model turned round (its chamfer at the
    // beam's far end). The exit block's branch exits start where the beam ends, ExitSpace on
    // (ModelSource/blocks/TrackBlocks.py).
    // The arc is in the rail part's coordinates, so the beam follows the rail height.
    // Only a look: the branch switches at once, the beam bends after it at a fixed speed.
    public class SwitchBeam
    {
        const float Speed = 1.5f;           // about the sideways speed of the beam end, m/s
        // TrackBlocks.py: the entry module (MODULE), the gap and the centre exit after the beam (FLEX_GAP + MODULE).
        const float EntryModule = 0.625f, ExitSpace = 0.05f + 0.625f;

        // Branch port names by SwitchBranch.
        public static readonly string[] BranchPorts = { "branch_c", "branch_l", "branch_r" };

        // Per subtype, in the rail part's coordinates.
        public class Shape
        {
            public Vector3 Start, Along, Side, Up;              // beam start (end of the entry module), straight on
            public float Length;                                // of the beam
            public readonly float[] Turns = new float[3];       // the beam end's turn towards Side per branch (SwitchBranch)
        }

        // entry, branches: port matrices in the rail part (Forward = outwards).
        public static Shape ShapeOf(Matrix entry, Matrix[] branches)
        {
            Shape s = new Shape { Along = entry.Backward, Up = entry.Up };
            s.Start = entry.Translation + s.Along * EntryModule;
            s.Side = Vector3.Cross(s.Along, s.Up);
            s.Length = Vector3.Dot(branches[(int)SwitchBranch.Center].Translation - s.Start, s.Along) - ExitSpace;
            for (int i = 0; i < branches.Length; i++)
                s.Turns[i] = (float)Math.Atan2(Vector3.Dot(branches[i].Forward, s.Side), Vector3.Dot(branches[i].Forward, s.Along));
            return s;
        }

        readonly Shape shape;
        readonly List<MyEntitySubpart> segments = new List<MyEntitySubpart>();
        float bend, target;         // the beam end's turn, rad
        Matrix partLocal;           // rail part relative to the block

        // Placed at the branch at once (a new or reloaded block does not swing).
        public SwitchBeam(Shape shape, MyEntity block, Matrix partLocal, SwitchBranch branch)
        {
            this.shape = shape;
            MyEntitySubpart segment;
            while (block.TryGetSubpart("Beam" + segments.Count, out segment))
                segments.Add(segment);
            bend = target = shape.Turns[(int)branch];
            Place(partLocal);
        }

        public List<MyEntitySubpart> Segments
        {
            get { return segments; }
        }

        public bool Moving
        {
            get { return bend != target; }
        }

        public void Select(SwitchBranch branch)
        {
            target = shape.Turns[(int)branch];
        }

        // One tick of the movement; true when the beam has just arrived.
        public bool Step()
        {
            if (!Moving)
                return false;
            // The end moves sideways about Length * bend / 2.
            float step = 2 * Speed / shape.Length / 60;
            bend = Math.Abs(target - bend) <= step ? target : bend + Math.Sign(target - bend) * step;
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
            Vector3 a = shape.Start;
            for (int i = 0; i < n; i++)
            {
                Vector3 b = Point((float)(i + 1) / n);
                Vector3 dir = Vector3.Normalize(b - a);
                Matrix m = (i == n - 1 ? Matrix.CreateWorld(b, -dir, shape.Up) : Matrix.CreateWorld(a, dir, shape.Up)) * partLocal;
                if (!segments[i].Closed)
                    segments[i].PositionComp.SetLocalMatrix(ref m);
                a = b;
            }
        }

        // Point of the beam at fraction t of its length.
        Vector3 Point(float t)
        {
            float s = shape.Length * t;
            if (Math.Abs(bend) < 1e-4f)
                return shape.Start + shape.Along * s;
            float r = shape.Length / bend, phi = bend * t;
            return shape.Start + shape.Along * (r * (float)Math.Sin(phi)) + shape.Side * (r * (1 - (float)Math.Cos(phi)));
        }
    }
}
