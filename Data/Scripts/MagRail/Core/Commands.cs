using System.Collections.Generic;
using System.Text;
using Sandbox.ModAPI;
using VRageMath;

namespace MagRail
{
    // Chat command /magrail: counts of track blocks and spans, then the spans nearest to the camera.
    public static class Commands
    {
        const int Listed = 8;
        static readonly List<Span> nearest = new List<Span>();

        public static void OnMessage(string text, ref bool sendToOthers)
        {
            if (!text.StartsWith("/magrail"))
                return;
            sendToOthers = false;
            TrackNetwork network = MagRailSession.Instance.Network;
            int rail = 0, prototype = 0, unbound = 0;
            nearest.Clear();
            foreach (Span s in network.Spans)
            {
                if (!s.Bound)
                {
                    unbound++;
                    continue;
                }
                if (s.IsRail)
                    rail++;
                else
                    prototype++;
                nearest.Add(s);
            }
            MyAPIGateway.Utilities.ShowMessage("MagRail", Loc.F("MagRail_Cmd_Summary", network.Blocks.Count, rail, prototype, unbound));

            Vector3D camera = MyAPIGateway.Session.Camera.WorldMatrix.Translation;
            nearest.Sort((a, b) => Distance(network, a, camera).CompareTo(Distance(network, b, camera)));
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < nearest.Count && i < Listed; i++)
            {
                Span s = nearest[i];
                SpanCurve c = network.CurveOf(s);
                sb.Clear();
                sb.Append(s.Owner.Block.CustomName).Append(' ').Append(s.Record.OwnerPort).Append(" — ")
                    .Append(s.Partner.Block.CustomName).Append(' ').Append(s.Record.PartnerPort).Append(": ")
                    .Append(c.Length.ToString("0.0")).Append(" m, R ")
                    .Append(double.IsInfinity(c.MinRadius) ? "∞" : c.MinRadius.ToString("0.0")).Append(" m, ")
                    .Append(Loc.T(s.IsRail ? "MagRail_Mode_Rail" : "MagRail_Mode_Prototype"));
                if (s.Record.WantRail && s.Problem != null)
                    sb.Append(" (").Append(Loc.T(s.Problem)).Append(')');
                MyAPIGateway.Utilities.ShowMessage("MagRail", sb.ToString());
            }
            nearest.Clear();
        }

        static double Distance(TrackNetwork network, Span s, Vector3D camera)
        {
            SpanCurve c = network.CurveOf(s);
            return Vector3D.Distance(Vector3D.Transform(c.Bounds.Center, TrackNetwork.FrameMatrix(s)), camera);
        }
    }
}
