using System;
using System.Collections.Generic;
using Sandbox.Definitions;
using Sandbox.Game.Entities;
using Sandbox.Game.Gui;
using Sandbox.ModAPI;
using Sandbox.ModAPI.Weapons;
using VRage.Game;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRage;
using VRage.ObjectBuilders;
using VRage.Utils;
using MyItemType = VRage.Game.ModAPI.Ingame.MyItemType;
using VRageMath;
using IMyEntity = VRage.ModAPI.IMyEntity;

namespace MagRail
{
    // Welding and grinding spans (survival, PLAN 10.8, SpanWeld) with the vanilla hand welder and grinder. The tools do
    // not see spans (they are not blocks), so the server watches every player's tool itself: shooting, the head's aim on a
    // weld segment within the tool's reach, no block in front of it (a block the ray meets first is the tool's own target)
    // -> that segment is worked on, as a block (every segment works on its own, Span.Ride).
    // Welding (a span being built): like a vanilla block, every Rail component the segment still needs goes from the
    // inventory into the span's stock at once, and is taken from there (TrackStyle.SegmentRails) as the welding gets to
    // it (stock empty: the welding stops there). Speed: world welder speed x the tool's (Mk1..Mk4), a segment takes
    // SegmentSeconds per component at speed 1; finished (carries trains) from TrackStyle.Working on, welded on to 1.
    // Grinding (any rail: a built one becomes one being built): the span's stock first, then the segment down to stage 0,
    // twice as fast as welding (as vanilla); the components come out into the inventory, what it has no room for stays in
    // the stock. A span at stage 0 all along with an empty stock may go back to prototype (TrackNetwork.CanUnbuild).
    // With free rails (creative world, or the player's admin creative tools on) the hand welder needs no components, as
    // the vanilla one: none are taken, the span's stock goes back into the inventory. The game does not tell the server's
    // mod whether a player's creative tools are on: the client reports its switch (toolsOn), the server checks the rights.
    // The server keeps the progress while working and stores it (TrackNetwork.SetWeld: saved, sent to clients) when a
    // segment changes stage or the stock changes, else every CommitTicks.
    // Client: the tool's window (MyHud.BlockInfo, ShowInfo) shows the aimed segment as a block (any rail, as a tool shows a
    // finished block too), and over it the whole span (WeldStrip: its segments, gaps); sparks and the tool's sound at the
    // aimed point while working; grinding with no room in the inventory, the vanilla "inventory is full" warning.
    // Ship welders and grinders (ShipToolLogic) work the same way on the segments their detector sphere touches, while
    // they are on: a welder from its own inventory, then over the conveyors (if it uses them) straight into the span; a
    // grinder into its own inventory (the game pushes it on over the conveyors) and, as vanilla, never on rails of its own
    // grid. Speed as vanilla's: 3 per second x the world's welder / grinder speed, shared by up to 4 segments. No window;
    // sparks and sound where they work.
    public static class SpanWelding
    {
        const int Ticks = 10;
        const int CommitTicks = 30;
        // Reach from the head: the tool's (2 m x DistanceMultiplier, measured by the game from the hand) plus this.
        const double ReachExtra = 1.0;
        const float SegmentSeconds = 1f;
        const float GrindSpeed = 2f;
        // Vanilla ship tools: 4 x 0.25 per activation, every 20 ticks (MyShipToolBase: every 10th tick, at most every 250 ms).
        const float ShipSpeed = 3f;
        // A welder short of components looks over the conveyors at most this often.
        const int PullTicks = 60;
        public static readonly List<ShipToolLogic> ShipTools = new List<ShipToolLogic>();
        public static readonly MyDefinitionId RailId = new MyDefinitionId(typeof(MyObjectBuilder_Component), "MagRail_Rail");
        static readonly MyItemType RailItem = MyItemType.MakeComponent("MagRail_Rail");

        // ---- server ----

        // A span's progress, stock and lost integrity (SpanWeld.Lost) while it is worked on (or damaged): ahead of its
        // record by up to CommitTicks.
        class Work
        {
            public float[] Done, Lost;
            public int Stock, Revision, Committed;
            public bool Dirty;
        }

        static readonly Dictionary<Span, Work> work = new Dictionary<Span, Work>();
        static readonly List<Span> finished = new List<Span>();
        static readonly List<IMyPlayer> players = new List<IMyPlayer>();
        // Players whose creative tools are on, as they reported (Net: CreativeTools).
        static readonly HashSet<ulong> toolsOn = new HashSet<ulong>();

        public static void SetToolsOn(ulong steamId, bool on)
        {
            if (on)
                toolsOn.Add(steamId);
            else
                toolsOn.Remove(steamId);
        }

        public static void ServerUpdate(TrackNetwork network, int tick)
        {
            if (tick % Ticks != 0)
                return;
            players.Clear();
            MyAPIGateway.Players.GetPlayers(players);
            for (int i = 0; i < players.Count; i++)
            {
                IMyCharacter character = players[i].Character;
                if (character == null || character.IsDead)
                    continue;
                IMyEngineerToolBase tool = character.EquippedTool as IMyEngineerToolBase;
                bool welder = tool is IMyWelder;
                if (tool == null || !tool.IsShooting || (!welder && !(tool is IMyAngleGrinder)))
                    continue;
                float speed, distance;
                ToolNumbers(tool, out speed, out distance);
                Span span;
                int segment;
                double at;
                if (!Aim(network, character, 2 * distance + ReachExtra, welder, out span, out segment, out at))
                    continue;
                float seconds = speed * Ticks / 60f;
                if (welder)
                    Weld(network, span, segment, MyAPIGateway.Session.SessionSettings.WelderSpeedMultiplier * seconds,
                        character.GetInventory(), null,
                        TrackNetwork.FreeRailsFor(players[i].SteamUserId, toolsOn.Contains(players[i].SteamUserId)), tick);
                else
                    Grind(network, span, segment, MyAPIGateway.Session.SessionSettings.GrinderSpeedMultiplier * GrindSpeed * seconds,
                        character.GetInventory(), tick);
            }
            players.Clear();
            for (int i = 0; i < ShipTools.Count; i++)
            {
                ShipToolLogic t = ShipTools[i];
                BoundingSphereD sphere;
                if (!t.Working || !t.Sphere(out sphere))
                    continue;
                Touch(network, sphere, t.Welder, t.Welder ? null : t.Block.CubeGrid, contacts);
                float seconds = ShipSpeed * Ticks / 60f / Math.Min(4, Math.Max(1, contacts.Count));
                for (int c = 0; c < contacts.Count; c++)
                    if (t.Welder)
                        Weld(network, contacts[c].Span, contacts[c].Segment,
                            MyAPIGateway.Session.SessionSettings.WelderSpeedMultiplier * seconds, t.Block.GetInventory(), t, false,
                            tick);
                    else
                        Grind(network, contacts[c].Span, contacts[c].Segment,
                            MyAPIGateway.Session.SessionSettings.GrinderSpeedMultiplier * seconds, t.Block.GetInventory(), tick);
                contacts.Clear();
            }
            // Due to be stored, or no longer needed (gone, built, not worked on for a while).
            foreach (KeyValuePair<Span, Work> pair in work)
                if (!pair.Key.Bound || tick - pair.Value.Committed >= (pair.Value.Dirty ? CommitTicks : 600))
                    finished.Add(pair.Key);
            for (int i = 0; i < finished.Count; i++)
            {
                Work w = work[finished[i]];
                if (finished[i].Bound && w.Dirty)
                    Commit(network, finished[i], w, tick);
                else
                    work.Remove(finished[i]);
            }
            finished.Clear();
        }

        // The span's work, started from its record (a built one: every segment finished, when `built` may).
        static Work WorkOf(Span span, TrackLayout layout, bool built, int tick)
        {
            Work w;
            if (work.TryGetValue(span, out w) && w.Revision == span.WeldRevision && w.Done.Length == layout.Segments)
                return w;
            float[] done = span.Weld(layout);
            if (done == null)
            {
                if (!built)
                    return null;
                done = new float[layout.Segments];
                for (int g = 0; g < done.Length; g++)
                    done[g] = 1;
            }
            else
                done = (float[])done.Clone();
            float[] lost = span.Record.Weld != null ? span.Record.Weld.ExpandLost(done.Length) : new float[done.Length];
            w = new Work { Done = done, Lost = lost, Stock = span.Stock, Revision = span.WeldRevision, Committed = tick };
            work[span] = w;
            return w;
        }

        // Segment g. amount: seconds of welding at speed 1. ship: a ship welder (the rest from its conveyors). free: no
        // components needed (TrackNetwork.FreeRails).
        static void Weld(TrackNetwork network, Span span, int g, float amount, IMyInventory inventory, ShipToolLogic ship,
            bool free, int tick)
        {
            TrackLayout layout = network.LayoutOf(span);
            Work w = WorkOf(span, layout, false, tick);
            if (w == null || g < 0 || g >= w.Done.Length)
                return;
            int cost = layout.Style.SegmentRails;
            bool store = Collapse(w, g);
            // Everything the segment still needs from the inventory into the stock at once, as a vanilla block's stockpile.
            int lacking = free ? 0 : cost - Mounted(w.Done[g], cost) - w.Stock;
            int take = Math.Min(lacking, inventory != null ? (int)inventory.GetItemAmount(RailId) : 0);
            if (free && w.Stock > 0)
            {
                int given = Give(inventory, w.Stock);
                if (given > 0)
                {
                    w.Stock -= given;
                    w.Dirty = store = true;
                }
            }
            if (take > 0)
            {
                inventory.RemoveItemsOfType(take, RailId);
                w.Stock += take;
                w.Dirty = store = true;
            }
            if (ship != null && lacking > Math.Max(take, 0) && ship.Block.UseConveyorSystem && tick - ship.PulledAt >= PullTicks)
            {
                ship.PulledAt = tick;
                int pulled = Pull(ship.Block, inventory, lacking - Math.Max(take, 0));
                if (pulled > 0)
                {
                    w.Stock += pulled;
                    w.Dirty = store = true;
                }
            }
            float rate = 1f / (SegmentSeconds * cost);      // progress per second of welding
            float from = w.Done[g], to = Math.Min(1, from + amount * rate);
            if (!free && Mounted(to, cost) - Mounted(from, cost) > w.Stock)
                to = Math.Min(to, (float)(Mounted(from, cost) + w.Stock) / cost);
            if (to > from)
            {
                if (!free)
                    w.Stock -= Mounted(to, cost) - Mounted(from, cost);
                store |= SpanWeld.Stage(to, layout.Style.Working) != SpanWeld.Stage(from, layout.Style.Working);
                w.Done[g] = to;
                w.Dirty = true;
            }
            if (store)
                Commit(network, span, w, tick);
        }

        // Segment g. amount: seconds of grinding at welding speed 1.
        static void Grind(TrackNetwork network, Span span, int g, float amount, IMyInventory inventory, int tick)
        {
            TrackLayout layout = network.LayoutOf(span);
            Work w = WorkOf(span, layout, true, tick);
            if (g < 0 || g >= w.Done.Length)
                return;
            int cost = layout.Style.SegmentRails;
            bool store = Collapse(w, g);
            // The stock first, as far as the inventory has room.
            int given = Give(inventory, w.Stock);
            w.Stock -= given;
            store |= given > 0;
            float rate = 1f / (SegmentSeconds * cost);
            float from = w.Done[g], to = Math.Max(0, from - amount * rate);
            if (to < from)
            {
                int back = Mounted(from, cost) - Mounted(to, cost);
                if (back > 0)
                {
                    int out_ = Give(inventory, back);
                    w.Stock += back - out_;
                    store = true;
                }
                store |= SpanWeld.Stage(to, layout.Style.Working) != SpanWeld.Stage(from, layout.Style.Working);
                w.Done[g] = to;
                w.Dirty = true;
            }
            if (store)
                Commit(network, span, w, tick);
        }

        // ---- damage (SpanDamage) ----

        static float railIntegrity;

        // Integrity of a weld segment welded full: its Rail components' (TrackStyle.SegmentRails x the component's
        // MaxIntegrity), as a vanilla block's is its components'.
        static float SegmentIntegrity(TrackStyle style)
        {
            if (railIntegrity <= 0)
            {
                MyComponentDefinition def = MyDefinitionManager.Static.GetComponentDefinition(RailId);
                railIntegrity = def != null && def.MaxIntegrity > 0 ? def.MaxIntegrity : 25;
            }
            return style.SegmentRails * railIntegrity;
        }

        // Server: `damage` (integrity points, as on a block) to segment g: its integrity goes down, the stage shown stays
        // (SpanWeld.Lost) until a tool touches it (Collapse). Under TrackStyle.Working it stops carrying trains (and
        // smokes if built finished); a built rail becomes one being built. Down to nothing the segment is destroyed: stage 0
        // at once, everything in it lost; the last segment with anything in it destroyed takes the span with it, for good
        // (its stock too). A segment at stage 0 takes no damage (bullets fly through it, SpanDamage). False: the span is gone.
        public static bool Damage(TrackNetwork network, Span span, int g, float damage, int tick)
        {
            TrackLayout layout = network.LayoutOf(span);
            Work w = WorkOf(span, layout, true, tick);
            if (w == null || g < 0 || g >= w.Done.Length || damage <= 0 || w.Done[g] <= 0)
                return true;
            float from = Math.Max(0, w.Done[g] - w.Lost[g]), to = from - damage / SegmentIntegrity(layout.Style);
            w.Dirty = true;
            if (to <= 0)
            {
                w.Done[g] = w.Lost[g] = 0;
                bool any = false;
                for (int k = 0; k < w.Done.Length && !any; k++)
                    any = w.Done[k] > 0;
                if (!any)
                {
                    work.Remove(span);
                    network.Unlink(span);
                    return false;
                }
                Commit(network, span, w, tick);
                return true;
            }
            w.Lost[g] = w.Done[g] - to;
            if (SpanWeld.Stage(to, layout.Style.Working) != SpanWeld.Stage(from, layout.Style.Working))
                Commit(network, span, w, tick);
            return true;
        }

        // A tool touches a damaged segment: its build level drops to what is left of it, the components over that are
        // gone (vanilla: the welder shows a damaged block's real stage). True: it was damaged.
        static bool Collapse(Work w, int g)
        {
            if (w.Lost[g] <= 0)
                return false;
            w.Done[g] = Math.Max(0, w.Done[g] - w.Lost[g]);
            w.Lost[g] = 0;
            w.Dirty = true;
            return true;
        }

        // Puts up to `count` Rail components into the inventory; how many fit.
        static int Give(IMyInventory inventory, int count)
        {
            if (inventory == null)
                return 0;
            int fit = count;
            while (fit > 0 && !inventory.CanItemsBeAdded(fit, RailItem))
                fit--;
            if (fit > 0)
                inventory.AddItems(fit, new MyObjectBuilder_Component { SubtypeName = RailId.SubtypeName });
            return fit;
        }

        static readonly List<IMyTerminalBlock> stores = new List<IMyTerminalBlock>();

        // Takes up to `count` Rail components out of the inventories the tool's one is connected to over the conveyors
        // (in its terminal grid group); how many.
        static int Pull(IMyShipToolBase tool, IMyInventory own, int count)
        {
            int pulled = 0;
            var terminal = MyAPIGateway.TerminalActionsHelper.GetTerminalSystemForGrid(tool.CubeGrid);
            if (terminal == null)
                return 0;
            stores.Clear();
            terminal.GetBlocksOfType(stores, b => b.HasInventory && b != tool);
            for (int i = 0; i < stores.Count && pulled < count; i++)
                for (int k = 0; k < stores[i].InventoryCount && pulled < count; k++)
                {
                    IMyInventory inventory = stores[i].GetInventory(k);
                    int have = (int)inventory.GetItemAmount(RailId);
                    if (have <= 0 || !inventory.CanTransferItemTo(own, RailItem))
                        continue;
                    int take = Math.Min(have, count - pulled);
                    inventory.RemoveItemsOfType(take, RailId);
                    pulled += take;
                }
            stores.Clear();
            return pulled;
        }

        static void Commit(TrackNetwork network, Span span, Work w, int tick)
        {
            network.SetWeld(span, w.Done, w.Stock, w.Lost);
            w.Revision = span.WeldRevision;
            w.Committed = tick;
            w.Dirty = false;
            if (span.Record.Weld == null)
                work.Remove(span);
        }

        // Components in a segment welded this far (each goes in as the welding gets to its share).
        public static int Mounted(float done, int cost)
        {
            return done <= 0 ? 0 : Math.Min(cost, (int)Math.Ceiling(done * cost - 1e-4f));
        }

        // Rail components in a span: welded into its segments (what damage left of them; all of them, as grinding would
        // give them back) and in stock; with the work not stored yet. A prototype has none; a span not bound counts its stored segments only.
        public static int Contents(TrackNetwork network, Span span)
        {
            if (!span.Record.WantRail)
                return 0;
            int cost = span.Owner.Style.SegmentRails;
            float[] done, lost = null;
            int stock;
            Work w;
            if (span.Bound)
            {
                TrackLayout layout = network.LayoutOf(span);
                if (work.TryGetValue(span, out w) && w.Revision == span.WeldRevision && w.Done.Length == layout.Segments)
                {
                    done = w.Done;
                    lost = w.Lost;
                    stock = w.Stock;
                }
                else
                {
                    done = span.Integrity(layout);
                    stock = span.Stock;
                    if (done == null)
                        return layout.Segments * cost;
                }
            }
            else
            {
                SpanWeld weld = span.Record.Weld;
                if (weld == null || weld.Runs == null)
                    return weld != null ? weld.Stock : 0;
                int segments = 0;
                for (int r = 0; r < weld.Runs.Length; r++)
                    segments += weld.Runs[r];
                done = weld.Expand(segments);
                lost = weld.ExpandLost(segments);
                stock = weld.Stock;
            }
            int n = stock;
            for (int g = 0; g < done.Length; g++)
                n += Mounted(lost != null ? done[g] - lost[g] : done[g], cost);
            return n;
        }

        // Within this of a ground off block, a player shooting a hand grinder is the one who cut it.
        const double SpillGrinderRange = 10;

        // Drops `count` Rail components from the spans of a block that is gone (TrackNetwork.Removed), as one pile: in
        // front of the player who ground it off with a hand grinder, else (a ship grinder, destroyed) where it stood.
        public static void Spill(int count, IMyCubeBlock block)
        {
            Vector3D at = block.WorldAABB.Center;
            MatrixD frame = block.WorldMatrix;
            IMyCharacter cutter = null;
            double best = SpillGrinderRange;
            players.Clear();
            MyAPIGateway.Players.GetPlayers(players);
            for (int i = 0; i < players.Count; i++)
            {
                IMyCharacter character = players[i].Character;
                if (character == null || character.IsDead)
                    continue;
                IMyAngleGrinder tool = character.EquippedTool as IMyAngleGrinder;
                double d = Vector3D.Distance(character.WorldMatrix.Translation, at);
                if (tool != null && tool.IsShooting && d < best)
                {
                    best = d;
                    cutter = character;
                }
            }
            players.Clear();
            if (cutter != null)
            {
                frame = cutter.WorldMatrix;
                at = frame.Translation + frame.Up * 1.2 + frame.Forward * 1.0;
            }
            var item = new MyObjectBuilder_FloatingObject
            {
                Item = new MyObjectBuilder_InventoryItem
                {
                    Amount = count,
                    PhysicalContent = new MyObjectBuilder_Component { SubtypeName = RailId.SubtypeName },
                },
                PositionAndOrientation = new MyPositionAndOrientation(at, frame.Forward, frame.Up),
                PersistentFlags = MyPersistentEntityFlags2.InScene,
            };
            MyAPIGateway.Entities.CreateFromObjectBuilderAndAdd(item);
        }

        // ---- aim (server and client) ----

        static void ToolNumbers(IMyEngineerToolBase tool, out float speed, out float distance)
        {
            MyDefinitionId id = tool.DefinitionId;
            MyEngineerToolBaseDefinition def = MyDefinitionManager.Static.TryGetHandItemDefinition(ref id) as MyEngineerToolBaseDefinition;
            speed = def != null ? def.SpeedMultiplier : 1;
            distance = def != null ? def.DistanceMultiplier : 1;
        }

        static readonly List<IHitInfo> hits = new List<IHitInfo>();

        // The weld segment of a rail (building: only of one being built) the character's head aims at within reach: the
        // nearest collision chord's box (the section's face outline, TrackStyle.Face*) the ray hits. None (false, the span
        // still given) where the ray meets a block first: the tool works on that one.
        static bool Aim(TrackNetwork network, IMyCharacter character, double reach, bool building, out Span span, out int segment,
            out double at)
        {
            MatrixD head = character.GetHeadMatrix(false, true);
            Vector3D origin = head.Translation;
            RayD ray = new RayD(origin, head.Forward);
            span = null;
            segment = -1;
            at = reach;
            for (int i = 0; i < network.Spans.Count; i++)
            {
                Span s = network.Spans[i];
                if (!s.Bound || !s.IsRail || (building && s.Record.Weld == null))
                    continue;
                TrackLayout layout = network.LayoutOf(s);
                MatrixD frame = TrackNetwork.FrameMatrix(s);
                BoundingSphereD bounds = layout.Curve.Bounds;
                if (Vector3D.Distance(Vector3D.Transform(bounds.Center, frame), origin) - bounds.Radius > reach)
                    continue;
                TrackStyle style = s.Owner.Style;
                for (int j = 0; j < layout.Collision.Count; j++)
                {
                    double half = layout.CollisionHalf[j];
                    MatrixD m = layout.Collision[j] * frame;
                    if (Vector3D.Distance(m.Translation, origin) > reach + half + 2)
                        continue;
                    BoundingBoxD box = new BoundingBoxD(new Vector3D(-style.FaceHalfWidth, style.FaceBottom, -half),
                        new Vector3D(style.FaceHalfWidth, style.FaceTop, half));
                    MatrixD inverse = MatrixD.Invert(m);
                    double? hit = box.Intersects(new RayD(Vector3D.Transform(ray.Position, inverse), Vector3D.TransformNormal(ray.Direction, inverse)));
                    if (hit.HasValue && hit.Value < at)
                    {
                        at = hit.Value;
                        span = s;
                        segment = layout.CollisionSegment[j];
                    }
                }
            }
            // false with the span still set: a block in front of it.
            return span != null && !BlockFirst(character, origin, ray.Direction, at);
        }

        // The physics ray from the head meets a block (a grid, or a part of one) closer than `distance`.
        static bool BlockFirst(IMyCharacter character, Vector3D origin, Vector3D direction, double distance)
        {
            hits.Clear();
            MyAPIGateway.Physics.CastRay(origin, origin + direction * distance, hits);
            bool block = false;
            for (int i = 0; i < hits.Count && !block; i++)
            {
                IMyEntity e = hits[i].HitEntity;
                if (e == null || e == character)
                    continue;
                block = e is IMyCubeGrid || e.GetTopMostParent() is IMyCubeGrid;
            }
            hits.Clear();
            return block;
        }

        struct Contact
        {
            public Span Span;
            public int Segment;
            public Vector3D Point;      // nearest point of the segment to the sphere's centre
            public double Distance;
        }

        static readonly List<Contact> contacts = new List<Contact>();

        // The segments a ship tool's sphere touches (the collision chords' face boxes, as Aim) that it has work on (a
        // welder: rails being built, a segment not welded full; a grinder: anything in it), each with its point nearest
        // to the centre. skip: rails of this grid are left alone.
        static void Touch(TrackNetwork network, BoundingSphereD sphere, bool welder, IMyCubeGrid skip, List<Contact> into)
        {
            for (int i = 0; i < network.Spans.Count; i++)
            {
                Span s = network.Spans[i];
                if (!s.Bound || !s.IsRail || (welder && s.Record.Weld == null) || (skip != null && s.Owner.Block.CubeGrid == skip))
                    continue;
                TrackLayout layout = network.LayoutOf(s);
                MatrixD frame = TrackNetwork.FrameMatrix(s);
                BoundingSphereD bounds = layout.Curve.Bounds;
                if (Vector3D.Distance(Vector3D.Transform(bounds.Center, frame), sphere.Center) - bounds.Radius > sphere.Radius)
                    continue;
                float[] done = s.Integrity(layout);
                TrackStyle style = s.Owner.Style;
                int first = into.Count;
                for (int j = 0; j < layout.Collision.Count; j++)
                {
                    int g = layout.CollisionSegment[j];
                    float d0 = done != null ? done[g] : 1;
                    if (welder ? d0 >= 1 : d0 <= 0 && s.Stock == 0)
                        continue;
                    double half = layout.CollisionHalf[j];
                    MatrixD m = layout.Collision[j] * frame;
                    if (Vector3D.Distance(m.Translation, sphere.Center) > sphere.Radius + half + 2)
                        continue;
                    Vector3D local = Vector3D.Transform(sphere.Center, MatrixD.Invert(m));
                    Vector3D near = Vector3D.Clamp(local, new Vector3D(-style.FaceHalfWidth, style.FaceBottom, -half),
                        new Vector3D(style.FaceHalfWidth, style.FaceTop, half));
                    double d = Vector3D.Distance(local, near);
                    if (d > sphere.Radius)
                        continue;
                    // A segment of several chords: its nearest one.
                    int k = first;
                    while (k < into.Count && into[k].Segment != g)
                        k++;
                    if (k < into.Count && into[k].Distance <= d)
                        continue;
                    Contact c = new Contact { Span = s, Segment = g, Point = Vector3D.Transform(near, m), Distance = d };
                    if (k < into.Count)
                        into[k] = c;
                    else
                        into.Add(c);
                }
            }
        }

        // ---- client ----

        static bool infoShown;
        static Span infoSpan;
        static int infoSegment = -1, infoMounted = -1, infoHave = -1, infoStock = -1;
        static ToolEffects hand = new ToolEffects();
        // The span and segment the tool's window shows (for WeldStrip), null: none.
        static Span stripSpan;
        static int stripSegment;

        static int toolsSent = -1;

        // The local player's TrackNetwork.FreeRails to the server, whenever it changes.
        static void ReportTools()
        {
            IMyPlayer player = MyAPIGateway.Session.Player;
            int on = TrackNetwork.FreeRails ? 1 : 0;
            if (player == null || on == toolsSent)
                return;
            toolsSent = on;
            if (Net.IsServer)
                SetToolsOn(player.SteamUserId, on == 1);
            else
                Net.SendCreativeTools(on == 1);
        }

        public static void ClientUpdate(TrackNetwork network)
        {
            ReportTools();
            ShipEffects(network);
            stripSpan = null;
            IMyCharacter character = MyAPIGateway.Session.Player != null ? MyAPIGateway.Session.Player.Character : null;
            IMyEngineerToolBase tool = character != null && !character.IsDead ? character.EquippedTool as IMyEngineerToolBase : null;
            bool welder = tool is IMyWelder;
            if (tool == null || !tool.IsShooting)
                unableSpan = null;
            if (!welder && !(tool is IMyAngleGrinder))
            {
                hand.Stop();
                infoShown = false;
                return;
            }
            float speed, distance;
            ToolNumbers(tool, out speed, out distance);
            Span span;
            int segment;
            double at;
            if (!Aim(network, character, 2 * distance + ReachExtra, false, out span, out segment, out at))
            {
                hand.Stop();
                // Off a span onto nothing: the tool's own window back. Onto a block: the tool writes it itself.
                if (infoShown && span == null)
                    RestoreInfo(tool);
                infoShown = false;
                return;
            }
            int lacking;
            bool stuck = ShowInfo(network, span, segment, character, welder, out lacking);
            stripSpan = span;
            stripSegment = segment;
            MatrixD head = character.GetHeadMatrix(false, true);
            if (tool.IsShooting && !stuck)
                hand.Play((MyEntity)character, head.Translation + head.Forward * at, head.Forward, head.Up,
                    welder ? "WelderContactPoint" : "AngleGrinder", welder ? "ToolPlayWeldMetal" : "ToolPlayGrindMetal");
            else
                hand.Stop();
            if (!welder && tool.IsShooting && !stuck && !character.GetInventory().CanItemsBeAdded(1, RailItem))
                InventoryFull(character);
            if (welder && tool.IsShooting && lacking > 0)
                MissingComponents(network, character, span, segment, lacking);
        }

        // The whole span over the tool's window (Hud.Begin first), while the window shows one of its segments.
        public static void DrawHud(TrackNetwork network)
        {
            if (stripSpan == null || !stripSpan.Bound || !stripSpan.IsRail)
                return;
            TrackLayout layout = network.LayoutOf(stripSpan);
            WeldStrip.Draw(layout, stripSpan.Integrity(layout), stripSegment);
        }

        // As the vanilla grinder's warning (its own is sent by the server, out of a mod's reach): the game's text and voice,
        // again after a pause while it lasts.
        const double FullSeconds = 3;
        static double fullAt = double.NegativeInfinity;
        static MyEntity3DSoundEmitter voice;

        static void InventoryFull(IMyCharacter character)
        {
            double now = MyAPIGateway.Session.ElapsedPlayTime.TotalSeconds;
            if (now - fullAt < FullSeconds)
                return;
            fullAt = now;
            Draw.Notify(MyTexts.GetString(MyStringId.GetOrCompute("AssemblerState_InventoryFull")), MyFontEnum.Red, 2000);
            if (voice == null)
                voice = new MyEntity3DSoundEmitter((MyEntity)character);
            voice.PlaySound(new MySoundPair("HudVocInventoryFull"), false, false, true);
        }

        // As the vanilla welder's (MyWelder): the game's "You need {0} to build {1}" kept up while welding with the
        // components missing (a new notification once the old one has run out), and its "unable" sound once per target
        // and press.
        const int MissingMs = 2500;
        static IMyHudNotification missingNote;
        static string missingText;
        static double missingAt = double.NegativeInfinity;
        static Span unableSpan;
        static int unableSegment;

        static void MissingComponents(TrackNetwork network, IMyCharacter character, Span span, int segment, int count)
        {
            MyComponentDefinition rail = MyDefinitionManager.Static.GetComponentDefinition(RailId);
            string text = string.Format(MyTexts.GetString(MyStringId.GetOrCompute("NotificationMissingComponentToPlaceBlockFormat")),
                rail.DisplayNameText + " (" + count + "x)", Loc.F("MagRail_Weld_Segment", segment + 1, network.LayoutOf(span).Segments));
            double now = MyAPIGateway.Session.ElapsedPlayTime.TotalSeconds;
            if (missingNote != null && text == missingText && now - missingAt < MissingMs / 1000.0 * 0.5)
                missingNote.ResetAliveTime();
            else
            {
                if (missingNote != null)
                    missingNote.Hide();
                missingNote = MyAPIGateway.Utilities.CreateNotification(text, MissingMs, MyFontEnum.Red);
                missingNote.Show();
                missingText = text;
            }
            missingAt = now;
            if (unableSpan == span && unableSegment == segment)
                return;
            unableSpan = span;
            unableSegment = segment;
            if (voice == null)
                voice = new MyEntity3DSoundEmitter((MyEntity)character);
            voice.PlaySound(new MySoundPair("HudUnable"), false, false, true);
        }

        // The tool's window for the aimed segment, as a block. Written only when something in it changed or someone else
        // (the tool, when its own target changed) wrote over it; the DefinitionId setter tells the window to read it
        // again. Returns: the work is stuck (welding: welded full, or no component left to go on with, marked red as the
        // welder does; grinding: nothing left to grind); lacking: the components welding misses there (0: none missing).
        // The bar is blue over CriticalIntegrity (the window's rule): the segment carries trains from TrackStyle.Working
        // on. The stock is the span's: shown as far as the segment needs it. The window shows the component numbers only
        // over 0 integrity: a segment at stage 0 has a hair of it.
        static bool ShowInfo(TrackNetwork network, Span span, int segment, IMyCharacter character, bool welder, out int lacking)
        {
            lacking = 0;
            MyHudBlockInfo info = MyHud.BlockInfo;
            MyComponentDefinition rail = MyDefinitionManager.Static.GetComponentDefinition(RailId);
            TrackLayout layout = network.LayoutOf(span);
            if (rail == null)
                return false;
            float[] weld = span.Integrity(layout);
            float done = weld != null ? weld[segment] : 1;
            int cost = layout.Style.SegmentRails;
            int mounted = Mounted(done, cost);
            int stock = Math.Min(span.Stock, cost - mounted);
            int have = (int)character.GetInventory().GetItemAmount(RailId);
            // Welded up to its last component in: the next one is wanted.
            bool missing = welder && !TrackNetwork.FreeRails && done < 1 && done >= (float)mounted / cost - 1e-4f && stock == 0
                && have == 0;
            bool stuck = welder ? missing || done >= 1 : done <= 0 && span.Stock == 0;
            if (missing)
                lacking = cost - mounted;
            string name = Loc.F("MagRail_Weld_Segment", segment + 1, layout.Segments);
            info.BlockIntegrity = Math.Max(done, 1e-4f);
            info.MissingComponentIndex = missing ? 0 : -1;
            if (infoShown && span == infoSpan && segment == infoSegment && info.BlockName == name && mounted == infoMounted
                && have == infoHave && stock == infoStock)
                return stuck;
            infoShown = true;
            infoSpan = span;
            infoSegment = segment;
            infoMounted = mounted;
            infoHave = have;
            infoStock = stock;
            info.BlockName = name;
            info.BlockIcons = rail.Icons;
            info.PCUCost = 0;
            info.CriticalIntegrity = layout.Style.Working - 0.001f;
            info.CriticalComponentIndex = 0;
            info.OwnershipIntegrity = 0;
            info.BlockBuiltBy = 0;
            info.GridSize = MyCubeSize.Large;
            info.HideComponentLossNotification();
            info.ShowComponentLoss = false;
            info.Components.Clear();
            info.Components.Add(new MyHudBlockInfo.ComponentInfo
            {
                DefinitionId = RailId,
                ComponentName = rail.DisplayNameText,
                Icons = rail.Icons,
                TotalCount = cost,
                MountedCount = mounted,
                StockpileCount = stock,
                AvailableAmount = have,
            });
            info.SetContextHelp(rail);
            info.DefinitionId = RailId;
            return stuck;
        }

        // Back to what the tool shows with no target (MyEngineerToolBase.DrawHud): it writes that only when its own target
        // changes, which aiming off a span is not.
        static void RestoreInfo(IMyEngineerToolBase tool)
        {
            infoSpan = null;
            MyPhysicalItemDefinition def = tool.PhysicalItemDefinition;
            MyHudBlockInfo info = MyHud.BlockInfo;
            info.MissingComponentIndex = -1;
            info.BlockName = def.DisplayNameText;
            info.PCUCost = 0;
            info.BlockIcons = def.Icons;
            info.BlockIntegrity = 1;
            info.CriticalIntegrity = 0;
            info.CriticalComponentIndex = 0;
            info.OwnershipIntegrity = 0;
            info.BlockBuiltBy = 0;
            info.GridSize = MyCubeSize.Small;
            info.Components.Clear();
            info.HideComponentLossNotification();
            info.ShowComponentLoss = false;
            info.SetContextHelp(def);
            info.DefinitionId = def.Id;
        }

        const double ShipEffectsDistance = 100;

        // Ship tools near the camera working on a span: sparks and sound at the nearest segment they get on with (a
        // welder: not finished, components in the span or in the tool; a grinder: anything left in it).
        static void ShipEffects(TrackNetwork network)
        {
            Vector3D camera = MyAPIGateway.Session.Camera != null ? MyAPIGateway.Session.Camera.WorldMatrix.Translation : Vector3D.Zero;
            for (int i = 0; i < ShipTools.Count; i++)
            {
                ShipToolLogic t = ShipTools[i];
                BoundingSphereD sphere;
                Contact best = new Contact { Segment = -1, Distance = double.MaxValue };
                if (t.Working && Vector3D.DistanceSquared(t.Block.GetPosition(), camera) < ShipEffectsDistance * ShipEffectsDistance
                    && t.Sphere(out sphere))
                {
                    Touch(network, sphere, t.Welder, t.Welder ? null : t.Block.CubeGrid, contacts);
                    for (int c = 0; c < contacts.Count; c++)
                        if (contacts[c].Distance < best.Distance && GetsOn(network, contacts[c], t))
                            best = contacts[c];
                    contacts.Clear();
                }
                if (best.Segment < 0)
                {
                    t.Effects.Stop();
                    continue;
                }
                MatrixD m = t.Block.WorldMatrix;
                t.Effects.Play((MyEntity)t.Block, best.Point, m.Forward, m.Up, t.Welder ? "WelderContactPoint" : "AngleGrinder",
                    t.Welder ? "ToolLrgWeldMetal" : "ToolPlayGrindMetal");
            }
        }

        // Touch leaves only segments with work on them: a welder still needs components for it.
        static bool GetsOn(TrackNetwork network, Contact c, ShipToolLogic t)
        {
            if (!t.Welder)
                return true;
            float[] done = c.Span.Integrity(network.LayoutOf(c.Span));
            int cost = c.Span.Owner.Style.SegmentRails;
            return done != null && (done[c.Segment] < (float)Mounted(done[c.Segment], cost) / cost || c.Span.Stock > 0
                || t.Block.GetInventory().GetItemAmount(RailId) > 0);
        }

        public static void Clear()
        {
            work.Clear();
            ShipTools.Clear();
            toolsOn.Clear();
            toolsSent = -1;
            hand = new ToolEffects();
            voice = null;
            fullAt = double.NegativeInfinity;
            missingNote = null;
            missingAt = double.NegativeInfinity;
            unableSpan = null;
            infoShown = false;
            infoSpan = null;
            stripSpan = null;
        }
    }

    // A tool's contact sparks and metal sound where it works on a span (the tool itself, working on no block, shows
    // neither).
    public class ToolEffects
    {
        MyParticleEffect effect;
        string particle;
        MyEntity3DSoundEmitter emitter;

        // forward: the way the tool works (the sparks fly back), up: its up.
        public void Play(MyEntity owner, Vector3D point, Vector3D forward, Vector3D up, string particle, string sound)
        {
            MatrixD m = MatrixD.CreateWorld(point, -forward, up);
            if (effect != null && this.particle != particle)
                Stop();
            if (effect == null)
            {
                MyParticlesManager.TryCreateParticleEffect(particle, ref m, ref point, uint.MaxValue, out effect);
                this.particle = particle;
            }
            else
                effect.WorldMatrix = m;
            // At the point: the emitter keeps its place relative to the camera until updated (SetPosition, Update).
            if (emitter == null)
                emitter = new MyEntity3DSoundEmitter(owner);
            emitter.SetPosition(point);
            if (!emitter.IsPlaying)
                emitter.PlaySound(new MySoundPair(sound), true);
            emitter.Update();
        }

        public void Stop()
        {
            if (effect != null)
                effect.Stop();
            effect = null;
            if (emitter != null && emitter.IsPlaying)
                emitter.StopSound(true);
        }
    }
}
