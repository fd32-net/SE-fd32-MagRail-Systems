using VRage.Game.Entity;
using VRageMath;

namespace MagRail
{
    // The piece entities one manager (TrackPieces, TrackCollision) placed for one span, valid for one
    // layout and one parent; a new curve or a grid turning static/dynamic starts them over.
    public class PlacedPieces
    {
        public readonly Span Span;
        public TrackLayout Layout;
        public MyEntity Parent;     // the moving grid the pieces are children of, or null
        public MatrixD Frame;       // the span's frame when the layout was taken
        public Vector3 Color;       // colour mask HSV and skin of the pieces (TrackPieces)
        public string Skin = "";
        public MyEntity[] Entities;
        public int Stamp;
        public double Distance;

        public PlacedPieces(Span span)
        {
            Span = span;
        }

        public void Take(TrackLayout layout, MyEntity parent, MatrixD frame, int count)
        {
            if (layout == Layout && parent == Parent)
                return;
            Clear();
            Layout = layout;
            Parent = parent;
            Frame = frame;
            Entities = new MyEntity[count];
        }

        public int Count
        {
            get
            {
                int n = 0;
                if (Entities != null)
                    for (int i = 0; i < Entities.Length; i++)
                        if (Entities[i] != null)
                            n++;
                return n;
            }
        }

        // Repaints the placed pieces and the ones still to come.
        public void Paint(Vector3 color, string skin)
        {
            bool newSkin = skin != Skin;
            Color = color;
            Skin = skin;
            if (Entities == null)
                return;
            for (int i = 0; i < Entities.Length; i++)
                if (Entities[i] != null)
                    PieceEntities.Paint(Entities[i], color, skin, newSkin);
        }

        public void Clear()
        {
            if (Entities == null)
                return;
            for (int i = 0; i < Entities.Length; i++)
            {
                PieceEntities.Remove(Entities[i]);
                Entities[i] = null;
            }
        }
    }
}
