using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Serialization;

namespace MorphLab.Sprinkler.Core
{
    /// <summary>One row of a pipe-schedule table: "up to MaxHeads sprinklers -> SizeMm".</summary>
    public class SizeRow
    {
        public int MaxHeads { get; set; }
        public int SizeMm { get; set; }
        /// <summary>True for rows that are NOT in the client notes and were added so mains can be sized.</summary>
        public bool Assumed { get; set; }

        public SizeRow() { }
        public SizeRow(int maxHeads, int sizeMm, bool assumed = false)
        {
            MaxHeads = maxHeads; SizeMm = sizeMm; Assumed = assumed;
        }
    }

    /// <summary>Spacing + sizing rules for one K-factor.</summary>
    public class KRule
    {
        public string Name { get; set; } = "K80";
        public double HeadToHeadMinMm { get; set; }
        public double HeadToHeadMaxMm { get; set; }
        public double WallMinMm { get; set; }
        public double WallMaxMm { get; set; }
        /// <summary>Max floor area protected by one head (m²). 0 = not checked.</summary>
        public double MaxCoverageM2 { get; set; }
        public string Note { get; set; } = "";
        public List<SizeRow> Sizing { get; set; } = new List<SizeRow>();

        public int SizeFor(int heads)
        {
            if (heads <= 0) return Sizing.Count > 0 ? Sizing[0].SizeMm : 25;
            foreach (var r in Sizing.OrderBy(r => r.MaxHeads))
                if (heads <= r.MaxHeads) return r.SizeMm;
            return Sizing.Count > 0 ? Sizing.Max(r => r.SizeMm) : 150;
        }
    }

    /// <summary>Which family to use for one K-type + orientation. Value = library id, "proj:Family : Type", or "" for Auto.</summary>
    public class SlotPick
    {
        public string Key { get; set; }
        public string Value { get; set; }
        public static string KeyOf(string k, bool pendant) => (k ?? "").ToUpperInvariant() + "|" + (pendant ? "Pendant" : "Upright");
    }

    public enum HeadOrientation { Auto, Upright, Pendant }
    public enum BranchDirection { Auto, AlongLongSide, AlongShortSide }
    public enum FeedPosition { Centre, End }
    public enum InletSide { Start, End }

    /// <summary>
    /// Everything the designer can tune. Saved to %AppData%\MorphLab\Sprinkler\settings.xml.
    /// Defaults come straight from "Sprinkler System – Overall Notes".
    /// </summary>
    public class SprinklerSettings
    {
        public List<KRule> Rules { get; set; } = new List<KRule>();

        /// <summary>Room-name keywords where NO sprinkler is placed (whole-word match, case-insensitive).</summary>
        public List<string> ExcludedRoomKeywords { get; set; } = new List<string>();

        /// <summary>At or above this ceiling height the room is treated as double-height -> K160.</summary>
        public double DoubleHeightThresholdMm { get; set; } = 8000;
        public string DoubleHeightKType { get; set; } = "K160";
        public string NormalKType { get; set; } = "K80";

        // ---- elevations ----
        public double UprightBelowDeckMm { get; set; } = 150;   // head insertion below roof / deck
        public double PendantBelowCeilingMm { get; set; } = 0;  // head insertion below false ceiling
        public double UprightRiserNippleMm { get; set; } = 300; // branch centreline below upright head
        public double PendantDropMm { get; set; } = 300;        // branch centreline above pendant head
        public bool ReadCeilings { get; set; } = true;

        // ---- layout ----
        public double SpacingRoundingMm { get; set; } = 10;
        public HeadOrientation Orientation { get; set; } = HeadOrientation.Auto;
        public BranchDirection Branches { get; set; } = BranchDirection.AlongShortSide;
        public FeedPosition Feed { get; set; } = FeedPosition.Centre;
        public InletSide Inlet { get; set; } = InletSide.Start;
        public double InletStubMm { get; set; } = 300;
        public int DropSizeMm { get; set; } = 25;

        // ---- generation ----
        public bool CreatePiping { get; set; } = true;
        public bool ReplacePrevious { get; set; } = true;

        // ---- families ----
        /// <summary>Per K-type + orientation choice. Empty / missing = "Auto" (best match from the library).</summary>
        public List<SlotPick> Slots { get; set; } = new List<SlotPick>();
        /// <summary>When a family is loaded, write the K-factor from its name into its K-Factor parameter.</summary>
        public bool SyncKFactor { get; set; } = true;
        public string PipeTypeName { get; set; } = "";
        public string SystemTypeName { get; set; } = "";

        public string SlotValue(string k, bool pendant)
        {
            var key = SlotPick.KeyOf(k, pendant);
            return Slots.FirstOrDefault(x => x.Key == key)?.Value ?? "";
        }

        public void SetSlot(string k, bool pendant, string value)
        {
            var key = SlotPick.KeyOf(k, pendant);
            Slots.RemoveAll(x => x.Key == key);
            if (!string.IsNullOrEmpty(value)) Slots.Add(new SlotPick { Key = key, Value = value });
        }

        // ---- QC / sizing ----
        public string SizingTableMode { get; set; } = "Auto"; // Auto | K80 | K160 | K320

        public KRule Rule(string k)
        {
            var r = Rules.FirstOrDefault(x => string.Equals(x.Name, k, StringComparison.OrdinalIgnoreCase));
            if (r != null) return r;
            // unknown K -> nearest rule by K value (e.g. K115 -> K80)
            int kv = KValue(k);
            return Rules.OrderBy(x => Math.Abs(KValue(x.Name) - kv)).FirstOrDefault() ?? CreateDefault().Rules[0];
        }

        public bool HasRule(string k) => Rules.Any(x => string.Equals(x.Name, k, StringComparison.OrdinalIgnoreCase));

        /// <summary>K names sorted by value: K80, K160, K240, K320 …</summary>
        public List<string> KNames => Rules.Select(r => r.Name).OrderBy(KValue).ToList();

        public static int KValue(string k)
        {
            if (string.IsNullOrEmpty(k)) return 0;
            var digits = new string(k.Where(char.IsDigit).ToArray());
            return int.TryParse(digits, out var v) ? v : 0;
        }

        /// <summary>
        /// A family with a K-factor that has no rule yet (e.g. a K115 variant) gets a rule copied from the
        /// nearest K, marked as assumed so the designer confirms it.
        /// </summary>
        public KRule EnsureRule(string k)
        {
            if (HasRule(k)) return Rule(k);
            var src = Rule(k);
            var r = new KRule
            {
                Name = k.ToUpperInvariant(),
                HeadToHeadMinMm = src.HeadToHeadMinMm, HeadToHeadMaxMm = src.HeadToHeadMaxMm,
                WallMinMm = src.WallMinMm, WallMaxMm = src.WallMaxMm, MaxCoverageM2 = src.MaxCoverageM2,
                Note = $"Auto-created from {src.Name} when a {k} family was added — confirm spacing and pipe table.",
                Sizing = src.Sizing.Select(x => new SizeRow(x.MaxHeads, x.SizeMm, true)).ToList()
            };
            Rules.Add(r);
            return r;
        }

        /// <summary>Settings saved by an older version miss newer default rules (e.g. K240) — add them.</summary>
        void EnsureDefaults()
        {
            var d = CreateDefault();
            foreach (var r in d.Rules) if (!HasRule(r.Name)) Rules.Add(r);
            if (ExcludedRoomKeywords == null || ExcludedRoomKeywords.Count == 0) ExcludedRoomKeywords = d.ExcludedRoomKeywords;
            if (Slots == null) Slots = new List<SlotPick>();
        }

        // ------------------------------------------------------------------ defaults
        public static SprinklerSettings CreateDefault()
        {
            var s = new SprinklerSettings();

            // Notes §1 + §2
            s.Rules.Add(new KRule
            {
                Name = "K80",
                HeadToHeadMinMm = 2500, HeadToHeadMaxMm = 4600,
                WallMinMm = 500, WallMaxMm = 1500,
                MaxCoverageM2 = 12.0,
                Note = "Normal ceiling / area conditions. Coverage cap 12 m² is an assumption (set 0 to disable).",
                Sizing = new List<SizeRow>
                {
                    new SizeRow(2, 25), new SizeRow(3, 32), new SizeRow(5, 40),
                    new SizeRow(10, 50), new SizeRow(30, 65), new SizeRow(60, 80),
                    new SizeRow(100, 100, true), new SizeRow(99999, 150, true)
                }
            });

            // Notes §3 + §4
            s.Rules.Add(new KRule
            {
                Name = "K160",
                HeadToHeadMinMm = 1800, HeadToHeadMaxMm = 4600,
                WallMinMm = 500, WallMaxMm = 2000,
                MaxCoverageM2 = 9.0,
                Note = "Warehouses, raw-material, double-height. Coverage cap 9 m² is an assumption (set 0 to disable).",
                Sizing = new List<SizeRow>
                {
                    new SizeRow(1, 25), new SizeRow(2, 40), new SizeRow(4, 50),
                    new SizeRow(8, 65), new SizeRow(99999, 100)
                }
            });

            // The MorphLab library has a K240 upright. Notes give no K240 rules -> K160/K320 group spacing.
            s.Rules.Add(new KRule
            {
                Name = "K240",
                HeadToHeadMinMm = 1800, HeadToHeadMaxMm = 4600,
                WallMinMm = 500, WallMaxMm = 2000,
                MaxCoverageM2 = 9.0,
                Note = "Not in the notes — spacing taken from the K160/K320 group, pipe table copied from K160. Confirm with designer.",
                Sizing = new List<SizeRow>
                {
                    new SizeRow(1, 25, true), new SizeRow(2, 40, true), new SizeRow(4, 50, true),
                    new SizeRow(8, 65, true), new SizeRow(99999, 100, true)
                }
            });

            // Notes group K160/K320 spacing together; no K320 pipe table was given.
            s.Rules.Add(new KRule
            {
                Name = "K320",
                HeadToHeadMinMm = 1800, HeadToHeadMaxMm = 4600,
                WallMinMm = 500, WallMaxMm = 2000,
                MaxCoverageM2 = 9.0,
                Note = "Spacing per notes (same as K160). Pipe table NOT given in notes — K160 table copied, confirm with designer.",
                Sizing = new List<SizeRow>
                {
                    new SizeRow(1, 25, true), new SizeRow(2, 40, true), new SizeRow(4, 50, true),
                    new SizeRow(8, 65, true), new SizeRow(99999, 100, true)
                }
            });

            // Notes §6
            s.ExcludedRoomKeywords = new List<string>
            {
                "SUBSTATION", "SUB STATION", "ELECTRICAL PANEL", "PANEL ROOM", "TRANSFORMER",
                "UPS", "SERVER", "TMA", "BCL3", "BATTERY", "SCADA", "IT ROOM", "LV"
            };
            return s;
        }

        // ------------------------------------------------------------------ persistence
        static string Folder => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MorphLab", "Sprinkler");
        static string FilePath => Path.Combine(Folder, "settings.xml");

        public static SprinklerSettings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    using (var fs = File.OpenRead(FilePath))
                    {
                        var s = (SprinklerSettings)new XmlSerializer(typeof(SprinklerSettings)).Deserialize(fs);
                        if (s != null && s.Rules != null && s.Rules.Count > 0) { s.EnsureDefaults(); return s; }
                    }
                }
            }
            catch { /* corrupt file -> defaults */ }
            return CreateDefault();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Folder);
                using (var fs = File.Create(FilePath))
                    new XmlSerializer(typeof(SprinklerSettings)).Serialize(fs, this);
            }
            catch { /* non-fatal */ }
        }
    }
}
