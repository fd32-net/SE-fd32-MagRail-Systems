using System;
using System.Collections.Generic;
using ProtoBuf;

namespace MagRail
{
    // A span in rail mode that is still being built (survival, PLAN 10.8): how far each of its weld segments
    // (TrackLayout.Segments, the stretches between the copper's gaps) is welded, 0..1: construction stage 0 (nothing in
    // it yet: the bottom of the frame, already walked on), then 1..3 (Stage), finished from TrackStyle.Working on (all its Rail
    // components but one, as a block over its working line: the finished rail, carries trains), welded on to 1. Stored
    // run-length: a long span is mostly runs of 0 and 1. No runs at all: every segment at 0 (as switched to rail; the segment count is the layout's).
    // Stock: components in the span not welded in yet (a block's construction stockpile: welding loads all the span
    // still needs at once, grinding leaves here what the inventory has no room for).
    // Damaged / Lost: integrity damage took off segments (SpanDamage), as a share of the segment, by segment (ascending).
    // As on a vanilla block the build level (the stage shown) stays until a welder or grinder touches the segment: then
    // it drops to what is left (SpanWelding.Collapse). The segment works by what is left (done - lost: under
    // TrackStyle.Working no trains); finished as built but out of service it smokes.
    // A span whose segments are all finished and with nothing in stock keeps no SpanWeld (SpanRecord.Weld null = built).
    [ProtoContract]
    public class SpanWeld
    {
        [ProtoMember(1)] public int[] Runs;         // run lengths, segments
        [ProtoMember(2)] public float[] Values;     // by Runs
        [ProtoMember(3)] public int Stock;
        [ProtoMember(4)] public int[] Damaged;
        [ProtoMember(5)] public float[] Lost;       // by Damaged

        // Construction stage of a segment welded this far: 0 (nothing welded in), 1..3, 4 finished (from `working` on,
        // TrackStyle.Working).
        public const int Finished = 4;

        public static int Stage(float done, float working)
        {
            if (done <= 0)
                return 0;
            if (done >= working)
                return Finished;
            return Math.Min(3, 1 + (int)(done / working * 3));
        }

        // Every segment's progress for a span of `segments` segments (a stored count that differs: the rest at 0).
        public float[] Expand(int segments)
        {
            float[] done = new float[segments];
            if (Runs == null || Values == null)
                return done;
            int at = 0;
            for (int r = 0; r < Runs.Length && r < Values.Length; r++)
                for (int k = 0; k < Runs[r] && at < segments; k++)
                    done[at++] = Values[r];
            return done;
        }

        // Every segment's lost integrity for a span of `segments` segments.
        public float[] ExpandLost(int segments)
        {
            float[] lost = new float[segments];
            if (Damaged != null && Lost != null)
                for (int i = 0; i < Damaged.Length && i < Lost.Length; i++)
                    if (Damaged[i] >= 0 && Damaged[i] < segments)
                        lost[Damaged[i]] = Lost[i];
            return lost;
        }

        // Every stored segment finished (TrackStyle.Working): the span is laid as a built one (TrackNetwork.LayoutOf).
        public bool AllFinished(float working)
        {
            if (Runs == null || Values == null || Runs.Length == 0)
                return false;
            for (int r = 0; r < Values.Length; r++)
                if (Stage(Values[r], working) != Finished)
                    return false;
            return true;
        }

        // The stored form of `done`, `stock` and `lost` (null: none); null when every segment is welded full, undamaged,
        // with nothing in stock (built).
        public static SpanWeld Pack(float[] done, int stock, float[] lost = null)
        {
            List<int> runs = new List<int>();
            List<float> values = new List<float>();
            bool built = true;
            for (int i = 0; i < done.Length; i++)
            {
                float v = Math.Max(0, Math.Min(1, done[i]));
                built &= v >= 1;
                if (values.Count > 0 && values[values.Count - 1] == v)
                    runs[runs.Count - 1]++;
                else
                {
                    runs.Add(1);
                    values.Add(v);
                }
            }
            List<int> damaged = null;
            List<float> lostList = null;
            if (lost != null)
                for (int i = 0; i < lost.Length && i < done.Length; i++)
                    if (lost[i] > 0)
                    {
                        if (damaged == null)
                        {
                            damaged = new List<int>();
                            lostList = new List<float>();
                        }
                        damaged.Add(i);
                        lostList.Add(lost[i]);
                    }
            if (built && stock <= 0 && damaged == null)
                return null;
            return new SpanWeld
            {
                Runs = runs.ToArray(), Values = values.ToArray(), Stock = Math.Max(0, stock),
                Damaged = damaged != null ? damaged.ToArray() : null,
                Lost = lostList != null ? lostList.ToArray() : null,
            };
        }

        // Nothing welded anywhere and nothing in stock: the span may still go back to prototype.
        public bool Empty
        {
            get
            {
                if (Stock > 0)
                    return false;
                if (Values != null)
                    for (int i = 0; i < Values.Length; i++)
                        if (Values[i] > 0)
                            return false;
                return true;
            }
        }
    }
}
