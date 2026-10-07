using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;

namespace MorphLab.Sprinkler.Core
{
    public enum ScanScope { ActiveLevel, AllLevels }

    /// <summary>
    /// Reads rooms from the host model AND every loaded architectural link (MEP models normally
    /// have the rooms in the linked architecture), converts them to host coordinates, finds the
    /// ceiling height and applies the "no sprinkler" room rules.
    /// </summary>
    public static class RoomScanner
    {
        class CeilingBox { public double MinX, MinY, MaxX, MaxY, BottomZ; }

        public static List<RoomInfo> Scan(Document doc, View activeView, ScanScope scope, SprinklerSettings s)
        {
            var result = new List<RoomInfo>();
            var hostLevels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().ToList();
            if (hostLevels.Count == 0) return result;

            Level activeLevel = activeView?.GenLevel;
            var ceilings = s.ReadCeilings ? CollectCeilings(doc) : new List<CeilingBox>();
            var opts = new SpatialElementBoundaryOptions { SpatialElementBoundaryLocation = SpatialElementBoundaryLocation.Finish };

            // ---- host rooms
            foreach (Room r in new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType())
            {
                var info = Build(r, Transform.Identity, false, "H:" + r.UniqueId, opts, hostLevels, ceilings, s);
                if (info != null) result.Add(info);
            }

            // ---- linked rooms
            foreach (RevitLinkInstance link in new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)))
            {
                Document ld = null;
                try { ld = link.GetLinkDocument(); } catch { }
                if (ld == null) continue;
                var tf = link.GetTotalTransform();
                foreach (Room r in new FilteredElementCollector(ld).OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType())
                {
                    var info = Build(r, tf, true, "L:" + link.UniqueId + ":" + r.UniqueId, opts, hostLevels, ceilings, s);
                    if (info != null) result.Add(info);
                }
            }

            if (scope == ScanScope.ActiveLevel && activeLevel != null)
            {
                double z = activeLevel.ProjectElevation;
                result = result.Where(ri => Math.Abs(ri.FloorZ - z) < U.Ft(600) || ri.HostLevelId == activeLevel.Id).ToList();
            }

            return result.OrderBy(r => r.LevelName).ThenBy(r => r.Number).ThenBy(r => r.Name).ToList();
        }

        static RoomInfo Build(Room r, Transform tf, bool linked, string key, SpatialElementBoundaryOptions opts,
                              List<Level> hostLevels, List<CeilingBox> ceilings, SprinklerSettings s)
        {
            if (r.Area <= 0 || r.Location == null) return null; // unplaced / not enclosed
            IList<IList<BoundarySegment>> loops;
            try { loops = r.GetBoundarySegments(opts); } catch { return null; }
            if (loops == null || loops.Count == 0) return null;

            var polys = new List<List<P2>>();
            double floorZ = double.NaN;
            foreach (var loop in loops)
            {
                var pts = new List<P2>();
                foreach (var seg in loop)
                {
                    var c = seg.GetCurve();
                    var tess = c.Tessellate();
                    for (int i = 0; i < tess.Count - 1; i++)
                    {
                        var w = tf.OfPoint(tess[i]);
                        if (double.IsNaN(floorZ)) floorZ = w.Z;
                        var p = new P2(w.X, w.Y);
                        if (pts.Count == 0 || pts[pts.Count - 1].Dist(p) > 1e-4) pts.Add(p);
                    }
                }
                if (pts.Count >= 3) polys.Add(pts);
            }
            if (polys.Count == 0) return null;

            // The largest loop is the outline, everything else is a hole (columns, shafts).
            var outer = polys.OrderByDescending(p => Poly.Area(p)).First();
            var holes = polys.Where(p => !ReferenceEquals(p, outer)).ToList();

            var lvl = hostLevels.OrderBy(l => Math.Abs(l.ProjectElevation - floorZ)).First();

            var info = new RoomInfo
            {
                Key = key,
                Name = r.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? r.Name,
                Number = r.Number,
                IsLinked = linked,
                HostLevelId = lvl.Id,
                HostLevelZ = lvl.ProjectElevation,
                LevelName = lvl.Name,
                FloorZ = floorZ,
                Outline = outer,
                Holes = holes,
                AreaM2 = r.Area * 0.09290304
            };

            // Height: lowest ceiling over the room centre, otherwise the room's own height.
            double roomH = r.UnboundedHeight;
            var centre = Centroid(outer);
            var ceil = ceilings
                .Where(c => centre.X >= c.MinX && centre.X <= c.MaxX && centre.Y >= c.MinY && centre.Y <= c.MaxY)
                .Where(c => c.BottomZ > floorZ + U.Ft(1500) && c.BottomZ < floorZ + roomH + U.Ft(3000))
                .OrderBy(c => c.BottomZ)
                .FirstOrDefault();
            if (ceil != null)
            {
                info.CeilingFound = true;
                info.HeightSource = "Ceiling";
                info.HeightMm = Math.Round(U.Mm(ceil.BottomZ - floorZ));
            }
            else
            {
                info.HeightSource = "Room";
                info.HeightMm = Math.Round(U.Mm(roomH));
            }

            info.AutoK = info.HeightMm >= s.DoubleHeightThresholdMm ? s.DoubleHeightKType : s.NormalKType;
            info.ExcludedReason = MatchExclusion(info.Name, s.ExcludedRoomKeywords);
            info.Include = !info.IsExcluded;
            info.Status = info.IsExcluded ? "No sprinkler · " + info.ExcludedReason : "Ready";
            return info;
        }

        /// <summary>Whole-word match so "UPS" never fires on "GROUPS".</summary>
        public static string MatchExclusion(string roomName, IEnumerable<string> keywords)
        {
            string name = " " + Normalise(roomName) + " ";
            foreach (var k in keywords)
            {
                var nk = Normalise(k);
                if (nk.Length == 0) continue;
                if (name.Contains(" " + nk + " ")) return k.Trim();
            }
            return "";
        }

        static string Normalise(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder();
            foreach (char ch in s.ToUpperInvariant()) sb.Append(char.IsLetterOrDigit(ch) ? ch : ' ');
            return string.Join(" ", sb.ToString().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));
        }

        static P2 Centroid(List<P2> poly)
        {
            double x = 0, y = 0; foreach (var p in poly) { x += p.X; y += p.Y; }
            return new P2(x / poly.Count, y / poly.Count);
        }

        static List<CeilingBox> CollectCeilings(Document doc)
        {
            var list = new List<CeilingBox>();
            void Add(Document d, Transform tf)
            {
                foreach (var e in new FilteredElementCollector(d).OfCategory(BuiltInCategory.OST_Ceilings).WhereElementIsNotElementType())
                {
                    var bb = e.get_BoundingBox(null);
                    if (bb == null) continue;
                    var a = tf.OfPoint(bb.Min); var b = tf.OfPoint(bb.Max);
                    list.Add(new CeilingBox
                    {
                        MinX = Math.Min(a.X, b.X), MaxX = Math.Max(a.X, b.X),
                        MinY = Math.Min(a.Y, b.Y), MaxY = Math.Max(a.Y, b.Y),
                        BottomZ = Math.Min(a.Z, b.Z)
                    });
                }
            }
            Add(doc, Transform.Identity);
            foreach (RevitLinkInstance link in new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)))
            {
                Document ld = null;
                try { ld = link.GetLinkDocument(); } catch { }
                if (ld != null) Add(ld, link.GetTotalTransform());
            }
            return list;
        }
    }
}
