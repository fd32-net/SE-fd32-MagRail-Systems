using System;
using System.Collections.Generic;
using Sandbox.Game.Entities;
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
        // Shorter than this the track pieces cannot be laid (the shortest piece squeezed to half its length).
        public const double MinSpanLength = 0.3;
        // Curves of spans between two moving grids follow them at this rate (ticks).
        const int MovingCurveTicks = 10;

        readonly Dictionary<long, TrackBlockLogic> blocks = new Dictionary<long, TrackBlockLogic>();
        readonly List<Span> spans = new List<Span>();
        readonly Dictionary<long, Span> byPort = new Dictionary<long, Span>();
        readonly List<Span> scratch = new List<Span>();
        readonly List<Span> apart = new List<Span>();
        // Projectors of the grid last asked about (a pasted grid registers all its blocks in one frame).
        readonly List<IMyProjector> projectors = new List<IMyProjector>();
        IMyCubeGrid projectorsGrid;
        int projectorsTick = -1;
        bool unresolved;
        int tick;

        // Changes with every change of blocks, spans or rail geometry (the track index rebuilds).
        public int Version { get; private set; }
        public readonly TrackIndex Index = new TrackIndex();

        public void Touch()
        {
            Version++;
        }

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

        // The port's span is in rail mode (built, or waiting to be): it is changed or deleted only as a prototype.
        public bool HasRail(TrackBlockLogic block, int port)
        {
            Span s = SpanAt(block, port);
            return s != null && s.Record.WantRail;
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
            Version++;
            AddOwnedSpans(block);
            Resolve();
        }

        public void Unregister(TrackBlockLogic block)
        {
            blocks.Remove(block.Block.EntityId);
            Version++;
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
            // Welded from a projection: built block by block, in the projector's grid frame, maybe rotated or
            // with other settings: its spans could not come back in the same place and are all dropped.
            bool projected = copy && store.Spans.Count > 0 && FromProjection(owner.Block);
            bool changed = false;
            for (int i = store.Spans.Count - 1; i >= 0; i--)
            {
                SpanRecord r = store.Spans[i];
                if (copy)
                {
                    // A copied (pasted) block: spans to other grids are dropped, spans inside the grid are
                    // found again by the partner's position.
                    if (!r.SameGrid || projected)
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
            // Clients decide the same from their own projection; this corrects one that has none.
            if (projected && Net.IsServer)
                Net.BroadcastSpans(owner);
            unresolved = true;
        }

        // A block welded from a projection: the projector keeps the projected copy of it, hidden, at its place
        // (MyProjectorBase.BuildInternal builds on the projector's grid and only hides the projected block).
        bool FromProjection(IMyCubeBlock block)
        {
            IMyCubeGrid grid = block.CubeGrid;
            if (grid != projectorsGrid || tick != projectorsTick)
            {
                projectorsGrid = grid;
                projectorsTick = tick;
                projectors.Clear();
                MyCubeGrid g = grid as MyCubeGrid;
                if (g != null)
                {
                    foreach (MyCubeBlock b in g.GetFatBlocks())
                    {
                        IMyProjector p = b as IMyProjector;
                        if (p != null)
                            projectors.Add(p);
                    }
                }
            }
            Vector3D at = grid.GridIntegerToWorld(block.Position);
            for (int i = 0; i < projectors.Count; i++)
            {
                IMyCubeGrid projected = projectors[i].ProjectedGrid;
                if (projected == null)
                    continue;
                IMySlimBlock twin = projected.GetCubeBlock(projected.WorldToGridInteger(at));
                if (twin != null && twin.BlockDefinition.Id == block.BlockDefinition)
                    return true;
            }
            return false;
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
                Version++;
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
                Version++;
            }
            scratch.Clear();
            TrackTerminal.Refresh();
        }

        void Unbind(Span s)
        {
            Version++;
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
                if (!s.Bound)
                    continue;
                // The grids under it came apart (a block broke off, a station became a ship, a ship split): it
                // could not come back in the same place, so it is deleted, not left as a prototype. Checked before
                // the damage: a block that breaks off is often damaged too.
                if (Net.IsServer && GridsApart(s.Owner, s.Partner))
                    apart.Add(s);
                else
                    s.Problem = Problem(s.Owner, s.OwnerPort, s.Partner, s.PartnerPort);
            }
            for (int i = 0; i < apart.Count; i++)
                if (apart[i].Bound)
                    Unlink(apart[i]);
            apart.Clear();
        }

        // Why these two ports cannot carry a rail now (Loc key), or null.
        public static string Problem(TrackBlockLogic a, int aPort, TrackBlockLogic b, int bPort)
        {
            if (!a.Block.IsFunctional || !b.Block.IsFunctional)
                return "MagRail_Problem_Damaged";
            if (a.Style != b.Style)
                return "MagRail_Problem_Style";
            return GridsOrLengthProblem(a, aPort, b, bPort);
        }

        // Neither both on stations nor both on one grid (then the span moves with it).
        static bool GridsApart(TrackBlockLogic a, TrackBlockLogic b)
        {
            return a.Block.CubeGrid != b.Block.CubeGrid && !(a.Block.CubeGrid.IsStatic && b.Block.CubeGrid.IsStatic);
        }

        static string GridsOrLengthProblem(TrackBlockLogic a, int aPort, TrackBlockLogic b, int bPort)
        {
            if (GridsApart(a, b))
                return "MagRail_Problem_Grids";
            if (Vector3D.Distance(a.PortWorld(aPort).Translation, b.PortWorld(bPort).Translation) < MinSpanLength)
                return "MagRail_Problem_Short";
            return null;
        }

        // Whether a new span may be laid between two ports (Loc key of the reason, or null). A prototype on either port
        // is no obstacle: Link replaces it; a rail is (it goes back to prototype first).
        public string LinkProblem(TrackBlockLogic a, int aPort, TrackBlockLogic b, int bPort)
        {
            if (a == b)
                return "MagRail_Problem_SameBlock";
            if (!a.Block.IsFunctional || !b.Block.IsFunctional)
                return "MagRail_Problem_Damaged";
            if (HasRail(a, aPort) || HasRail(b, bPort))
                return "MagRail_Problem_PortRail";
            if (a.Style != b.Style)
                return "MagRail_Problem_Style";
            return GridsOrLengthProblem(a, aPort, b, bPort);
        }

        // ---- joints ----

        // Joint tolerance: port points this close, facing each other within this angle.
        const double JointDistance = 0.5;
        static readonly double JointFacingCos = -Math.Cos(MathHelper.ToRadians(15));
        readonly List<TrackIndex.Entry> jointScratch = new List<TrackIndex.Entry>();

        // A fixed block whose port meets this one end to end (on any grid, of any style: a train would drive across anyway),
        // not computed ahead: found when a train gets there. A port with a span has no joint.
        public bool FindJoint(TrackBlockLogic block, int port, out TrackBlockLogic other, out int otherPort)
        {
            other = null;
            otherPort = -1;
            if (!block.IsJointPort(port) || SpanAt(block, port) != null)
                return false;
            MatrixD p = block.PortWorld(port);
            double best = JointDistance;
            jointScratch.Clear();
            Index.Query(this, p.Translation, JointDistance, jointScratch);
            for (int i = 0; i < jointScratch.Count; i++)
            {
                TrackBlockLogic b = jointScratch[i].Block;
                if (b == null || b == block || !b.Rideable)
                    continue;
                for (int q = 0; q < b.PortCount; q++)
                {
                    if (!b.IsJointPort(q) || SpanAt(b, q) != null)
                        continue;
                    MatrixD m = b.PortWorld(q);
                    double d = Vector3D.Distance(m.Translation, p.Translation);
                    if (d <= best && Vector3D.Dot(m.Forward, p.Forward) <= JointFacingCos)
                    {
                        best = d;
                        other = b;
                        otherPort = q;
                    }
                }
            }
            jointScratch.Clear();
            return other != null;
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
            s.Curve = BaseCurve(s);
            // Over planet ground, spans that stand still (both blocks on static grids): a prototype follows the ground
            // as it is, a rail keeps the lift measured when it was built (SetMode).
            if (s.Owner.Block.CubeGrid.IsStatic && s.Partner.Block.CubeGrid.IsStatic && !s.Record.NoLift)
                s.Curve = s.Record.WantRail
                    ? TerrainLift.Apply(s.Curve, FrameMatrix(s), s.Record.Lift)
                    : TerrainLift.Live(s.Curve, FrameMatrix(s), s.Owner.Style);
            s.CurveOwnerRevision = s.Owner.Revision;
            s.CurvePartnerRevision = s.Partner.Revision;
            s.CurveGrid = grid;
            s.CurveTick = tick;
            return s.Curve;
        }

        // The span's curve between its ports, before any lift over the ground.
        static SpanCurve BaseCurve(Span s)
        {
            return InGridFrame(s)
                ? SpanCurve.Build(s.Owner.PortGridLocal(s.OwnerPort), s.Partner.PortGridLocal(s.PartnerPort))
                : SpanCurve.Build(s.Owner.PortWorld(s.OwnerPort), s.Partner.PortWorld(s.PartnerPort));
        }

        // How the span is laid from track pieces (for its current curve).
        public TrackLayout LayoutOf(Span s)
        {
            SpanCurve c = CurveOf(s);
            if (c == null)
                return null;
            // A style without strip models lays its switch branches as they are (no FanLevel).
            TrackStyle style = s.Owner.Style;
            bool fanChanged = style.HasStrips && FanLevel.KeyChanged(this, s);
            if (s.Layout == null || s.Layout.Curve != c || fanChanged)
                s.Layout = TrackLayout.Build(c, style, style.HasStrips ? FanLevel.Build(this, s, c, style.Width) : null,
                    s.Owner.IsBranchPort(s.OwnerPort), s.Partner.IsBranchPort(s.PartnerPort));
            return s.Layout;
        }

        // ---- changes (server; a client sends a request instead) ----

        // noLift: the span ignores the ground (SpanRecord.NoLift).
        public void RequestLink(TrackBlockLogic a, int aPort, TrackBlockLogic b, int bPort, bool noLift)
        {
            if (Net.IsServer)
                Link(a, aPort, b, bPort, noLift);
            else
                Net.SendLinkRequest(a, a.PortName(aPort), b, b.PortName(bPort), noLift);
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

        // Rails only (a prototype is not painted).
        public void RequestPaint(Span s, Vector3 hsv, string skin)
        {
            if (!s.Record.WantRail)
                return;
            if (Net.IsServer)
                SetPaint(s, hsv, skin);
            else
                Net.SendPaintRequest(s.Owner, s.Record.OwnerPort, hsv, skin);
        }

        public void Link(TrackBlockLogic a, int aPort, TrackBlockLogic b, int bPort, bool noLift)
        {
            if (LinkProblem(a, aPort, b, bPort) != null)
                return;
            // Prototypes on the two ports give way to the new span.
            Span replaced = SpanAt(a, aPort);
            if (replaced != null)
                Unlink(replaced);
            replaced = SpanAt(b, bPort);
            if (replaced != null)
                Unlink(replaced);
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
                NoLift = noLift,
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
            // Built as it lies now: the prototype's lift over the ground is kept with the rail.
            s.Record.Lift = null;
            if (wantRail && s.Bound && !s.Record.NoLift && s.Owner.Block.CubeGrid.IsStatic && s.Partner.Block.CubeGrid.IsStatic)
                s.Record.Lift = TerrainLift.Measure(BaseCurve(s), FrameMatrix(s), s.Owner.Style);
            s.Record.WantRail = wantRail;
            s.Curve = null;
            Changed(s.Owner);
        }

        public void SetPaint(Span s, Vector3 hsv, string skin)
        {
            s.Record.Painted = true;
            s.Record.PaintH = hsv.X;
            s.Record.PaintS = hsv.Y;
            s.Record.PaintV = hsv.Z;
            s.Record.PaintSkin = skin;
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
            Index.Clear();
        }
    }
}
