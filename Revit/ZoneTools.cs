using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace MorphLab.Sprinkler.Revit
{
    /// <summary>
    /// Installation-valve zones, like the 7 colour-coded zones on the INOX roof drawing.
    /// The zone name is written to the element's Comments ("ZONE-1"), and one view filter per
    /// zone colours pipes, fittings, accessories and heads.
    /// </summary>
    public static class ZoneTools
    {
        public const string Prefix = "ZONE-";
        const string FilterPrefix = "MLS Sprinkler ";

        // Same order as the reference drawing: yellow, magenta, green, blue, maroon, cyan, orange…
        static readonly Color[] Palette =
        {
            new Color(230, 190, 0), new Color(220, 0, 220), new Color(0, 180, 0), new Color(0, 70, 220),
            new Color(140, 20, 20), new Color(0, 200, 220), new Color(245, 120, 0), new Color(120, 60, 200),
            new Color(0, 130, 120), new Color(200, 80, 120)
        };

        static readonly BuiltInCategory[] Cats =
        {
            BuiltInCategory.OST_PipeCurves, BuiltInCategory.OST_PipeFitting,
            BuiltInCategory.OST_PipeAccessory, BuiltInCategory.OST_Sprinklers
        };

        public static string Normalise(string zone)
        {
            zone = (zone ?? "").Trim().ToUpperInvariant();
            if (zone.Length == 0) return "";
            return zone.StartsWith(Prefix) ? zone : Prefix + zone;
        }

        /// <summary>Writes the zone to Comments. Existing non-zone comments are kept as a suffix.</summary>
        public static int Assign(Document doc, ICollection<ElementId> ids, string zone, out int skipped)
        {
            zone = Normalise(zone); skipped = 0; int n = 0;
            using (var t = new Transaction(doc, "MorphLab · Assign sprinkler zone"))
            {
                t.Start();
                foreach (var id in ids)
                {
                    var e = doc.GetElement(id);
                    var p = e?.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
                    if (p == null || p.IsReadOnly) { skipped++; continue; }
                    p.Set(zone);
                    n++;
                }
                t.Commit();
            }
            return n;
        }

        public static List<string> ZonesInView(Document doc, View view)
        {
            var filter = new ElementMulticategoryFilter(Cats.ToList());
            return new FilteredElementCollector(doc, view.Id).WherePasses(filter).WhereElementIsNotElementType()
                .Select(e => e.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString())
                .Where(v => !string.IsNullOrEmpty(v) && v.StartsWith(Prefix))
                .Distinct().OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>Creates/updates one filter per zone and colours it in the active view.</summary>
        public static string ApplyColours(Document doc, View view)
        {
            var zones = ZonesInView(doc, view);
            if (zones.Count == 0) return "No zones found in this view. Assign zones first.";
            if (view.ViewTemplateId != ElementId.InvalidElementId)
                return "This view uses a view template that controls filters — remove the template or apply colours in the template.";

            var catIds = Cats.Select(c => new ElementId(c)).ToList();
            var existing = new FilteredElementCollector(doc).OfClass(typeof(ParameterFilterElement))
                .Cast<ParameterFilterElement>().ToDictionary(f => f.Name, f => f);
            var solid = new FilteredElementCollector(doc).OfClass(typeof(FillPatternElement)).Cast<FillPatternElement>()
                .FirstOrDefault(f => f.GetFillPattern().IsSolidFill);

            using (var t = new Transaction(doc, "MorphLab · Zone colours"))
            {
                t.Start();
                for (int i = 0; i < zones.Count; i++)
                {
                    string z = zones[i];
                    var rule = ParameterFilterRuleFactory.CreateEqualsRule(new ElementId(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS), z);
                    var ef = new ElementParameterFilter(rule);
                    string name = FilterPrefix + z;
                    if (!existing.TryGetValue(name, out var pfe))
                        pfe = ParameterFilterElement.Create(doc, name, catIds, ef);
                    else
                        pfe.SetElementFilter(ef);

                    if (!view.GetFilters().Contains(pfe.Id)) view.AddFilter(pfe.Id);
                    var c = Palette[i % Palette.Length];
                    var ogs = new OverrideGraphicSettings()
                        .SetProjectionLineColor(c)
                        .SetProjectionLineWeight(5);
                    if (solid != null)
                        ogs.SetSurfaceForegroundPatternId(solid.Id).SetSurfaceForegroundPatternColor(c);
                    view.SetFilterOverrides(pfe.Id, ogs);
                }
                t.Commit();
            }
            return $"Coloured {zones.Count} zone(s): " + string.Join(", ", zones);
        }

        /// <summary>Legend-style quantity schedule (Family and Type + Count), like the drawing's legend table.</summary>
        public static string CreateLegendSchedule(Document doc, out ViewSchedule schedule)
        {
            schedule = null;
            using (var t = new Transaction(doc, "MorphLab · Sprinkler legend schedule"))
            {
                t.Start();
                var vs = ViewSchedule.CreateSchedule(doc, new ElementId(BuiltInCategory.OST_Sprinklers));
                string baseName = "MLS - Fire Sprinkler Legend";
                string name = baseName; int k = 2;
                var names = new HashSet<string>(new FilteredElementCollector(doc).OfClass(typeof(ViewSchedule)).Select(v => v.Name));
                while (names.Contains(name)) name = baseName + " " + k++;
                vs.Name = name;

                var def = vs.Definition;
                ScheduleFieldId famType = null;
                foreach (var sf in def.GetSchedulableFields())
                {
                    if (sf.FieldType == ScheduleFieldType.Instance &&
                        sf.ParameterId.Equals(new ElementId(BuiltInParameter.ELEM_FAMILY_AND_TYPE_PARAM)))
                        famType = def.AddField(sf).FieldId;
                }
                foreach (var sf in def.GetSchedulableFields())
                    if (sf.FieldType == ScheduleFieldType.Count) { def.AddField(sf); break; }

                if (famType != null) def.AddSortGroupField(new ScheduleSortGroupField(famType));
                def.IsItemized = false;
                t.Commit();
                schedule = vs;
                return "Created schedule '" + name + "'.";
            }
        }

        /// <summary>Upright / pendant / other counts for the quantity card.</summary>
        public static (int up, int pend, int other, string byK) Counts(Document doc, MorphLab.Sprinkler.Core.FamilyLibrary lib)
        {
            int up = 0, pend = 0, other = 0;
            var perK = new SortedDictionary<int, int>();
            foreach (FamilyInstance fi in new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Sprinklers).WhereElementIsNotElementType())
            {
                var o = FamilyService.OrientationOf(fi.Symbol, lib);
                if (o == "Pendant") pend++; else if (o == "Upright") up++; else other++;
                int k = MorphLab.Sprinkler.Core.SprinklerSettings.KValue(FamilyService.KOf(fi.Symbol, lib));
                perK[k] = perK.TryGetValue(k, out var c) ? c + 1 : 1;
            }
            return (up, pend, other, string.Join(" · ", perK.Select(kv => $"K{kv.Key}: {kv.Value:N0}")));
        }
    }
}
