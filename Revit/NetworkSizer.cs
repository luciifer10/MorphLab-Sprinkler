using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using MorphLab.Sprinkler.Core;

namespace MorphLab.Sprinkler.Revit
{
    public class SizeResult
    {
        public int Pipes, Changed, Locked, Heads;
        public bool Looped;
        public List<string> Notes = new List<string>();
        public string Summary =>
            $"{Heads} heads downstream · {Pipes} pipes checked · {Changed} resized · {Locked} locked (kept)" +
            (Looped ? " · ⚠ loop detected" : "");
    }

    /// <summary>
    /// Pipe-schedule sizing of an EXISTING network (drawn by hand or by this plugin):
    /// walk the connectors from the inlet pipe the user picks, count sprinklers downstream of
    /// every pipe and apply the K-table. Size-locked pipes are left alone.
    /// </summary>
    public static class NetworkSizer
    {
        class Node { public Element E; public Node Parent; public List<Node> Kids = new List<Node>(); public int Heads; public int MaxK; }

        /// <param name="upstreamPoint">Point the user clicked near the SUPPLY end of the inlet pipe.
        /// The walk never goes back through that end, so the riser / other zones are not counted.</param>
        public static SizeResult Size(Document doc, Pipe inlet, XYZ upstreamPoint, SprinklerSettings s, FamilyLibrary lib, bool dryRun = false)
        {
            var res = new SizeResult();
            var visited = new HashSet<string> { inlet.Id.ToString() };
            var root = new Node { E = inlet };
            var queue = new Queue<Node>(); queue.Enqueue(root);

            // the inlet connector nearest the click is the supply side — block it
            Connector supply = upstreamPoint != null ? SprinklerBuilder.Conn(inlet, upstreamPoint) : null;

            while (queue.Count > 0)
            {
                var n = queue.Dequeue();
                foreach (var nb in Neighbours(n.E, ReferenceEquals(n, root) ? supply : null))
                {
                    string id = nb.Id.ToString();
                    if (visited.Contains(id))
                    {
                        if (n.Parent == null || n.Parent.E.Id.ToString() != id) res.Looped = true;
                        continue;
                    }
                    visited.Add(id);
                    var child = new Node { E = nb, Parent = n };
                    n.Kids.Add(child);
                    if (!IsSprinkler(nb)) queue.Enqueue(child);
                }
            }

            Count(root, lib);
            res.Heads = root.Heads;
            if (res.Looped)
                res.Notes.Add("The network loops (gridded system). Pipe-schedule sizing assumes a tree — sizes on the loop are only indicative; use hydraulic calculation.");

            if (dryRun) return res;
            using (var t = new Transaction(doc, "MorphLab · Size sprinkler pipes"))
            {
                t.Start();
                Apply(root, s, res);
                t.Commit();
            }
            return res;
        }

        static void Count(Node n, FamilyLibrary lib)
        {
            if (IsSprinkler(n.E))
            {
                n.Heads = 1;
                n.MaxK = SprinklerSettings.KValue(FamilyService.KOf((n.E as FamilyInstance)?.Symbol, lib));
                return;
            }
            foreach (var k in n.Kids) { Count(k, lib); n.Heads += k.Heads; n.MaxK = Math.Max(n.MaxK, k.MaxK); }
        }

        static void Apply(Node n, SprinklerSettings s, SizeResult res)
        {
            if (n.E is Pipe p && n.Heads > 0)
            {
                res.Pipes++;
                if (Tagging.IsLocked(p)) res.Locked++;
                else
                {
                    // Auto: the largest K fed by this pipe decides the table (K80 heads + one K160 -> K160 table)
                    string table = s.SizingTableMode == "Auto" ? "K" + (n.MaxK > 0 ? n.MaxK : 80) : s.SizingTableMode;
                    int mm = s.Rule(table).SizeFor(n.Heads);
                    var prm = p.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM);
                    if (prm != null && !prm.IsReadOnly && Math.Abs(U.Mm(prm.AsDouble()) - mm) > 0.5)
                    {
                        prm.Set(U.Ft(mm));
                        res.Changed++;
                    }
                }
            }
            foreach (var k in n.Kids) Apply(k, s, res);
        }

        static IEnumerable<Element> Neighbours(Element e, Connector skip)
        {
            ConnectorSet set = null;
            if (e is MEPCurve c) set = c.ConnectorManager?.Connectors;
            else if (e is FamilyInstance fi) set = fi.MEPModel?.ConnectorManager?.Connectors;
            if (set == null) yield break;
            foreach (Connector cn in set)
            {
                if (cn.Domain != Domain.DomainPiping || !cn.IsConnected) continue;
                if (skip != null && cn.Origin.IsAlmostEqualTo(skip.Origin)) continue;
                foreach (Connector r in cn.AllRefs)
                {
                    if (r.Owner == null || r.Owner.Id == e.Id) continue;
                    if (r.ConnectorType != ConnectorType.End && r.ConnectorType != ConnectorType.Curve) continue;
                    if (r.Owner is PipingSystem) continue;
                    yield return r.Owner;
                }
            }
        }

        public static bool IsSprinkler(Element e) =>
            e?.Category != null && e.Category.Id.Equals(new ElementId(BuiltInCategory.OST_Sprinklers));


    }
}
