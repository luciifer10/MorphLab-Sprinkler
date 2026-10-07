using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Xml.Serialization;

namespace MorphLab.Sprinkler.Core
{
    public class FamilyTypeInfo
    {
        public string Name { get; set; }
        /// <summary>Value of the family's K-Factor parameter for this type (0 = not found).</summary>
        public double KParam { get; set; }
    }

    /// <summary>One .rfa in the sprinkler library (bundled with the plugin or added by the user).</summary>
    public class LibraryFamily : INotifyPropertyChanged
    {
        public string Id { get; set; }
        public string FileName { get; set; }
        public string FamilyName { get; set; }      // = file name without .rfa (that's what Revit calls it)
        public bool BuiltIn { get; set; }
        public string KType { get; set; } = "K80";
        public double KFactor { get; set; } = 80;
        public string Orientation { get; set; } = "Upright";   // Upright | Pendant | Sidewall
        public string TypeName { get; set; } = "";
        public List<string> Types { get; set; } = new List<string>();
        public string RevitVersion { get; set; } = "";
        public string Warnings { get; set; } = "";

        bool _enabled = true;
        public bool Enabled { get => _enabled; set { _enabled = value; Raise(); } }

        // ---- runtime only
        [XmlIgnore] public string FullPath { get; set; }
        string _status = "";
        [XmlIgnore] public string Status { get => _status; set { _status = value; Raise(); } }
        [XmlIgnore] public bool FileMissing => string.IsNullOrEmpty(FullPath) || !File.Exists(FullPath);
        [XmlIgnore] public string Title => $"{KType} {Orientation}";
        [XmlIgnore] public string Source => BuiltIn ? "MorphLab" : "Your library";
        [XmlIgnore] public bool HasWarnings => !string.IsNullOrWhiteSpace(Warnings);
        [XmlIgnore] public bool IsPendant => Orientation == "Pendant";

        public void RefreshUi()
        {
            Raise(nameof(Title)); Raise(nameof(KType)); Raise(nameof(Orientation)); Raise(nameof(TypeName));
            Raise(nameof(Warnings)); Raise(nameof(HasWarnings)); Raise(nameof(Status));
        }

        public event PropertyChangedEventHandler PropertyChanged;
        void Raise([CallerMemberName] string p = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
    }

    /// <summary>
    /// The sprinkler family library:
    ///   • built-in  — the MorphLab families shipped in the add-in's "Families" folder
    ///   • user      — .rfa files the user adds, copied to %AppData%\MorphLab\Sprinkler\Families
    /// The index (K-type, orientation, which type to use, enabled) lives in library.xml.
    /// </summary>
    public class FamilyLibrary
    {
        public List<LibraryFamily> Items { get; set; } = new List<LibraryFamily>();

        static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MorphLab", "Sprinkler");
        public static string UserFolder => Path.Combine(Root, "Families");
        static string IndexPath => Path.Combine(Root, "library.xml");
        public static string BuiltInFolder => Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? "", "Families");

        // ------------------------------------------------------------------ built-ins
        /// <summary>
        /// The four MorphLab families. Their type names and K-Factor parameters were copied from a K80
        /// family and not all updated, so K comes from the FILE name and the parameter is corrected on load.
        /// </summary>
        static List<LibraryFamily> BuiltIns() => new List<LibraryFamily>
        {
            new LibraryFamily
            {
                Id = "ml-k80-upright", FileName = "K-80_Upright_Sprinkler.rfa", FamilyName = "K-80_Upright_Sprinkler",
                BuiltIn = true, KType = "K80", KFactor = 80, Orientation = "Upright",
                TypeName = "K80 Upright @ 68°C", Types = new List<string> { "K80 Upright @ 68°C" }, RevitVersion = "2023"
            },
            new LibraryFamily
            {
                Id = "ml-k80-pendant", FileName = "K80_PENDENT_SPRINKLER.rfa", FamilyName = "K80_PENDENT_SPRINKLER",
                BuiltIn = true, KType = "K80", KFactor = 80, Orientation = "Pendant",
                TypeName = "K-80 Pendent @ 68°", Types = new List<string> { "K-80 Pendent @ 68°" }, RevitVersion = "2023"
            },
            new LibraryFamily
            {
                Id = "ml-k160-upright", FileName = "K160_UPRIGHT_SPRINKLER_0001.rfa", FamilyName = "K160_UPRIGHT_SPRINKLER_0001",
                BuiltIn = true, KType = "K160", KFactor = 160, Orientation = "Upright",
                TypeName = "K-80 Upright @ 68°C", Types = new List<string> { "K-80 Upright @ 68°C" }, RevitVersion = "2023",
                Warnings = "Type name and K-Factor parameter say K80 — K160 is taken from the family name and the parameter is corrected on load."
            },
            new LibraryFamily
            {
                Id = "ml-k240-upright", FileName = "K240_UPRIGHT_SPRINKLER.rfa", FamilyName = "K240_UPRIGHT_SPRINKLER",
                BuiltIn = true, KType = "K240", KFactor = 240, Orientation = "Upright",
                TypeName = "K-240 Upright @ 74°C", Types = new List<string> { "K-80 Upright @ 68°C", "K-240 Upright @ 74°C" }, RevitVersion = "2023",
                Warnings = "K-Factor parameter is 80 in both types — corrected to 240 on load. The extra 'K-80 Upright' type is ignored."
            }
        };

        // ------------------------------------------------------------------ load / save
        public static FamilyLibrary Load()
        {
            FamilyLibrary lib = null;
            try
            {
                if (File.Exists(IndexPath))
                    using (var fs = File.OpenRead(IndexPath))
                        lib = (FamilyLibrary)new XmlSerializer(typeof(FamilyLibrary)).Deserialize(fs);
            }
            catch { }
            lib = lib ?? new FamilyLibrary();

            // built-ins are always regenerated (new plugin versions may update them); only Enabled is remembered
            var saved = lib.Items.Where(i => i.BuiltIn).ToDictionary(i => i.Id, i => i.Enabled);
            lib.Items.RemoveAll(i => i.BuiltIn);
            var bi = BuiltIns();
            foreach (var b in bi)
            {
                if (saved.TryGetValue(b.Id, out var en)) b.Enabled = en;
                b.FullPath = Path.Combine(BuiltInFolder, b.FileName);
            }
            lib.Items.InsertRange(0, bi);

            foreach (var u in lib.Items.Where(i => !i.BuiltIn))
                u.FullPath = Path.Combine(UserFolder, u.FileName);

            foreach (var i in lib.Items)
                i.Status = i.FileMissing ? "File missing" : "Loads automatically";
            return lib;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Root);
                using (var fs = File.Create(IndexPath))
                    new XmlSerializer(typeof(FamilyLibrary)).Serialize(fs, this);
            }
            catch { }
        }

        // ------------------------------------------------------------------ queries
        public LibraryFamily Find(string id) => Items.FirstOrDefault(i => i.Id == id);

        public LibraryFamily FindByFamilyName(string familyName) =>
            Items.FirstOrDefault(i => string.Equals(i.FamilyName, familyName, StringComparison.OrdinalIgnoreCase));

        /// <summary>Best enabled family for a K-type + orientation. exact=false when only the other orientation exists.</summary>
        public LibraryFamily Best(string k, bool pendant, out bool exact)
        {
            var cands = Items.Where(i => i.Enabled && !i.FileMissing && string.Equals(i.KType, k, StringComparison.OrdinalIgnoreCase)
                                         && i.Orientation != "Sidewall").ToList();
            var hit = cands.FirstOrDefault(i => i.IsPendant == pendant);
            exact = hit != null;
            return hit ?? cands.FirstOrDefault();
        }

        public bool Has(string k, bool pendant) { Best(k, pendant, out var exact); return exact; }
        public bool HasAny(string k) => Best(k, false, out _) != null;

        /// <summary>Copies an .rfa into the user library folder and returns the new entry (not yet detected).</summary>
        public LibraryFamily AddFile(string sourcePath)
        {
            Directory.CreateDirectory(UserFolder);
            string file = Path.GetFileName(sourcePath);
            string dest = Path.Combine(UserFolder, file);
            if (!string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(dest), StringComparison.OrdinalIgnoreCase))
                File.Copy(sourcePath, dest, true);

            var existing = Items.FirstOrDefault(i => !i.BuiltIn && string.Equals(i.FileName, file, StringComparison.OrdinalIgnoreCase));
            if (existing != null) Items.Remove(existing);

            var f = new LibraryFamily
            {
                Id = "user-" + Guid.NewGuid().ToString("N").Substring(0, 10),
                FileName = file,
                FamilyName = Path.GetFileNameWithoutExtension(file),
                FullPath = dest
            };
            Items.Add(f);
            return f;
        }

        public void Remove(LibraryFamily f)
        {
            if (f == null || f.BuiltIn) return;
            Items.Remove(f);
            try { if (File.Exists(f.FullPath)) File.Delete(f.FullPath); } catch { }
        }
    }

    /// <summary>
    /// Works out K-factor, orientation and the type to use from names and parameters. Pure logic.
    /// Priority for K: family (file) name → type name → K-Factor parameter, because copied families
    /// often keep an old K-Factor value.
    /// </summary>
    public static class FamilyDetect
    {
        static readonly Regex KRx = new Regex(@"(?<![A-Z0-9])K\s*[-_=]?\s*(\d{2,3})(?!\d)", RegexOptions.IgnoreCase);

        public static int ParseK(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            foreach (Match m in KRx.Matches(text))
                if (int.TryParse(m.Groups[1].Value, out int v) && v >= 40 && v <= 600) return v;
            return 0;
        }

        public static string ParseOrientation(string text)
        {
            var u = (text ?? "").ToUpperInvariant().Replace("_", " ").Replace("-", " ");
            if (u.Contains("PENDENT") || u.Contains("PENDANT") || u.Contains("PEND ")) return "Pendant";
            if (u.Contains("SIDEWALL") || u.Contains("SIDE WALL")) return "Sidewall";
            if (u.Contains("UPRIGHT") || u.Contains("UP RIGHT")) return "Upright";
            if (u.Contains("CONCEALED") || u.Contains("RECESSED") || u.Contains("FLUSH")) return "Pendant";
            return "";
        }

        /// <summary>Fills K, orientation, chosen type and warnings on a library entry.</summary>
        public static void Apply(LibraryFamily f, IList<FamilyTypeInfo> types, string category, string revitVersion)
        {
            var warn = new List<string>();
            f.Types = types.Select(t => t.Name).ToList();
            f.RevitVersion = revitVersion ?? "";

            if (!string.IsNullOrEmpty(category) && !string.Equals(category, "Sprinklers", StringComparison.OrdinalIgnoreCase))
                warn.Add($"Category is '{category}', not Sprinklers — the plugin can't place it.");

            int kName = ParseK(f.FamilyName);
            int kType = types.Select(t => ParseK(t.Name)).FirstOrDefault(v => v > 0);
            int kParam = (int)Math.Round(types.Select(t => t.KParam).FirstOrDefault(v => v > 0));
            int k = kName > 0 ? kName : (kType > 0 ? kType : kParam);
            if (k == 0) { k = 80; warn.Add("No K-factor in the name or parameters — assumed K80. Set it below."); }

            f.KFactor = k;
            f.KType = "K" + k;

            // type: the one whose own name carries this K, else whose K-Factor matches, else the first
            var pick = types.FirstOrDefault(t => ParseK(t.Name) == k)
                    ?? types.FirstOrDefault(t => (int)Math.Round(t.KParam) == k)
                    ?? types.FirstOrDefault();
            f.TypeName = pick?.Name ?? "";

            if (kName > 0 && kType > 0 && kType != kName && pick != null && ParseK(pick.Name) != k)
                warn.Add($"Type name says K{kType} but the family is K{kName} — using K{kName} from the family name.");
            if (kParam > 0 && kParam != k)
                warn.Add($"K-Factor parameter is {kParam} — corrected to {k} when loaded (Sync K-Factor).");
            if (types.Count > 1)
                warn.Add($"{types.Count} types inside — using '{f.TypeName}'. You can change it below.");

            string o = ParseOrientation(f.FamilyName);
            if (o == "") o = ParseOrientation(f.TypeName);
            if (o == "") { o = "Upright"; warn.Add("Orientation not in the name — assumed Upright. Set it below."); }
            f.Orientation = o;
            if (o == "Sidewall") warn.Add("Sidewall heads are kept in the library but not used by automatic layout.");

            f.Warnings = string.Join("\n", warn);
        }
    }
}
