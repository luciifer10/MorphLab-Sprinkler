using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using MorphLab.Sprinkler.Core;

namespace MorphLab.Sprinkler.Revit
{
    public class QcIssue
    {
        public ElementId Id { get; set; }
        public string IdText => Id?.ToString() ?? "";
        public string Room { get; set; }
        public string Rule { get; set; }
        public string Detail { get; set; }
        public string Severity { get; set; }   // Fail | Warn
        public bool IsFail => Severity == "Fail";
    }

    public class QcReport
    {
        public int Checked, Passed;
        public List<QcIssue> Issues = new List<QcIssue>();
        public int Fails => Issues.Where(i => i.IsFail).Select(i => i.IdText).Distinct().Count();
        public int Warns => Issues.Where(i => !i.IsFail).Select(i => i.IdText).Distinct().Count();
        public string Summary => $"{Checked} heads checked · {Passed} clean · {Fails} failing · {Warns} warnings";
    }

    /// <summary>Audits the sprinklers in the active view against the notes' K-rules.</summary>
    public static class QcChecker
    {
        class Head { public FamilyInstance E; public P2 P; public RoomInfo Room; public string K; }

        public static QcReport Run(Document doc, View view, List<RoomInfo> rooms, SprinklerSettings s, FamilyLibrary lib)
        {
            var rep = new QcReport();
            var heads = new List<Head>();

            foreach (FamilyInstance fi in new FilteredElementCollector(doc, view.Id)
                         .OfCategory(BuiltInCategory.OST_Sprinklers).WhereElementIsNotElementType())
            {
                if (!(fi.Location is LocationPoint lp)) continue;
                var p = new P2(lp.Point.X, lp.Point.Y);
                var room = rooms
                    .Where(r => lp.Point.Z >= r.FloorZ - U.Ft(300) && lp.Point.Z <= r.FloorZ + U.Ft(r.HeightMm + 1500))
                    .FirstOrDefault(r => Poly.Contains(r.Outline, p) && !r.Holes.Any(h => Poly.Contains(h, p)));
                heads.Add(new Head { E = fi, P = p, Room = room, K = KOf(fi, room, s, lib) });
            }
            rep.Checked = heads.Count;

            foreach (var h in heads)
            {
                var rule = s.Rule(h.K);
                string room = h.Room?.Display ?? "—";
                int before = rep.Issues.Count;

                if (h.Room == null)
                {
                    rep.Issues.Add(Issue(h, room, "Outside rooms", "Not inside any scanned room — scan rooms first, or head is in a void.", "Warn"));
                    continue;
                }
                if (h.Room.IsExcluded)
                    rep.Issues.Add(Issue(h, room, "No-sprinkler room", $"Room matches '{h.Room.ExcludedReason}' — sprinklers not allowed.", "Fail"));

                // neighbours in the same room
                var others = heads.Where(o => !ReferenceEquals(o, h) && ReferenceEquals(o.Room, h.Room)).ToList();
                if (others.Count > 0)
                {
                    double nn = others.Min(o => o.P.Dist(h.P)) * 304.8;
                    if (nn < rule.HeadToHeadMinMm - 1)
                        rep.Issues.Add(Issue(h, room, "Spacing too close", $"{nn:0} mm to nearest head (min {rule.HeadToHeadMinMm:0})", "Fail"));
                    if (nn > rule.HeadToHeadMaxMm + 1)
                        rep.Issues.Add(Issue(h, room, "Spacing too wide", $"{nn:0} mm to nearest head (max {rule.HeadToHeadMaxMm:0})", "Fail"));
                }

                // wall distance
                double dw = Poly.EdgeDist(h.P, h.Room.Outline, h.Room.Holes, out var wallPt);
                double dwMm = dw * 304.8;
                if (dwMm < rule.WallMinMm - 1)
                    rep.Issues.Add(Issue(h, room, "Too close to wall", $"{dwMm:0} mm (min {rule.WallMinMm:0})", "Fail"));
                else if (dwMm > rule.WallMaxMm + 1 && IsPerimeter(h, others, wallPt, dw, rule))
                    rep.Issues.Add(Issue(h, room, "Too far from wall", $"{dwMm:0} mm (max {rule.WallMaxMm:0})", "Fail"));

                if (rep.Issues.Count == before) rep.Passed++;
            }
            return rep;
        }

        /// <summary>True when no other head sits between this head and its nearest wall.</summary>
        static bool IsPerimeter(Head h, List<Head> others, P2 wallPt, double dw, KRule rule)
        {
            var u = (wallPt - h.P) * (1.0 / Math.Max(dw, 1e-9));
            double band = U.Ft(rule.HeadToHeadMinMm) / 2;
            foreach (var o in others)
            {
                var v = o.P - h.P;
                double t = v.Dot(u);
                if (t <= 0 || t >= dw) continue;
                double perp = Math.Abs(v.X * u.Y - v.Y * u.X);
                if (perp < band) return false;
            }
            return true;
        }

        static string KOf(FamilyInstance fi, RoomInfo room, SprinklerSettings s, FamilyLibrary lib)
        {
            if (s.SizingTableMode != "Auto") return s.SizingTableMode;
            return FamilyService.KOf(fi.Symbol, lib);
        }

        static QcIssue Issue(Head h, string room, string rule, string detail, string sev) =>
            new QcIssue { Id = h.E.Id, Room = room, Rule = rule, Detail = detail, Severity = sev };

        public static string WriteCsv(QcReport rep, string folder)
        {
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, "SprinklerQC_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".csv");
            var sb = new StringBuilder("ElementId,Room,Severity,Rule,Detail\n");
            foreach (var i in rep.Issues)
                sb.Append(i.IdText).Append(',').Append(Csv(i.Room)).Append(',').Append(i.Severity).Append(',')
                  .Append(Csv(i.Rule)).Append(',').Append(Csv(i.Detail)).Append('\n');
            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
            return path;
        }

        static string Csv(string v) => "\"" + (v ?? "").Replace("\"", "\"\"") + "\"";
    }
}
