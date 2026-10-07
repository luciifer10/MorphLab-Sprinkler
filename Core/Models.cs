using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Autodesk.Revit.DB;

namespace MorphLab.Sprinkler.Core
{
    /// <summary>A room (host or linked) prepared for sprinkler design. Bound to the Rooms grid.</summary>
    public class RoomInfo : INotifyPropertyChanged
    {
        public string Key { get; set; }                 // H:uid  or  L:linkUid:roomUid
        public string Name { get; set; }
        public string Number { get; set; }
        public string LevelName { get; set; }
        public ElementId HostLevelId { get; set; }
        public double HostLevelZ { get; set; }          // ft, internal origin
        public double FloorZ { get; set; }              // ft, internal origin (host coords)
        public bool IsLinked { get; set; }
        public string Source => IsLinked ? "Link" : "Host";

        public List<P2> Outline { get; set; } = new List<P2>();
        public List<List<P2>> Holes { get; set; } = new List<List<P2>>();
        public double AreaM2 { get; set; }

        public bool CeilingFound { get; set; }
        public string HeightSource { get; set; } = "Room";

        double _heightMm;
        public double HeightMm { get => _heightMm; set { _heightMm = value; Raise(); Raise(nameof(AutoK)); } }

        public string ExcludedReason { get; set; } = "";
        public bool IsExcluded => !string.IsNullOrEmpty(ExcludedReason);

        bool _include;
        public bool Include { get => _include; set { _include = value; Raise(); } }

        string _kOverride = "Auto";
        public string KOverride { get => _kOverride; set { _kOverride = value; Raise(); } }

        string _headOverride = "Auto";
        public string HeadOverride { get => _headOverride; set { _headOverride = value; Raise(); } }

        string _status = "";
        public string Status { get => _status; set { _status = value; Raise(); } }

        /// <summary>Filled in by the scanner from the settings threshold, shown in the grid.</summary>
        public string AutoK { get; set; } = "K80";

        public string Display => string.IsNullOrWhiteSpace(Number) ? Name : Number + " · " + Name;

        public event PropertyChangedEventHandler PropertyChanged;
        void Raise([CallerMemberName] string p = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
    }

    /// <summary>One branch line: heads on either side of the feed point, nearest-to-feed first.</summary>
    public class BranchRowPlan
    {
        public double Cross;                       // local ft, coordinate along the main
        public List<double> Left = new List<double>();
        public List<double> Right = new List<double>();
        public int Count => Left.Count + Right.Count;
    }

    public class NetPlan
    {
        public bool BranchAlongX;
        public double Feed;                        // local ft, where the main crosses the branches
        public double InletCross;                  // local ft, start of inlet stub
        public List<BranchRowPlan> Rows = new List<BranchRowPlan>(); // downstream order

        /// <summary>Converts (along-branch, along-main) to the plan's local XY.</summary>
        public P2 Local(double along, double cross) => BranchAlongX ? new P2(along, cross) : new P2(cross, along);
        public int TotalHeads { get { int n = 0; foreach (var r in Rows) n += r.Count; return n; } }
    }

    /// <summary>Result of planning one room.</summary>
    public class RoomPlan
    {
        public RoomInfo Room;
        public string KType;
        public bool Pendant;
        public Frame Frame;
        public double MinX, MaxX, MinY, MaxY;      // local ft
        public int Nx, Ny;
        public double SxMm, SyMm, ExMm, EyMm;
        public double CoverageM2;
        public List<P2> HeadsLocal = new List<P2>();
        public List<P2> HeadsWorld = new List<P2>();
        public int Dropped;
        public double HeadZ, PipeZ;                // ft, absolute (internal origin)
        public NetPlan Net;
        public List<string> Warnings = new List<string>();
        public bool HasErrors;

        public string Summary =>
            $"{HeadsWorld.Count} heads · {KType} {(Pendant ? "pendant" : "upright")} · " +
            $"{SxMm:0} × {SyMm:0} mm · wall {ExMm:0}/{EyMm:0} mm · {CoverageM2:0.0} m²/head";
    }
}
