using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;

namespace MorphLab.Sprinkler.Revit
{
    /// <summary>Loaded sprinkler families, pipe types and fire piping systems, named "Family : Type".</summary>
    public class Catalog
    {
        public List<string> Sprinklers = new List<string>();
        public List<string> PipeTypes = new List<string>();
        public List<string> SystemTypes = new List<string>();
        public Dictionary<string, string> SprinklerPlacement = new Dictionary<string, string>();
        /// <summary>Sprinkler types loaded in the project with their detected K and orientation.</summary>
        public List<ProjSprinkler> Projects = new List<ProjSprinkler>();

        public class ProjSprinkler { public string Name; public string K; public string Orientation; public string FamilyName; }

        public static string NameOf(FamilySymbol s) => s.FamilyName + " : " + s.Name;

        public static Catalog Read(Document doc, MorphLab.Sprinkler.Core.FamilyLibrary lib = null)
        {
            var c = new Catalog();
            foreach (var s in new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Sprinklers)
                         .OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>())
            {
                var n = NameOf(s);
                c.Sprinklers.Add(n);
                c.SprinklerPlacement[n] = s.Family?.FamilyPlacementType.ToString() ?? "";
                c.Projects.Add(new ProjSprinkler
                {
                    Name = n, FamilyName = s.FamilyName,
                    K = FamilyService.KOf(s, lib), Orientation = FamilyService.OrientationOf(s, lib)
                });
            }
            c.Sprinklers.Sort(StringComparer.OrdinalIgnoreCase);

            c.PipeTypes = new FilteredElementCollector(doc).OfClass(typeof(PipeType))
                .Cast<PipeType>().Select(p => p.Name).OrderBy(n => n).ToList();

            var sys = new FilteredElementCollector(doc).OfClass(typeof(PipingSystemType)).Cast<PipingSystemType>().ToList();
            c.SystemTypes = sys.OrderBy(t => IsFire(t) ? 0 : 1).ThenBy(t => t.Name).Select(t => t.Name).ToList();
            return c;
        }

        static bool IsFire(PipingSystemType t)
        {
            var k = t.SystemClassification;
            return k == MEPSystemClassification.FireProtectWet || k == MEPSystemClassification.FireProtectDry ||
                   k == MEPSystemClassification.FireProtectPreaction || k == MEPSystemClassification.FireProtectOther;
        }

        // ---------------------------------------------------------------- lookups
        public static FamilySymbol FindSymbol(Document doc, string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            return new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Sprinklers)
                .OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
                .FirstOrDefault(s => NameOf(s) == name);
        }

        public static PipeType FindPipeType(Document doc, string name)
        {
            var all = new FilteredElementCollector(doc).OfClass(typeof(PipeType)).Cast<PipeType>().ToList();
            return all.FirstOrDefault(p => p.Name == name) ?? all.FirstOrDefault();
        }

        public static PipingSystemType FindSystemType(Document doc, string name)
        {
            var all = new FilteredElementCollector(doc).OfClass(typeof(PipingSystemType)).Cast<PipingSystemType>().ToList();
            return all.FirstOrDefault(p => p.Name == name)
                ?? all.FirstOrDefault(IsFire)
                ?? all.FirstOrDefault();
        }
    }
}
