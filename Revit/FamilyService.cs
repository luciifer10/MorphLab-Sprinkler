using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Autodesk.Revit.DB;
using MorphLab.Sprinkler.Core;
using RevitApp = Autodesk.Revit.ApplicationServices.Application;

namespace MorphLab.Sprinkler.Revit
{
    /// <summary>
    /// Everything that touches sprinkler families in Revit:
    ///  • Inspect   — read an .rfa's category, types and K-Factor WITHOUT loading it (PartAtom)
    ///  • Resolve   — pick the family for a K-type + orientation, load it from the library if needed,
    ///                choose the right type and correct its K-Factor parameter
    ///  • Import    — save a family already loaded in the project into the user library
    ///  • Variant   — the senior's workflow: copy a family and change only its K-factor
    /// </summary>
    public static class FamilyService
    {
        // ================================================================== inspect (.rfa on disk)
        public static void Inspect(RevitApp app, LibraryFamily f)
        {
            var types = new List<FamilyTypeInfo>();
            string category = "", version = "";
            string xml = Path.Combine(Path.GetTempPath(), "mls_partatom_" + Guid.NewGuid().ToString("N") + ".xml");
            try
            {
                app.ExtractPartAtomFromFamilyFile(f.FullPath, xml);
                var x = XDocument.Load(xml);
                XNamespace atom = "http://www.w3.org/2005/Atom";
                XNamespace A = "urn:schemas-autodesk-com:partatom";

                category = x.Root.Elements(atom + "category")
                    .Where(c => (string)c.Element(atom + "scheme") == "adsk:revit:grouping")
                    .Select(c => (string)c.Element(atom + "term")).FirstOrDefault() ?? "";
                version = x.Descendants(A + "product-version").Select(e => e.Value).FirstOrDefault() ?? "";

                foreach (var part in x.Descendants(A + "part"))
                {
                    var name = part.Elements().FirstOrDefault(e => e.Name.LocalName == "title")?.Value ?? "";
                    var kEl = part.Elements().FirstOrDefault(e => e.Name.LocalName == "K_Factor")
                           ?? part.Elements().FirstOrDefault(e => e.Name.LocalName == "Sprkl_K_Factor");
                    types.Add(new FamilyTypeInfo { Name = name, KParam = Num(kEl?.Value) });
                }
            }
            catch { /* unreadable PartAtom: fall back to name-only detection */ }
            finally { try { File.Delete(xml); } catch { } }

            FamilyDetect.Apply(f, types, category, version);

            if (int.TryParse(version, out int fv) && int.TryParse(app.VersionNumber, out int rv) && fv > rv)
                f.Warnings = ($"Saved in Revit {fv} — it can't be loaded into Revit {rv}.\n" + f.Warnings).Trim();
        }

        static double Num(string s)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            var m = Regex.Match(s, @"-?\d+(\.\d+)?");
            return m.Success && double.TryParse(m.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
        }

        // ================================================================== project status
        public static void RefreshStatus(Document doc, FamilyLibrary lib)
        {
            var loaded = new HashSet<string>(new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>()
                .Select(f => f.Name), StringComparer.OrdinalIgnoreCase);
            foreach (var i in lib.Items)
                i.Status = loaded.Contains(i.FamilyName) ? "In project ✓"
                         : i.FileMissing ? "File missing"
                         : "Loads automatically";
        }

        // ================================================================== resolve for placement
        /// <summary>Must run inside an open transaction (it may load families and set parameters).</summary>
        public static FamilySymbol Resolve(Document doc, SprinklerSettings s, FamilyLibrary lib, string k, bool pendant, List<string> msgs)
        {
            string what = $"{k} {(pendant ? "pendant" : "upright")}";
            string slot = s.SlotValue(k, pendant);

            // 1. user picked a family that is already in the project
            if (slot.StartsWith("proj:"))
            {
                var sym = Catalog.FindSymbol(doc, slot.Substring(5));
                if (sym != null) return Prepare(sym, lib.FindByFamilyName(sym.FamilyName), s);
                msgs.Add($"{what}: chosen project family '{slot.Substring(5)}' is no longer loaded — using Auto.");
            }

            // 2. a library entry (chosen, or the best automatic match)
            LibraryFamily entry = string.IsNullOrEmpty(slot) || slot.StartsWith("proj:") ? null : lib.Find(slot);
            if (entry == null)
            {
                entry = lib.Best(k, pendant, out bool exact);
                if (entry != null && !exact)
                    msgs.Add($"No {what} in the library — using {entry.Title}. Add one in the Heads tab if needed.");
            }

            // 3. nothing in the library: any project family whose name says this K
            if (entry == null)
            {
                var sym = ProjectBest(doc, lib, k, pendant);
                if (sym != null) { msgs.Add($"{what}: using project family '{Catalog.NameOf(sym)}'."); return Prepare(sym, null, s); }
                msgs.Add($"No {k} sprinkler family in the library or the project — add one in the Heads tab.");
                return null;
            }

            var fam = EnsureLoaded(doc, entry, msgs);
            if (fam == null) return null;
            return Prepare(PickSymbol(doc, fam, entry.TypeName, (int)entry.KFactor), entry, s);
        }

        public static Family EnsureLoaded(Document doc, LibraryFamily entry, List<string> msgs)
        {
            var fam = new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>()
                .FirstOrDefault(f => string.Equals(f.Name, entry.FamilyName, StringComparison.OrdinalIgnoreCase));
            if (fam != null) return fam;
            if (entry.FileMissing) { msgs.Add($"{entry.Title}: file not found ({entry.FullPath})."); return null; }
            try
            {
                if (doc.LoadFamily(entry.FullPath, new LoadOptions(), out fam) && fam != null)
                {
                    msgs.Add($"Loaded {entry.FamilyName} into the project.");
                    return fam;
                }
            }
            catch (Exception ex) { msgs.Add($"{entry.Title}: could not load — {ex.Message}"); return null; }
            // LoadFamily returns false when a family with that name already exists — find it
            return new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>()
                .FirstOrDefault(f => string.Equals(f.Name, entry.FamilyName, StringComparison.OrdinalIgnoreCase));
        }

        static FamilySymbol PickSymbol(Document doc, Family fam, string typeName, int k)
        {
            var syms = fam.GetFamilySymbolIds().Select(id => doc.GetElement(id)).OfType<FamilySymbol>().ToList();
            string want = Simplify(typeName);
            return syms.FirstOrDefault(x => x.Name == typeName)
                ?? syms.FirstOrDefault(x => Simplify(x.Name) == want)
                ?? syms.FirstOrDefault(x => FamilyDetect.ParseK(x.Name) == k)
                ?? syms.FirstOrDefault();
        }

        static string Simplify(string s) => new string((s ?? "").ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());

        static FamilySymbol Prepare(FamilySymbol sym, LibraryFamily entry, SprinklerSettings s)
        {
            if (sym == null) return null;
            if (!sym.IsActive) sym.Activate();
            if (s.SyncKFactor)
            {
                double k = entry?.KFactor ?? 0;
                if (k <= 0) k = FamilyDetect.ParseK(sym.FamilyName);
                if (k > 0) SetK(sym, k);
            }
            return sym;
        }

        static readonly string[] KParamNames = { "K-Factor", "Sprkl_K_Factor", "K Factor", "K_Factor" };

        /// <summary>Writes the K-factor into the type's K-Factor parameters (fixes copied families).</summary>
        public static int SetK(FamilySymbol sym, double k)
        {
            int n = 0;
            foreach (var name in KParamNames)
            {
                var p = sym.LookupParameter(name);
                if (p == null || p.IsReadOnly || p.StorageType != StorageType.Double) continue;
                try { if (Math.Abs(p.AsDouble() - k) > 0.01) { p.Set(k); n++; } } catch { }
            }
            return n;
        }

        static FamilySymbol ProjectBest(Document doc, FamilyLibrary lib, string k, bool pendant)
        {
            var all = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Sprinklers)
                .OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>().ToList();
            var ofK = all.Where(x => KOf(x, lib) == k).ToList();
            return ofK.FirstOrDefault(x => (OrientationOf(x, lib) == "Pendant") == pendant) ?? ofK.FirstOrDefault();
        }

        // ================================================================== K / orientation of placed heads
        /// <summary>K-type of a sprinkler type: library entry → family name → type name → K-Factor parameter.</summary>
        public static string KOf(FamilySymbol sym, FamilyLibrary lib)
        {
            if (sym == null) return "K80";
            var e = lib?.FindByFamilyName(sym.FamilyName);
            if (e != null) return e.KType;
            int k = FamilyDetect.ParseK(sym.FamilyName);
            if (k == 0) k = FamilyDetect.ParseK(sym.Name);
            if (k == 0)
                foreach (var name in KParamNames)
                {
                    var p = sym.LookupParameter(name);
                    if (p != null && p.StorageType == StorageType.Double && p.AsDouble() > 0) { k = (int)Math.Round(p.AsDouble()); break; }
                }
            return k > 0 ? "K" + k : "K80";
        }

        public static string OrientationOf(FamilySymbol sym, FamilyLibrary lib)
        {
            var e = lib?.FindByFamilyName(sym.FamilyName);
            if (e != null) return e.Orientation;
            var o = FamilyDetect.ParseOrientation(sym.FamilyName);
            if (o == "") o = FamilyDetect.ParseOrientation(sym.Name);
            return o == "" ? "Other" : o;
        }

        // ================================================================== load everything
        public static string LoadAll(Document doc, FamilyLibrary lib, SprinklerSettings s)
        {
            var msgs = new List<string>(); int n = 0;
            using (var t = new Transaction(doc, "MorphLab · Load sprinkler library"))
            {
                t.Start();
                foreach (var e in lib.Items.Where(i => i.Enabled && !i.FileMissing))
                {
                    var fam = EnsureLoaded(doc, e, msgs);
                    if (fam == null) continue;
                    Prepare(PickSymbol(doc, fam, e.TypeName, (int)e.KFactor), e, s);
                    n++;
                }
                t.Commit();
            }
            RefreshStatus(doc, lib);
            return $"{n} famil{(n == 1 ? "y" : "ies")} ready in the project." + (msgs.Count > 0 ? " " + string.Join(" ", msgs.Where(m => !m.StartsWith("Loaded"))) : "");
        }

        // ================================================================== import from project
        /// <summary>Saves a family that is loaded in the project into the user library (.rfa).</summary>
        public static LibraryFamily ImportFromProject(Document doc, Family fam, FamilyLibrary lib)
        {
            Directory.CreateDirectory(FamilyLibrary.UserFolder);
            string path = Path.Combine(FamilyLibrary.UserFolder, Safe(fam.Name) + ".rfa");
            var fd = doc.EditFamily(fam);
            try { fd.SaveAs(path, new SaveAsOptions { OverwriteExistingFile = true }); }
            finally { fd.Close(false); }

            var entry = lib.AddFile(path);
            var types = fam.GetFamilySymbolIds().Select(id => doc.GetElement(id)).OfType<FamilySymbol>()
                .Select(sy => new FamilyTypeInfo { Name = sy.Name, KParam = ReadK(sy) }).ToList();
            FamilyDetect.Apply(entry, types, "Sprinklers", "");
            return entry;
        }

        static double ReadK(FamilySymbol sy)
        {
            foreach (var n in KParamNames)
            {
                var p = sy.LookupParameter(n);
                if (p != null && p.StorageType == StorageType.Double) return p.AsDouble();
            }
            return 0;
        }

        // ================================================================== K-factor variant
        /// <summary>
        /// "Same family, only the K-factor changes": copies a library family, sets K-Factor in every type,
        /// renames K in type names, saves it as a new .rfa in the user library (e.g. K115_UPRIGHT_SPRINKLER.rfa).
        /// </summary>
        public static LibraryFamily CreateVariant(Document doc, LibraryFamily baseEntry, int newK, FamilyLibrary lib, SprinklerSettings s, out string message)
        {
            var msgs = new List<string>();
            Family fam;
            using (var t = new Transaction(doc, "MorphLab · Load base family"))
            {
                t.Start();
                fam = EnsureLoaded(doc, baseEntry, msgs);
                t.Commit();
            }
            if (fam == null) { message = string.Join(" ", msgs); return null; }

            string newName = Regex.IsMatch(baseEntry.FamilyName, @"K\s*[-_=]?\s*\d{2,3}", RegexOptions.IgnoreCase)
                ? Regex.Replace(baseEntry.FamilyName, @"(K\s*[-_=]?\s*)\d{2,3}", "${1}" + newK, RegexOptions.IgnoreCase)
                : $"K{newK}_{baseEntry.Orientation.ToUpperInvariant()}_SPRINKLER";
            newName = Regex.Replace(newName, @"_0+\d$", "");   // drop Revit backup suffix like _0001
            Directory.CreateDirectory(FamilyLibrary.UserFolder);
            string path = Path.Combine(FamilyLibrary.UserFolder, Safe(newName) + ".rfa");

            var fd = doc.EditFamily(fam);
            var typeInfos = new List<FamilyTypeInfo>();
            try
            {
                using (var t = new Transaction(fd, "Set K-factor"))
                {
                    t.Start();
                    var fm = fd.FamilyManager;
                    var kParams = fm.Parameters.Cast<FamilyParameter>()
                        .Where(p => KParamNames.Contains(p.Definition.Name) && p.StorageType == StorageType.Double).ToList();
                    var used = new HashSet<string>();
                    foreach (var ft in fm.Types.Cast<FamilyType>().ToList())
                    {
                        fm.CurrentType = ft;
                        foreach (var p in kParams) { try { fm.Set(p, (double)newK); } catch { } }
                        string tn = Regex.Replace(ft.Name, @"(K\s*[-_=]?\s*)\d{2,3}", "${1}" + newK, RegexOptions.IgnoreCase);
                        while (used.Contains(tn) || (tn != ft.Name && fm.Types.Cast<FamilyType>().Any(o => o.Name == tn && o != ft))) tn += " (2)";
                        if (tn != ft.Name) { try { fm.RenameCurrentType(tn); } catch { tn = ft.Name; } }
                        used.Add(tn);
                        typeInfos.Add(new FamilyTypeInfo { Name = tn, KParam = newK });
                    }
                    t.Commit();
                }
                fd.SaveAs(path, new SaveAsOptions { OverwriteExistingFile = true });
            }
            finally { fd.Close(false); }

            var entry = lib.AddFile(path);
            FamilyDetect.Apply(entry, typeInfos, "Sprinklers", "");
            entry.Orientation = baseEntry.Orientation;
            s.EnsureRule(entry.KType);
            message = $"Created {entry.FamilyName} (K{newK} {entry.Orientation}) from {baseEntry.FamilyName}.";
            return entry;
        }

        static string Safe(string name)
        {
            foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name.Trim();
        }

        class LoadOptions : IFamilyLoadOptions
        {
            public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues)
            {
                overwriteParameterValues = false;
                return true;
            }

            public bool OnSharedFamilyFound(Family sharedFamily, bool familyInUse, out FamilySource source, out bool overwriteParameterValues)
            {
                source = FamilySource.Family;
                overwriteParameterValues = false;
                return true;
            }
        }
    }
}
