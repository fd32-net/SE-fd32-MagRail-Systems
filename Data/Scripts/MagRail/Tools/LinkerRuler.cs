using System;
using System.Collections.Generic;
using Sandbox.ModAPI;
using VRage;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRage.Library.Utils;
using VRage.ModAPI;
using VRageMath;

namespace MagRail
{
    // The running rail faces of the Rail Linker (model ModelSource/blocks/RailLinker.py): subparts Face0, Face1, ... -
    // glowing outlines of the wide rail's section - run forwards along the head top from the dummy profile_start to
    // profile_end, growing in at the start and shrinking away at the end; every frame, clients only, visual. Being parts
    // of the tool they are drawn with it, in first person too (the game draws a first person tool over everything,
    // billboards included). They and the glowing floors of the head's end recesses (material Emissive) take the
    // Linker's state colour; other players' Linkers run in the idle colour.
    public static class LinkerRuler
    {
        const string StartDummy = "profile_start", EndDummy = "profile_end", FacePrefix = "Face";
        const double Speed = 0.06;          // m/s
        const double Fade = 0.2;            // share of the track at each end where a face grows in or shrinks away
        const float MinScale = 0.001f;
        const string GlowMaterial = "Emissive";
        const float GlowEmissivity = 1;
        // The glow is set again this often even without a change: a subpart whose render object is not there yet
        // ignores it (seen: one face of the run stayed dark).
        const double RelightSeconds = 0.5;
        const double OthersRange = 100;     // other players' Linkers animate this close to the camera
        const int PlayersRefresh = 60;      // frames

        static readonly Dictionary<string, IMyModelDummy> dummies = new Dictionary<string, IMyModelDummy>();
        static string model;                // the model the track was read from
        static bool known;
        static Vector3 start, end;
        // The colour each tool entity's glow was last set to and when (a tool entity lives while it is in hand).
        static readonly Dictionary<long, MyTuple<Color, double>> lit = new Dictionary<long, MyTuple<Color, double>>();
        static readonly List<IMyPlayer> players = new List<IMyPlayer>();
        static int playersAge = PlayersRefresh;
        // A clock that runs every frame (ElapsedPlayTime moves only with the simulation: steps at over 60 FPS).
        static readonly MyGameTimer clock = new MyGameTimer();

        // The local player's Linker, in its state colour.
        public static void Draw(IMyEntity tool, Color color)
        {
            MyEntity e = tool as MyEntity;
            if (e == null || tool.Model == null || !Read(tool.Model))
                return;
            double now = clock.Elapsed.Seconds;
            MyTuple<Color, double> was;
            bool relight = !lit.TryGetValue(tool.EntityId, out was) || was.Item1 != color || now - was.Item2 >= RelightSeconds;
            if (relight)
            {
                if (lit.Count > 64)
                    lit.Clear();
                lit[tool.EntityId] = MyTuple.Create(color, now);
                tool.SetEmissiveParts(GlowMaterial, color, GlowEmissivity);
            }
            Vector3 along = end - start;
            float length = along.Length();
            if (length < 0.001f)
                return;
            Vector3 forward = along / length;
            Vector3 right = Vector3.Normalize(Vector3.Cross(forward, Vector3.Up));
            Vector3 up = Vector3.Cross(right, forward);
            int count = 0;
            MyEntitySubpart face;
            while (e.TryGetSubpart(FacePrefix + count, out face))
                count++;
            double run = now * Speed / length;
            for (int i = 0; i < count; i++)
            {
                e.TryGetSubpart(FacePrefix + i, out face);
                double t = run + (double)i / count;
                t -= Math.Floor(t);
                float grow = (float)Math.Min(1, Math.Min(t, 1 - t) / Fade);
                float scale = Math.Max(MinScale, grow * grow * (3 - 2 * grow));
                Matrix local = Matrix.CreateScale(scale) * Matrix.CreateWorld(start + along * (float)t, forward, up);
                face.PositionComp.SetLocalMatrix(ref local);
                // Shrunk to nothing at the end of the track a face loses its glow (the game resets it): lit again
                // all the while it grows in at the start.
                if (relight || t < Fade)
                    face.SetEmissiveParts(GlowMaterial, color, GlowEmissivity);
            }
        }

        // Other players' Linkers near the camera, in the idle colour (their state is theirs).
        public static void DrawOthers(Vector3D camera, Color idle)
        {
            if (++playersAge >= PlayersRefresh)
            {
                playersAge = 0;
                players.Clear();
                MyAPIGateway.Players.GetPlayers(players);
            }
            IMyCharacter own = MyAPIGateway.Session.Player != null ? MyAPIGateway.Session.Player.Character : null;
            foreach (IMyPlayer p in players)
            {
                IMyCharacter c = p.Character;
                if (c == null || c == own || c.MarkedForClose || !RailLinker.IsHolding(c)
                    || Vector3D.DistanceSquared(c.WorldMatrix.Translation, camera) > OthersRange * OthersRange)
                    continue;
                Draw(c.EquippedTool, idle);
            }
        }

        static bool Read(IMyModel m)
        {
            if (m.AssetName == model)
                return known;
            model = m.AssetName;
            known = false;
            dummies.Clear();
            m.GetDummies(dummies);
            IMyModelDummy s, e;
            if (dummies.TryGetValue(StartDummy, out s) && dummies.TryGetValue(EndDummy, out e))
            {
                start = s.Matrix.Translation;
                end = e.Matrix.Translation;
                known = true;
            }
            dummies.Clear();
            return known;
        }
    }
}
