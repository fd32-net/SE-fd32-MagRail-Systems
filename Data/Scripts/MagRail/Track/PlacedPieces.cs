using System.Collections.Generic;
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
        // By Entities (TrackCollision): the collision piece is stage 0's low one.
        public bool[] Low;
        // A span being built (TrackPieces): its welding as last applied (ApplyWeld; null: built), the stage pieces
        // (TrackLayout.Stages) and the stage each placed one shows.
        public float[] Weld;
        public int WeldRevision = -1;
        public MyEntity[] Stages;
        public int[] StageShown;
        // Every piece the welding asks for is placed (TrackPieces.Build).
        public bool Done;
        // Done once for this layout, or the pieces of the one before still stand in (Leaving): SpanLines no longer
        // draws the span (a new welding places its pieces again over a few ticks, and the steel line flashed meanwhile).
        public bool Shown;
        // The pieces of the layout before, kept until this one's are all placed (Take, keep): a span finished to 100 %
        // or ground from it is laid anew (TrackLayout.Segmented) and was a hole for a few ticks.
        public readonly List<MyEntity> Leaving = new List<MyEntity>();
        public int Stamp;
        public double Distance;

        public PlacedPieces(Span span)
        {
            Span = span;
        }

        // keep: the pieces of a fully placed layout on the same curve and parent stand in until the new ones are placed
        // (Leaving, TrackPieces.Build).
        public void Take(TrackLayout layout, MyEntity parent, MatrixD frame, int count, bool keep = false)
        {
            if (layout == Layout && parent == Parent)
                return;
            keep &= Shown && Layout != null && layout.Curve == Layout.Curve && parent == Parent;
            if (keep)
                Hand();
            else
                Clear();
            Layout = layout;
            Parent = parent;
            Frame = frame;
            Entities = new MyEntity[count];
            Bares = new MyEntity[count];
            Low = new bool[count];
            Stages = new MyEntity[layout.Stages.Count];
            StageShown = new int[layout.Stages.Count];
            Weld = null;
            WeldRevision = -1;
            Done = false;
            Shown = keep;
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
                if (Stages != null)
                    for (int i = 0; i < Stages.Length; i++)
                        if (Stages[i] != null)
                            n++;
                return n + Leaving.Count;
            }
        }

        // Track piece i is shown: built, or its segment finished (TrackStyle.Working).
        public bool Wanted(int i)
        {
            return Weld == null || SpanWeld.Stage(Weld[Layout.PieceSegment(i)], Layout.Style.Working) == SpanWeld.Finished;
        }

        // The construction stage stage piece j shows (0..3), or -1: none (built, or its segment finished).
        public int StageWanted(int j)
        {
            if (Weld == null)
                return -1;
            int stage = SpanWeld.Stage(Weld[Layout.StageSegment[j]], Layout.Style.Working);
            return stage == SpanWeld.Finished ? -1 : stage;
        }

        // New welding: the pieces it no longer asks for go (the missing ones come with TrackPieces.Build).
        public void ApplyWeld(float[] weld, int revision)
        {
            Weld = weld;
            WeldRevision = revision;
            Done = false;
            for (int i = 0; i < Entities.Length; i++)
            {
                if (Entities[i] != null && !Wanted(i))
                {
                    PieceEntities.Remove(Entities[i]);
                    PieceEntities.Remove(Bares[i]);
                    Entities[i] = null;
                    Bares[i] = null;
                }
            }
            for (int j = 0; j < Stages.Length; j++)
            {
                if (Stages[j] != null && StageShown[j] != StageWanted(j))
                {
                    PieceEntities.Remove(Stages[j]);
                    Stages[j] = null;
                }
            }
        }

        // Repaints the placed pieces and the ones still to come (not the stages: bare construction steel).
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

        // The placed pieces go to Leaving (Take, keep).
        void Hand()
        {
            for (int i = 0; i < Entities.Length; i++)
            {
                if (Entities[i] != null)
                    Leaving.Add(Entities[i]);
                if (Bares[i] != null)
                    Leaving.Add(Bares[i]);
            }
            for (int j = 0; j < Stages.Length; j++)
                if (Stages[j] != null)
                    Leaving.Add(Stages[j]);
        }

        public void Release()
        {
            for (int i = 0; i < Leaving.Count; i++)
                PieceEntities.Remove(Leaving[i]);
            Leaving.Clear();
        }

        public void Clear()
        {
            Release();
            if (Entities == null)
                return;
            for (int i = 0; i < Entities.Length; i++)
            {
                PieceEntities.Remove(Entities[i]);
                PieceEntities.Remove(Bares[i]);
                Entities[i] = null;
                Bares[i] = null;
            }
            for (int j = 0; j < Stages.Length; j++)
            {
                PieceEntities.Remove(Stages[j]);
                Stages[j] = null;
            }
        }
    }
}
