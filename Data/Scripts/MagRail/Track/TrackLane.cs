namespace MagRail
{
    // The rail inside a track block from one of its ports to another: straight on a rail block, from the
    // switch entry to each branch (all straight: the branches share one point). Built from the port dummies in the block's
    // coordinates, so it moves with the block; the curve follows the rail part (height, head).
    public class TrackLane
    {
        public readonly TrackBlockLogic Block;
        public readonly int From, To;       // port indexes: S = 0 at From, S = Length at To
        SpanCurve curve;
        int curveRevision = -1;

        public TrackLane(TrackBlockLogic block, int from, int to)
        {
            Block = block;
            From = from;
            To = to;
        }

        // In the block's coordinates.
        public SpanCurve Curve
        {
            get
            {
                if (curve == null || curveRevision != Block.Revision)
                {
                    curve = SpanCurve.Lane(Block.PortBlockLocal(From), Block.PortBlockLocal(To));
                    curveRevision = Block.Revision;
                }
                return curve;
            }
        }

        public int Port(int end)
        {
            return end == 0 ? From : To;
        }
    }
}
