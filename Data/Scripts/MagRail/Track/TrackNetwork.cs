using System.Collections.Generic;
using Sandbox.ModAPI;
using VRage.Game.ModAPI;
using VRageMath;

namespace MagRail
{
    // All track blocks and spans. A span lives in the Storage of its owner block (SpanStore) and is
    // bound to its partner when both are loaded; an unbound record stays stored (the partner may be
    // streamed out or hidden by a concealment mod) and only an explicit delete removes it.
    // The server changes spans; clients ask it (Net) and mirror the owner's store it sends back.
    public class TrackNetwork
    {
        public const double MinSpanLength = 2.5;
        // Curves of spans between two moving grids follow them at this rate (ticks).
        const int MovingCurveTicks = 10;

        readonly Dictionary<long, TrackBlockLogic> blocks = new Dictionary<long, TrackBlockLogic>();
        readonly List<Span> spans = new List<Span>();
        readonly Dictionary<long, Span> byPort = new Dictionary<long, Span>();
        readonly List<Span> scratch = new List<Span>();
        bool unresolved;
        int tick;

        public ICollection<TrackBlockLogic> Blocks
        {
            get { return blocks.Values; }
        }

        public List<Span> Spans
        {
            get { return spans; }
        }

        static long PortKey(TrackBlockLogic block, int port)
        {
            return unchecked(block.Block.EntityId * 31 + port);
        }

        public TrackBlockLogic Find(long entityId)
        {
            TrackBlockLogic logic;
            return blocks.TryGetValue(entityId, out logic) ? logic : null;
        }

        public Span SpanAt(TrackBlockLogic block, int port)
        {
            Span s;
            return byPort.TryGetValue(PortKey(block, port), out s) ? s : null;
        }

        // A block is locked while a span in rail mode touches it: its height and head stay as they are.
        public bool IsLocked(TrackBlockLogic block)
        {
            for (int i = 0; i < block.PortCount; i++)
            {
                Span s = SpanAt(block, i);
                if (s != null && s.Record.WantRail)
                    return true;
            }
            return false;
        }

        // ---- blocks ----

        public void Register(TrackBlockLogic block)
        {
            blocks[block.Block.EntityId] = block;
            AddOwnedSpans(block);
            Resolve();
        }

        public void Unregister(TrackBlockLogic block)
        {
            blocks.Remove(block.Block.EntityId);
            for (int i = spans.Count - 1; i >= 0; i--)
            {
                Span s = spans[i];
                if (s.Owner == block)
                {
                    Unbind(s);
                    spans.RemoveAt(i);
                }
                else if (s.Partner == block)
                {
                    Unbind(s);
                    unresolved = true;
                }
            }
            TrackTerminal.Refresh();
        }

        void AddOwnedSpans(TrackBlockLogic owner)
        {
            SpanStore store = owner.SpanStore;
            bool copy = store.SelfId != owner.Block.EntityId;
            bool changed = false;
            for (int i = store.Spans.Count - 1; i >= 0; i--)
            {
                SpanRecord r = store.Spans[i];
                if (copy)
                {
                    // A copied block: spans to other grids are dropped, spans inside the grid are
                    // found again by the partner's position.
                    if (!r.SameGrid)
                    {
                        store.Spans.RemoveAt(i);
                        changed = true;
                        continue;
                    }
                    r.PartnerId = 0;
                    changed = true;
                }
                spans.Add(new Span(owner, r));
            }
            if (changed || copy)
                owner.SaveSpans();
            unresolved = true;
        }

        // Binds every unbound span whose two blocks are loaded.
        void Resolve()
        {
            unresolved = false;
            scratch.Clear();
            for (int i = 0; i < spans.Count; i++)
            {
                Span s = spans[i];
                if (s.Bound)
                    continue;
                SpanRecord r = s.Record;
                if (!s.Owner.PortsKnown)
                {
                    unresolved = true;
                    continue;
                }
                s.OwnerPort = s.Owner.PortIndex(r.OwnerPort);
                if (s.OwnerPort < 0)
                {
                    scratch.Add(s);     // the model has no such port any more
                    continue;
                }
                TrackBlockLogic partner = null;
                if (r.PartnerId != 0)
                {
                    blocks.TryGetValue(r.PartnerId, out partner);
                }
                else if (r.SameGrid)
                {
                    IMySlimBlock slim = s.Owner.Block.CubeGrid.GetCubeBlock(new Vector3I(r.PartnerX, r.PartnerY, r.PartnerZ));
                    if (slim != null && slim.FatBlock != null)
                        blocks.TryGetValue(slim.FatBlock.EntityId, out partner);
                }
                if (partner == null || !partner.PortsKnown)
                {
                    unresolved = true;
                    continue;
                }
                int partnerPort = partner.PortIndex(r.PartnerPort);
                if (partnerPort < 0 || partner == s.Owner
                    || byPort.ContainsKey(PortKey(s.Owner, s.OwnerPort)) || byPort.ContainsKey(PortKey(partner, partnerPort)))
                {
                    scratch.Add(s);     // dead or duplicate record
                    continue;
                }
                s.Partner = partner;
                s.PartnerPort = partnerPort;
                s.Curve = null;
                byPort[PortKey(s.Owner, s.OwnerPort)] = s;
                byPort[PortKey(partner, partnerPort)] = s;
                if (r.PartnerId != partner.Block.EntityId)
                {
                    r.PartnerId = partner.Block.EntityId;
                    s.Owner.SaveSpans();
                }
            }
            for (int i = 0; i < scratch.Count; i++)
            {
                Span s = scratch[i];
                s.Owner.SpanStore.Spans.Remove(s.Record);
                s.Owner.SaveSpans();
                spans.Remove(s);
            }
            scratch.Clear();
            TrackTerminal.Refresh();
        }

        void Unbind(Span s)
        {
            if (s.Partner != null)
            {
                byPort.Remove(PortKey(s.Owner, s.OwnerPort));
                byPort.Remove(PortKey(s.Partner, s.PartnerPort));
            }
            s.Partner = null;
            s.PartnerPort = -1;
            s.Curve = null;
        }

        // Client: the server's version of an owner's spans.
        public void ReplaceOwnerSpans(TrackBlockLogic owner, SpanStore store)
        {
            for (int i = spans.Count - 1; i >= 0; i--)
            {
                if (spans[i].Owner == owner)
                {
                    Unbind(spans[i]);
                    spans.RemoveAt(i);
                }
            }
            owner.SetSpanStore(store);
            for (int i = 0; i < store.Spans.Count; i++)
                spans.Add(new Span(owner, store.Spans[i]));
            Resolve();
        }

        // ---- update ----

        public void Update()
        {
            tick++;
            if (unresolved && tick % 100 == 0)
                Resolve();
            if (tick % 10 != 0)
                return;
            for (int i = 0; i < spans.Count; i++)
            {
                Span s = spans[i];
                if (s.Bound)
                    s.Problem = Problem(s.Owner, s.OwnerPort, s.Partner, s.PartnerPort);
            }
        }

        // Why these two ports cannot carry a rail now (Loc key), or null.
        public static string Problem(TrackBlockLogic a, int aPort, TrackBlockLogic b, int bPort)
        {
            if (!a.Block.IsFunctional || !b.Block.IsFunctional)
                return "MagRail_Problem_Damaged";
            return GridsOrLengthProblem(a, aPort, b, bPort);
        }

        static string GridsOrLengthProblem(TrackBlockLogic a, int aPort, TrackBlockLogic b, int bPort)
        {
            // Both on stations, or both on one grid (then the span moves with it).
            if (a.Block.CubeGrid != b.Block.CubeGrid && !(a.Block.CubeGrid.IsStatic && b.Block.CubeGrid.IsStatic))
                return "MagRail_Problem_Grids";
            if (Vector3D.Distance(a.PortWorld(aPort).Translation, b.PortWorld(bPort).Translation) < MinSpanLength)
                return "MagRail_Problem_Short";
            return null;
        }

        // Whether a new span may be laid between two ports (Loc key of the reason, or null).
        public string LinkProblem(TrackBlockLogic a, int aPort, TrackBlockLogic b, int bPort)
        {
            if (a == b)
                return "MagRail_Problem_SameBlock";
            if (SpanAt(a, aPort) != null || SpanAt(b, bPort) != null)
                return "MagRail_Problem_PortBusy";
            return GridsOrLengthProblem(a, aPort, b, bPort);
        }

        // ---- geometry ----

        public static bool InGridFrame(Span s)
        {
            return s.Owner.Block.CubeGrid == s.Partner.Block.CubeGrid;
        }

        public static MatrixD FrameMatrix(Span s)
        {
            return InGridFrame(s) ? s.Owner.Block.CubeGrid.WorldMatrix : MatrixD.Identity;
        }

        // The span's curve, rebuilt when a block's rail moved (settings), the blocks changed grids (merge,
        // split) or, between two grids that are not both static, every few ticks.
        public SpanCurve CurveOf(Span s)
        {
            if (!s.Bound)
                return null;
            bool inGrid = InGridFrame(s);
            object grid = inGrid ? s.Owner.Block.CubeGrid : null;
            bool moving = !inGrid && !(s.Owner.Block.CubeGrid.IsStatic && s.Partner.Block.CubeGrid.IsStatic);
            if (s.Curve != null && s.CurveOwnerRevision == s.Owner.Revision && s.CurvePartnerRevision == s.Partner.Revision
                && s.CurveGrid == grid && !(moving && tick - s.CurveTick >= MovingCurveTicks))
                return s.Curve;
            s.Curve = inGrid
                ? SpanCurve.Build(s.Owner.PortGridLocal(s.OwnerPort), s.Partner.PortGridLocal(s.PartnerPort))
                : SpanCurve.Build(s.Owner.PortWorld(s.OwnerPort), s.Partner.PortWorld(s.PartnerPort));
            s.CurveOwnerRevision = s.Owner.Revision;
            s.CurvePartnerRevision = s.Partner.Revision;
            s.CurveGrid = grid;
            s.CurveTick = tick;
            return s.Curve;
        }

        // How the span is laid from track pieces (for its current curve).
        public TrackLayout LayoutOf(Span s)
        {
            SpanCurve c = CurveOf(s);
            if (c == null)
                return null;
            if (s.Layout == null || s.Layout.Curve != c)
                s.Layout = TrackLayout.Build(c);
            return s.Layout;
        }

        // ---- changes (server; a client sends a request instead) ----

        public void RequestLink(TrackBlockLogic a, int aPort, TrackBlockLogic b, int bPort)
        {
            if (Net.IsServer)
                Link(a, aPort, b, bPort);
            else
                Net.SendLinkRequest(a, a.PortName(aPort), b, b.PortName(bPort));
        }

        public void RequestUnlink(Span s)
        {
            if (Net.IsServer)
                Unlink(s);
            else
                Net.SendUnlinkRequest(s.Owner, s.Record.OwnerPort);
        }

        public void RequestMode(Span s, bool wantRail)
        {
            if (Net.IsServer)
                SetMode(s, wantRail);
            else
                Net.SendModeRequest(s.Owner, s.Record.OwnerPort, wantRail);
        }

        public void Link(TrackBlockLogic a, int aPort, TrackBlockLogic b, int bPort)
        {
            if (LinkProblem(a, aPort, b, bPort) != null)
                return;
            string portName = a.PortName(aPort);
            // An unbound record on this port (its partner is gone) gives way to the new span.
            for (int i = spans.Count - 1; i >= 0; i--)
            {
                Span old = spans[i];
                if (old.Owner == a && !old.Bound && old.Record.OwnerPort == portName)
                {
                    a.SpanStore.Spans.Remove(old.Record);
                    spans.RemoveAt(i);
                }
            }
            Vector3I pos = b.Block.Position;
            SpanRecord r = new SpanRecord
            {
                OwnerPort = portName, PartnerId = b.Block.EntityId, PartnerPort = b.PortName(bPort),
                SameGrid = a.Block.CubeGrid == b.Block.CubeGrid, PartnerX = pos.X, PartnerY = pos.Y, PartnerZ = pos.Z,
            };
            a.SpanStore.Spans.Add(r);
            spans.Add(new Span(a, r));
            Changed(a);
        }

        public void Unlink(Span s)
        {
            Unbind(s);
            spans.Remove(s);
            s.Owner.SpanStore.Spans.Remove(s.Record);
            Changed(s.Owner);
        }

        public void SetMode(Span s, bool wantRail)
        {
            s.Record.WantRail = wantRail;
            Changed(s.Owner);
        }

        void Changed(TrackBlockLogic owner)
        {
            owner.SaveSpans();
            Resolve();
            Net.BroadcastSpans(owner);
        }

        // Server: the span on a named port of an owner block (requests name ports, not indexes).
        public Span FindOwnedSpan(TrackBlockLogic owner, string ownerPort)
        {
            for (int i = 0; i < spans.Count; i++)
                if (spans[i].Owner == owner && spans[i].Record.OwnerPort == ownerPort)
                    return spans[i];
            return null;
        }

        public void Clear()
        {
            blocks.Clear();
            spans.Clear();
            byPort.Clear();
        }
    }
}
