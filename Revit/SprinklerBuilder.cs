using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.DB.Structure;
using MorphLab.Sprinkler.Core;

namespace MorphLab.Sprinkler.Revit
{
    public class BuildResult
    {
        public int Rooms, Heads, Pipes, Fittings, FittingFailures, Replaced;
        public List<string> Messages = new List<string>();
        public string Summary =>
            $"{Rooms} room(s) · {Heads} heads · {Pipes} pipes · {Fittings} fittings" +
            (FittingFailures > 0 ? $" · {FittingFailures} fitting(s) failed" : "") +
            (Replaced > 0 ? $" · replaced {Replaced} old element(s)" : "");
    }

    /// <summary>Places the planned heads and builds a schedule-sized branch + cross-main network.</summary>
    public class SprinklerBuilder
    {
        readonly Document _doc;
        readonly SprinklerSettings _s;
        readonly FamilyLibrary _lib;
        readonly Dictionary<string, FamilySymbol> _symbols = new Dictionary<string, FamilySymbol>();
        BuildResult _res;
        string _key;
        ElementId _sysId, _pipeTypeId, _levelId;

        public SprinklerBuilder(Document doc, SprinklerSettings s, FamilyLibrary lib) { _doc = doc; _s = s; _lib = lib; }

        public BuildResult Build(IList<RoomPlan> plans)
        {
            _res = new BuildResult();
            var pipeType = Catalog.FindPipeType(_doc, _s.PipeTypeName);
            var sysType = Catalog.FindSystemType(_doc, _s.SystemTypeName);
            if (_s.CreatePiping && (pipeType == null || sysType == null))
                _res.Messages.Add("No pipe type or piping system type in this project — heads only.");

            using (var tg = new TransactionGroup(_doc, "MorphLab · Place sprinklers"))
            {
                tg.Start();

                // ---- families first: load from the library if needed, fix K-Factor, activate
                using (var t = new Transaction(_doc, "MorphLab · Prepare sprinkler families"))
                {
                    t.Start();
                    var msgs = new List<string>();
                    foreach (var pair in plans.Where(p => !p.HasErrors).Select(p => new { p.KType, p.Pendant }).Distinct())
                        _symbols[SlotPick.KeyOf(pair.KType, pair.Pendant)] = FamilyService.Resolve(_doc, _s, _lib, pair.KType, pair.Pendant, msgs);
                    t.Commit();
                    _res.Messages.AddRange(msgs.Distinct());
                }
                foreach (var plan in plans)
                {
                    if (plan.HasErrors || plan.HeadsWorld.Count == 0) continue;
                    using (var t = new Transaction(_doc, "Sprinklers · " + plan.Room.Display))
                    {
                        var fho = t.GetFailureHandlingOptions();
                        fho.SetFailuresPreprocessor(new WarningSwallower());
                        t.SetFailureHandlingOptions(fho);
                        t.Start();
                        try
                        {
                            BuildRoom(plan, pipeType, sysType);
                            t.Commit();
                            _res.Rooms++;
                        }
                        catch (Exception ex)
                        {
                            t.RollBack();
                            plan.Room.Status = "Failed · " + ex.Message;
                            _res.Messages.Add(plan.Room.Display + ": " + ex.Message);
                        }
                    }
                }
                tg.Assimilate();
            }
            return _res;
        }

        // ================================================================== one room
        void BuildRoom(RoomPlan plan, PipeType pipeType, PipingSystemType sysType)
        {
            _key = plan.Room.Key;
            var rule = _s.Rule(plan.KType);

            if (_s.ReplacePrevious)
            {
                var old = Tagging.ElementsForRoom(_doc, _key);
                if (old.Count > 0) { _doc.Delete(old); _res.Replaced += old.Count; }
            }

            var level = _doc.GetElement(plan.Room.HostLevelId) as Level;
            if (level == null) throw new InvalidOperationException("host level not found");

            _symbols.TryGetValue(SlotPick.KeyOf(plan.KType, plan.Pendant), out var sym);
            if (sym == null)
                throw new InvalidOperationException($"no {plan.KType} {(plan.Pendant ? "pendant" : "upright")} sprinkler family (Heads tab)");
            var placement = sym.Family.FamilyPlacementType;
            if (placement != FamilyPlacementType.OneLevelBased && placement != FamilyPlacementType.WorkPlaneBased)
                throw new InvalidOperationException($"'{Catalog.NameOf(sym)}' is face-hosted ({placement}). Load a NON-hosted sprinkler family.");
            if (!sym.IsActive) { sym.Activate(); _doc.Regenerate(); }

            // ---- 1. heads
            var heads = new Dictionary<string, FamilyInstance>();
            for (int i = 0; i < plan.HeadsWorld.Count; i++)
            {
                var w = plan.HeadsWorld[i];
                var fi = _doc.Create.NewFamilyInstance(new XYZ(w.X, w.Y, level.ProjectElevation), sym, level, StructuralType.NonStructural);
                SetOffset(fi, plan.HeadZ - level.ProjectElevation);
                Tagging.Mark(fi, _key);
                heads[KeyOf(plan.HeadsLocal[i])] = fi;
                _res.Heads++;
            }
            _doc.Regenerate();
            foreach (var fi in heads.Values) FixZ(fi, plan.HeadZ);

            plan.Room.Status = $"Placed {heads.Count} heads";
            if (!_s.CreatePiping || plan.Net == null || pipeType == null || sysType == null) return;

            // ---- 2. network
            _sysId = sysType.Id; _pipeTypeId = pipeType.Id; _levelId = level.Id;
            _doc.Regenerate();
            BuildNetwork(plan, rule, heads);
            plan.Room.Status = $"Placed {heads.Count} heads + piping";
        }

        static string KeyOf(P2 local) => Math.Round(U.Mm(local.X)) + "|" + Math.Round(U.Mm(local.Y));

        static void SetOffset(FamilyInstance fi, double offsetFt)
        {
            foreach (var bip in new[] { BuiltInParameter.INSTANCE_ELEVATION_PARAM, BuiltInParameter.INSTANCE_FREE_HOST_OFFSET_PARAM })
            {
                var p = fi.get_Parameter(bip);
                if (p != null && !p.IsReadOnly) { p.Set(offsetFt); return; }
            }
        }

        void FixZ(FamilyInstance fi, double targetZ)
        {
            if (!(fi.Location is LocationPoint lp)) return;
            double dz = targetZ - lp.Point.Z;
            if (Math.Abs(dz) > U.Ft(1)) ElementTransformUtils.MoveElement(_doc, fi.Id, new XYZ(0, 0, dz));
        }

        // ================================================================== branches + cross main
        class Side { public List<Pipe> Segs = new List<Pipe>(); public List<Pipe> Drops = new List<Pipe>(); public List<XYZ> Taps = new List<XYZ>(); public List<FamilyInstance> Heads = new List<FamilyInstance>(); }

        void BuildNetwork(RoomPlan plan, KRule rule, Dictionary<string, FamilyInstance> heads)
        {
            var net = plan.Net;
            double z = plan.PipeZ;
            Func<double, double, XYZ> W = (along, cross) =>
            {
                var w = plan.Frame.ToWorld(net.Local(along, cross));
                return new XYZ(w.X, w.Y, z);
            };

            // downstream head counts for main segments
            var rowCounts = net.Rows.Select(r => r.Count).ToList();
            var mainSegs = new List<Pipe>();
            var junctions = new List<XYZ>();
            var lefts = new List<Side>();
            var rights = new List<Side>();

            XYZ prev = W(net.Feed, net.InletCross);
            for (int k = 0; k < net.Rows.Count; k++)
            {
                var row = net.Rows[k];
                var J = W(net.Feed, row.Cross);
                int downstream = rowCounts.Skip(k).Sum();
                mainSegs.Add(MakePipe(prev, J, rule.SizeFor(downstream)));
                junctions.Add(J);
                prev = J;

                lefts.Add(BuildSide(plan, rule, row, row.Left, J, W, heads));
                rights.Add(BuildSide(plan, rule, row, row.Right, J, W, heads));
            }

            _doc.Regenerate();

            // ---- fittings on the main
            for (int k = 0; k < junctions.Count; k++)
            {
                var J = junctions[k];
                var mIn = Conn(mainSegs[k], J);
                var mOut = k + 1 < mainSegs.Count ? Conn(mainSegs[k + 1], J) : null;
                var L0 = lefts[k].Segs.Count > 0 ? Conn(lefts[k].Segs[0], J) : null;
                var R0 = rights[k].Segs.Count > 0 ? Conn(rights[k].Segs[0], J) : null;

                if (mOut != null)
                {
                    if (L0 != null && R0 != null) Fit(() => _doc.Create.NewCrossFitting(mIn, mOut, L0, R0));
                    else Fit(() => _doc.Create.NewTeeFitting(mIn, mOut, L0 ?? R0));
                }
                else
                {
                    if (L0 != null && R0 != null) Fit(() => _doc.Create.NewTeeFitting(L0, R0, mIn));
                    else Fit(() => _doc.Create.NewElbowFitting(mIn, L0 ?? R0));
                }
            }

            // ---- fittings along branches + drop-to-head connections
            foreach (var side in lefts.Concat(rights))
            {
                for (int i = 0; i < side.Taps.Count; i++)
                {
                    var tap = side.Taps[i];
                    var segEnd = Conn(side.Segs[i], tap);
                    var next = i + 1 < side.Segs.Count ? Conn(side.Segs[i + 1], tap) : null;
                    var drop = side.Drops[i];
                    if (drop == null) continue;
                    var dropStart = Conn(drop, tap);

                    if (next != null) Fit(() => _doc.Create.NewTeeFitting(segEnd, next, dropStart));
                    else Fit(() => _doc.Create.NewElbowFitting(segEnd, dropStart));

                    var head = side.Heads[i];
                    var hc = HeadConnector(head);
                    if (hc != null)
                    {
                        var dEnd = Conn(drop, hc.Origin);
                        try { if (dEnd != null && !dEnd.IsConnected) dEnd.ConnectTo(hc); } catch { }
                    }
                }
            }
        }

        Side BuildSide(RoomPlan plan, KRule rule, BranchRowPlan row, List<double> alongs, XYZ J,
                       Func<double, double, XYZ> W, Dictionary<string, FamilyInstance> heads)
        {
            var side = new Side();
            XYZ from = J;
            for (int i = 0; i < alongs.Count; i++)
            {
                var tap = W(alongs[i], row.Cross);
                side.Segs.Add(MakePipe(from, tap, rule.SizeFor(alongs.Count - i)));
                side.Taps.Add(tap);

                heads.TryGetValue(KeyOf(plan.Net.Local(alongs[i], row.Cross)), out var head);
                side.Heads.Add(head);
                Pipe drop = null;
                var hc = head != null ? HeadConnector(head) : null;
                if (hc != null)
                {
                    var end = new XYZ(tap.X, tap.Y, hc.Origin.Z);
                    if (Math.Abs(hc.Origin.X - tap.X) < U.Ft(5) && Math.Abs(hc.Origin.Y - tap.Y) < U.Ft(5)) end = hc.Origin;
                    drop = MakePipe(tap, end, rule.SizeFor(1));
                }
                side.Drops.Add(drop);
                from = tap;
            }
            return side;
        }

        // ================================================================== helpers
        Pipe MakePipe(XYZ a, XYZ b, int sizeMm)
        {
            if (a.DistanceTo(b) < U.Ft(20)) return null;
            var p = Pipe.Create(_doc, _sysId, _pipeTypeId, _levelId, a, b);
            p.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM)?.Set(U.Ft(sizeMm));
            Tagging.Mark(p, _key);
            _res.Pipes++;
            return p;
        }

        void Fit(Func<FamilyInstance> create)
        {
            try
            {
                var f = create();
                if (f != null) { Tagging.Mark(f, _key); _res.Fittings++; }
                else _res.FittingFailures++;
            }
            catch { _res.FittingFailures++; }
        }

        public static Connector Conn(Element e, XYZ near)
        {
            if (e == null) return null;
            ConnectorSet set = null;
            if (e is MEPCurve c) set = c.ConnectorManager?.Connectors;
            else if (e is FamilyInstance fi) set = fi.MEPModel?.ConnectorManager?.Connectors;
            if (set == null) return null;
            Connector best = null; double bd = double.MaxValue;
            foreach (Connector cn in set)
            {
                if (cn.ConnectorType != ConnectorType.End) continue;
                double d = cn.Origin.DistanceTo(near);
                if (d < bd) { bd = d; best = cn; }
            }
            return best;
        }

        static Connector HeadConnector(FamilyInstance head)
        {
            var set = head?.MEPModel?.ConnectorManager?.Connectors;
            if (set == null) return null;
            foreach (Connector c in set) if (c.Domain == Domain.DomainPiping) return c;
            return null;
        }
    }

    /// <summary>Silently accepts Revit warnings (e.g. "slightly off axis") during generation.</summary>
    public class WarningSwallower : IFailuresPreprocessor
    {
        public FailureProcessingResult PreprocessFailures(FailuresAccessor a)
        {
            foreach (var f in a.GetFailureMessages())
                if (f.GetSeverity() == FailureSeverity.Warning) a.DeleteWarning(f);
            return FailureProcessingResult.Continue;
        }
    }
}
