using System;
using System.Collections.Generic;
using System.Linq;

namespace MorphLab.Sprinkler.Core
{
    /// <summary>
    /// Pure math (no Revit calls) — turns a room outline + K-rule into a sprinkler grid and a
    /// centre/end-fed branch + cross-main layout. Kept free of the API so it is fast and safe
    /// to run for previews.
    /// </summary>
    public static class GridPlanner
    {
        // ================================================================== public entry
        /// <param name="available">Optional: (K, pendant) → is a family available? Lets the plan switch
        /// orientation instead of failing later (e.g. no K160 pendant → K160 upright).</param>
        public static RoomPlan Plan(RoomInfo room, SprinklerSettings s, Func<string, bool, bool> available = null)
        {
            var plan = new RoomPlan { Room = room };

            // ---- K-type (notes §5: double-height -> K160, manual override wins)
            plan.KType = room.KOverride != null && room.KOverride != "Auto"
                ? room.KOverride
                : (room.HeightMm >= s.DoubleHeightThresholdMm ? s.DoubleHeightKType : s.NormalKType);
            var rule = s.Rule(plan.KType);

            // ---- orientation: under a found false ceiling -> pendant, open deck/roof -> upright
            string ho = room.HeadOverride ?? "Auto";
            if (ho == "Upright") plan.Pendant = false;
            else if (ho == "Pendant") plan.Pendant = true;
            else if (s.Orientation == HeadOrientation.Upright) plan.Pendant = false;
            else if (s.Orientation == HeadOrientation.Pendant) plan.Pendant = true;
            else plan.Pendant = room.CeilingFound;

            if (available != null && !available(plan.KType, plan.Pendant))
            {
                string want = plan.Pendant ? "pendant" : "upright";
                if (available(plan.KType, !plan.Pendant))
                {
                    plan.Pendant = !plan.Pendant;
                    plan.Warnings.Add($"No {plan.KType} {want} family — using {plan.KType} {(plan.Pendant ? "pendant" : "upright")} instead.");
                }
                else
                {
                    plan.HasErrors = true;
                    plan.Warnings.Add($"No {plan.KType} sprinkler family — add or create one in the Heads tab.");
                }
            }

            if (room.Outline.Count < 3) { plan.HasErrors = true; plan.Warnings.Add("Room has no usable boundary."); return plan; }

            // ---- local frame aligned with the dominant wall
            double ang = Poly.DominantAngle(room.Outline);
            plan.Frame = new Frame(room.Outline[0], ang);
            var local = room.Outline.Select(p => plan.Frame.ToLocal(p)).ToList();
            var holesLocal = room.Holes.Select(h => h.Select(p => plan.Frame.ToLocal(p)).ToList()).ToList();
            plan.MinX = local.Min(p => p.X); plan.MaxX = local.Max(p => p.X);
            plan.MinY = local.Min(p => p.Y); plan.MaxY = local.Max(p => p.Y);

            double Lx = U.Mm(plan.MaxX - plan.MinX), Ly = U.Mm(plan.MaxY - plan.MinY);

            // ---- solve each axis, then tighten for coverage
            var ax = SolveAxis(Lx, rule, s.SpacingRoundingMm, 1);
            var ay = SolveAxis(Ly, rule, s.SpacingRoundingMm, 1);
            if (!ax.Ok) plan.Warnings.Add($"X axis ({Lx:0} mm): {ax.Note}");
            if (!ay.Ok) plan.Warnings.Add($"Y axis ({Ly:0} mm): {ay.Note}");

            if (rule.MaxCoverageM2 > 0)
            {
                for (int guard = 0; guard < 200 && Cov(ax, ay) > rule.MaxCoverageM2 + 1e-6; guard++)
                {
                    bool growX = Pitch(ax) >= Pitch(ay);
                    var tryA = growX ? SolveAxis(Lx, rule, s.SpacingRoundingMm, ax.N + 1) : SolveAxis(Ly, rule, s.SpacingRoundingMm, ay.N + 1);
                    if (tryA.Ok) { if (growX) ax = tryA; else ay = tryA; continue; }
                    var tryB = growX ? SolveAxis(Ly, rule, s.SpacingRoundingMm, ay.N + 1) : SolveAxis(Lx, rule, s.SpacingRoundingMm, ax.N + 1);
                    if (tryB.Ok) { if (growX) ay = tryB; else ax = tryB; continue; }
                    plan.Warnings.Add($"Coverage {Cov(ax, ay):0.0} m² exceeds {rule.MaxCoverageM2:0.0} m² and spacing limits stop more heads.");
                    break;
                }
            }

            plan.Nx = ax.N; plan.Ny = ay.N;
            plan.SxMm = ax.S; plan.SyMm = ay.S; plan.ExMm = ax.E; plan.EyMm = ay.E;
            plan.CoverageM2 = Cov(ax, ay);

            // ---- generate + clip to the real outline
            double wallMinFt = U.Ft(rule.WallMinMm) * 0.98;
            for (int i = 0; i < ax.N; i++)
            {
                double x = plan.MinX + U.Ft(ax.E + i * ax.S);
                for (int j = 0; j < ay.N; j++)
                {
                    double y = plan.MinY + U.Ft(ay.E + j * ay.S);
                    var p = new P2(x, y);
                    bool ok = Poly.Contains(local, p) && !holesLocal.Any(h => Poly.Contains(h, p))
                              && Poly.EdgeDist(p, local, holesLocal, out _) >= wallMinFt;
                    if (ok) plan.HeadsLocal.Add(p); else plan.Dropped++;
                }
            }
            plan.HeadsWorld = plan.HeadsLocal.Select(p => plan.Frame.ToWorld(p)).ToList();

            if (plan.Dropped > 0)
                plan.Warnings.Add($"{plan.Dropped} grid point(s) fell outside the room or too close to a wall (irregular shape) — run Check after placing.");
            if (plan.HeadsLocal.Count == 0) { plan.HasErrors = true; plan.Warnings.Add("No sprinkler fits inside this room."); }

            // ---- elevations
            double topZ = room.FloorZ + U.Ft(room.HeightMm);
            if (plan.Pendant)
            {
                plan.HeadZ = topZ - U.Ft(s.PendantBelowCeilingMm);
                plan.PipeZ = plan.HeadZ + U.Ft(s.PendantDropMm);
            }
            else
            {
                plan.HeadZ = topZ - U.Ft(s.UprightBelowDeckMm);
                plan.PipeZ = plan.HeadZ - U.Ft(s.UprightRiserNippleMm);
            }
            if (room.HeightSource == "Room" && room.HeightMm < 2000)
                plan.Warnings.Add($"Room height is only {room.HeightMm:0} mm — set the real ceiling height in the Rooms grid.");

            if (plan.HeadsLocal.Count > 0) plan.Net = BuildNetwork(plan, s);
            return plan;
        }

        // ================================================================== axis solver
        struct Axis { public int N; public double E, S; public bool Ok; public string Note; }

        static double Pitch(Axis a) => a.N > 1 ? a.S : a.E * 2;
        static double Cov(Axis x, Axis y) => Pitch(x) * Pitch(y) / 1e6;

        /// <summary>
        /// Fewest heads n (≥ nMin) along a length L so that wall distance e ∈ [WallMin, WallMax]
        /// and head spacing s ∈ [H2HMin, H2HMax]. e is chosen near the classic "half a spacing".
        /// </summary>
        static Axis SolveAxis(double L, KRule r, double round, int nMin)
        {
            if (L < 2 * r.WallMinMm)
                return new Axis { N = 1, E = L / 2, S = 0, Ok = false, Note = "narrower than 2 × min wall distance — one centred head" };

            for (int n = Math.Max(1, nMin); n < 5000; n++)
            {
                if (n == 1)
                {
                    double e1 = L / 2;
                    if (e1 >= r.WallMinMm && e1 <= r.WallMaxMm) return new Axis { N = 1, E = e1, S = 0, Ok = true };
                    continue;
                }
                double lo = Math.Max(r.WallMinMm, (L - (n - 1) * r.HeadToHeadMaxMm) / 2);
                double hi = Math.Min(r.WallMaxMm, (L - (n - 1) * r.HeadToHeadMinMm) / 2);
                if ((L - (n - 1) * r.HeadToHeadMinMm) / 2 < r.WallMinMm) break; // more heads only gets worse
                if (lo > hi + 1e-6) continue;

                double e = Clamp(L / (2.0 * n), lo, hi);
                double sp = (L - 2 * e) / (n - 1);
                if (round > 0)
                {
                    double sr = Math.Floor(sp / round) * round;
                    double er = (L - (n - 1) * sr) / 2;
                    if (sr >= r.HeadToHeadMinMm && er >= lo - 1e-6 && er <= hi + 1e-6) { sp = sr; e = er; }
                }
                return new Axis { N = n, E = e, S = sp, Ok = true };
            }

            // No exact solution: pick the least-bad compromise and say so.
            Axis best = new Axis { N = 1, E = L / 2, Ok = false }; double bestV = double.MaxValue;
            int nMax = (int)Math.Ceiling(L / r.HeadToHeadMinMm) + 2;
            for (int n = Math.Max(1, nMin); n <= nMax; n++)
            {
                double e = n == 1 ? L / 2 : Clamp(L / (2.0 * n), r.WallMinMm, r.WallMaxMm);
                double sp = n == 1 ? 0 : (L - 2 * e) / (n - 1);
                double v = Math.Max(0, e - r.WallMaxMm) + Math.Max(0, r.WallMinMm - e);
                if (n > 1) v += Math.Max(0, r.HeadToHeadMinMm - sp) + Math.Max(0, sp - r.HeadToHeadMaxMm);
                if (v < bestV) { bestV = v; best = new Axis { N = n, E = e, S = sp, Ok = false }; }
            }
            best.Note = $"no layout meets every limit — closest uses {best.N} head(s), wall {best.E:0} mm, spacing {best.S:0} mm";
            return best;
        }

        static double Clamp(double v, double lo, double hi) => v < lo ? lo : (v > hi ? hi : v);

        // ================================================================== branches + main
        static NetPlan BuildNetwork(RoomPlan p, SprinklerSettings s)
        {
            var net = new NetPlan();
            double w = p.MaxX - p.MinX, h = p.MaxY - p.MinY;
            net.BranchAlongX = s.Branches == BranchDirection.AlongLongSide ? w > h : w <= h;

            Func<P2, double> along = q => net.BranchAlongX ? q.X : q.Y;
            Func<P2, double> cross = q => net.BranchAlongX ? q.Y : q.X;

            var alongs = p.HeadsLocal.Select(along).Select(v => Math.Round(v, 4)).Distinct().OrderBy(v => v).ToList();
            double eAlongFt = U.Ft(net.BranchAlongX ? p.ExMm : p.EyMm);

            bool centre = s.Feed == FeedPosition.Centre && alongs.Count >= 2;
            if (centre)
            {
                int mid = alongs.Count / 2;
                net.Feed = (alongs[mid - 1] + alongs[mid]) / 2;
            }
            else net.Feed = alongs[0] - Math.Max(U.Ft(150), eAlongFt / 2);

            var rows = p.HeadsLocal
                .GroupBy(q => Math.Round(U.Mm(cross(q))))
                .Select(g => new BranchRowPlan
                {
                    Cross = cross(g.First()),
                    Left = g.Select(along).Where(a => a < net.Feed).OrderByDescending(a => a).ToList(),
                    Right = g.Select(along).Where(a => a > net.Feed).OrderBy(a => a).ToList()
                })
                .Where(r => r.Count > 0)
                .ToList();

            double stub = U.Ft(Math.Max(100, s.InletStubMm));
            if (s.Inlet == InletSide.Start)
            {
                net.Rows = rows.OrderBy(r => r.Cross).ToList();
                net.InletCross = net.Rows[0].Cross - stub;
            }
            else
            {
                net.Rows = rows.OrderByDescending(r => r.Cross).ToList();
                net.InletCross = net.Rows[0].Cross + stub;
            }
            return net;
        }
    }
}
