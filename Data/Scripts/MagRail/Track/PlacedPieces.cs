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
        // The selected branch of the switch at the span's start / end when it is not the span's (the copper fades
        // there, TrackLayout.Glow), else -1.
        public int SelectedStart = -1, SelectedEnd = -1;
        public MyEntity[] Entities;
        // By Entities: the bare body under a piece that has one (TrackLayout.Bare), else null. It is always shown and
        // the piece over it only while the branch is selected (Show; the bodies are the same): swapping the two left a
        // hole for a frame, an entity shown again comes a frame late; now at most the copper does.
        public MyEntity[] Bares;
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
            Bares = new MyEntity[count];
        }

        public int Count
        {
            get
            {
                int n = 0;
                if (Entities != null)
                    for (int i = 0; i < Entities.Length; i++)
                    {
                        if (Entities[i] != null)
                            n++;
                        if (Bares[i] != null)
                            n++;
                    }
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
            {
                if (Entities[i] != null)
                    PieceEntities.Paint(Entities[i], color, skin, newSkin);
                if (Bares[i] != null)
                    PieceEntities.Paint(Bares[i], color, skin, newSkin);
                // A redraw makes the render objects again.
                Show(i);
            }
        }

        // Lights the placed pieces' copper again for a new branch selection (and the ones still to come).
        public void Light(int selectedStart, int selectedEnd)
        {
            SelectedStart = selectedStart;
            SelectedEnd = selectedEnd;
            if (Entities == null)
                return;
            for (int i = 0; i < Entities.Length; i++)
            {
                if (Entities[i] != null)
                    PieceEntities.LightCopper(Entities[i], Layout.Glow(i, selectedStart, selectedEnd));
                Show(i);
            }
        }

        // Piece i over its bare body: hidden while the branch is not selected (TrackLayout.Bared).
        public void Show(int i)
        {
            if (Bares[i] != null && Entities[i] != null)
                Entities[i].Render.Visible = !Layout.Bared(i, SelectedStart, SelectedEnd);
        }

        public void Clear()
        {
            if (Entities == null)
                return;
            for (int i = 0; i < Entities.Length; i++)
            {
                PieceEntities.Remove(Entities[i]);
                PieceEntities.Remove(Bares[i]);
                Entities[i] = null;
                Bares[i] = null;
            }
        }
    }
}
