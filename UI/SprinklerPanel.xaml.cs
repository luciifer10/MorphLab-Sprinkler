using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Shapes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using MorphLab.Sprinkler.Core;
using MorphLab.Sprinkler.Revit;
using WColor = System.Windows.Media.Color;
// Revit's DB/UI namespaces also define Line, Ellipse, Point, ComboBox, TextBox — pin the WPF ones.
using Line = System.Windows.Shapes.Line;
using Ellipse = System.Windows.Shapes.Ellipse;
using Polygon = System.Windows.Shapes.Polygon;
using Point = System.Windows.Point;
using ComboBox = System.Windows.Controls.ComboBox;
using TextBox = System.Windows.Controls.TextBox;
using Brush = System.Windows.Media.Brush;
using Visibility = System.Windows.Visibility;
using MessageBox = System.Windows.MessageBox;
using TextBlock = System.Windows.Controls.TextBlock;

namespace MorphLab.Sprinkler.UI
{
    public partial class SprinklerPanel : UserControl
    {
        SprinklerSettings _s = SprinklerSettings.Load();
        readonly ObservableCollection<RoomInfo> _rooms = new ObservableCollection<RoomInfo>();
        readonly ICollectionView _roomView;
        List<RoomPlan> _plans = new List<RoomPlan>();
        Catalog _catalog;
        QcReport _qc;
        readonly FamilyLibrary _lib = FamilyLibrary.Load();
        readonly ObservableCollection<LibraryFamily> _libItems = new ObservableCollection<LibraryFamily>();
        readonly ObservableCollection<SlotRow> _slots = new ObservableCollection<SlotRow>();

        static readonly string[] HeadOptions = { "Auto", "Upright", "Pendant" };
        static readonly int[] Sizes = { 25, 32, 40, 50, 65, 80, 100, 125, 150, 200 };

        public SprinklerPanel()
        {
            InitializeComponent();

            ColHead.ItemsSource = HeadOptions;
            CmbManualSize.ItemsSource = Sizes;
            CmbManualSize.SelectedIndex = 0;
            CmbEditOrient.ItemsSource = new[] { "Upright", "Pendant", "Sidewall" };
            ListLib.ItemsSource = _libItems;
            ListSlots.ItemsSource = _slots;
            ReloadLibraryList();

            _roomView = CollectionViewSource.GetDefaultView(_rooms);
            _roomView.Filter = FilterRoom;
            GridRooms.ItemsSource = _roomView;
            ListPlaceRooms.ItemsSource = _rooms;

            LoadSettingsToUi();
            RevitTask.OnError = ex => SetStatus("Error: " + ex.Message, Kind.Error);
        }

        // ================================================================== status
        enum Kind { Ok, Busy, Warn, Error }

        void SetStatus(string text, Kind k = Kind.Ok)
        {
            TxtStatus.Text = text;
            StatusDot.Fill = new SolidColorBrush(
                k == Kind.Ok ? WColor.FromRgb(0x15, 0x80, 0x3D) :
                k == Kind.Busy ? WColor.FromRgb(0x1C, 0x6E, 0xB5) :
                k == Kind.Warn ? WColor.FromRgb(0xA1, 0x5C, 0x07) : WColor.FromRgb(0xD6, 0x28, 0x28));
        }

        static Document Doc(UIApplication app) => app.ActiveUIDocument?.Document;

        // ================================================================== settings <-> UI
        void LoadSettingsToUi()
        {
            CmbBranch.SelectedIndex = _s.Branches == BranchDirection.AlongLongSide ? 1 : 0;
            CmbFeed.SelectedIndex = _s.Feed == FeedPosition.End ? 1 : 0;
            CmbInlet.SelectedIndex = _s.Inlet == InletSide.End ? 1 : 0;
            ChkPiping.IsChecked = _s.CreatePiping;
            ChkReplace.IsChecked = _s.ReplacePrevious;
            RefreshKOptions();
            ChkSyncK.IsChecked = _s.SyncKFactor;

            ListRules.ItemsSource = null;
            ListRules.ItemsSource = _s.Rules;
            TxtThreshold.Text = _s.DoubleHeightThresholdMm.ToString("0");
            TxtUpBelow.Text = _s.UprightBelowDeckMm.ToString("0");
            TxtRiser.Text = _s.UprightRiserNippleMm.ToString("0");
            TxtPendBelow.Text = _s.PendantBelowCeilingMm.ToString("0");
            TxtDrop.Text = _s.PendantDropMm.ToString("0");
            TxtRound.Text = _s.SpacingRoundingMm.ToString("0");
            ChkCeilings.IsChecked = _s.ReadCeilings;
            TxtKeywords.Text = string.Join(Environment.NewLine, _s.ExcludedRoomKeywords);
            RefreshTable();
            BuildSlots();
        }

        /// <summary>K lists everywhere follow the rules (K80, K160, K240, K320 + any added K).</summary>
        void RefreshKOptions()
        {
            var ks = _s.KNames;
            ColK.ItemsSource = new[] { "Auto" }.Concat(ks).ToList();
            CmbHighK.ItemsSource = ks.Where(k => k != _s.NormalKType).ToList();
            CmbHighK.SelectedItem = _s.DoubleHeightKType;
            var table = new[] { "Auto" }.Concat(ks).ToList();
            CmbTable.ItemsSource = table;
            CmbTable.SelectedItem = table.Contains(_s.SizingTableMode) ? _s.SizingTableMode : "Auto";
        }

        void ReadUiToSettings()
        {
            _s.Branches = CmbBranch.SelectedIndex == 1 ? BranchDirection.AlongLongSide : BranchDirection.AlongShortSide;
            _s.Feed = CmbFeed.SelectedIndex == 1 ? FeedPosition.End : FeedPosition.Centre;
            _s.Inlet = CmbInlet.SelectedIndex == 1 ? InletSide.End : InletSide.Start;
            _s.CreatePiping = ChkPiping.IsChecked == true;
            _s.ReplacePrevious = ChkReplace.IsChecked == true;
            _s.SyncKFactor = ChkSyncK.IsChecked == true;
            if (_catalog != null)
            {
                _s.PipeTypeName = CmbPipe.SelectedItem as string ?? "";
                _s.SystemTypeName = CmbSystem.SelectedItem as string ?? "";
            }
        }

        // ================================================================== 1 · ROOMS
        void BtnScan_Click(object sender, RoutedEventArgs e)
        {
            var scope = CmbScope.SelectedIndex == 1 ? ScanScope.AllLevels : ScanScope.ActiveLevel;
            SetStatus("Scanning rooms…", Kind.Busy);
            RevitTask.Run(app =>
            {
                var doc = Doc(app);
                if (doc == null) { SetStatus("Open a project first.", Kind.Warn); return; }
                ScanInto(doc, app.ActiveUIDocument.ActiveView, scope);
                FillCatalog(doc);
                TxtHeaderInfo.Text = doc.Title + " · " + app.ActiveUIDocument.ActiveView.Name;
            });
        }

        void ScanInto(Document doc, View view, ScanScope scope)
        {
            var found = RoomScanner.Scan(doc, view, scope, _s);
            _rooms.Clear();
            foreach (var r in found)
            {
                r.PropertyChanged += (o, a) => { if (a.PropertyName == nameof(RoomInfo.Include)) UpdateRoomChips(); };
                _rooms.Add(r);
            }
            UpdateRoomChips();
            int linked = found.Count(r => r.IsLinked);
            if (found.Count == 0)
                SetStatus("No placed rooms found. Rooms must be placed and enclosed (host model or a loaded link).", Kind.Warn);
            else
                SetStatus($"{found.Count} rooms ({linked} from links). Check heights, then go to Plan.");
        }

        void UpdateRoomChips()
        {
            TxtRoomsTotal.Text = _rooms.Count.ToString();
            TxtRoomsOn.Text = _rooms.Count(r => r.Include && !r.IsExcluded).ToString();
            TxtRoomsOff.Text = _rooms.Count(r => r.IsExcluded).ToString();
        }

        bool FilterRoom(object o)
        {
            var q = TxtFilter?.Text?.Trim();
            if (string.IsNullOrEmpty(q)) return true;
            var r = (RoomInfo)o;
            return (r.Display + " " + r.LevelName).IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        void TxtFilter_TextChanged(object sender, TextChangedEventArgs e) => _roomView?.Refresh();

        void BtnAll_Click(object sender, RoutedEventArgs e)
        {
            foreach (RoomInfo r in _roomView) if (!r.IsExcluded) r.Include = true;
            UpdateRoomChips();
        }

        void BtnNone_Click(object sender, RoutedEventArgs e)
        {
            foreach (RoomInfo r in _roomView) r.Include = false;
            UpdateRoomChips();
        }

        void GoHeads_Click(object sender, RoutedEventArgs e) => Tabs.SelectedIndex = 1;
        void GoPlan_Click(object sender, RoutedEventArgs e) => Tabs.SelectedIndex = 2;
        void GoPlace_Click(object sender, RoutedEventArgs e) => Tabs.SelectedIndex = 3;

        // ================================================================== 2 · PLAN
        void BtnRefreshCatalog_Click(object sender, RoutedEventArgs e)
        {
            RevitTask.Run(app =>
            {
                var doc = Doc(app);
                if (doc != null) { FillCatalog(doc); SetStatus("Families reloaded."); }
            });
        }

        void FillCatalog(Document doc)
        {
            _catalog = Catalog.Read(doc, _lib);
            Fill(CmbPipe, _catalog.PipeTypes, _s.PipeTypeName, _catalog.PipeTypes.FirstOrDefault());
            Fill(CmbSystem, _catalog.SystemTypes, _s.SystemTypeName, _catalog.SystemTypes.FirstOrDefault());
            FamilyService.RefreshStatus(doc, _lib);
            BuildSlots();
        }

        static void Fill(ComboBox cb, List<string> items, string saved, string guess)
        {
            cb.ItemsSource = items;
            cb.SelectedItem = items.Contains(saved) ? saved : guess;
        }

        void BtnPlan_Click(object sender, RoutedEventArgs e)
        {
            GridRooms.CommitEdit(DataGridEditingUnit.Row, true);
            ReadUiToSettings();
            _s.Save();

            var targets = _rooms.Where(r => r.Include && !r.IsExcluded).ToList();
            if (targets.Count == 0) { SetStatus("Tick at least one room in step 1.", Kind.Warn); Tabs.SelectedIndex = 0; return; }
            foreach (var k in targets.Select(r => r.KOverride).Where(k => k != null && k != "Auto").Distinct()) _s.EnsureRule(k);

            // Pure geometry — runs instantly on the panel thread, no Revit API needed.
            _plans = targets.Select(r => GridPlanner.Plan(r, _s, FamilyAvailable)).ToList();
            foreach (var p in _plans)
                p.Room.Status = p.HasErrors ? "Cannot plan · " + p.Warnings.FirstOrDefault() : p.Summary;

            CmbPreview.ItemsSource = _plans.Select(p => p.Room.Display).ToList();
            CmbPreview.SelectedIndex = _plans.Count > 0 ? 0 : -1;

            int heads = _plans.Where(p => !p.HasErrors).Sum(p => p.HeadsWorld.Count);
            int branches = _plans.Where(p => p.Net != null).Sum(p => p.Net.Rows.Count);
            TxtPlRooms.Text = _plans.Count(p => !p.HasErrors).ToString();
            TxtPlHeads.Text = heads.ToString("N0");
            TxtPlBranches.Text = branches.ToString("N0");
            int warn = _plans.Count(p => p.Warnings.Count > 0);
            SetStatus($"Planned {_plans.Count} room(s), {heads:N0} heads." + (warn > 0 ? $" {warn} room(s) have notes — check the preview." : ""),
                warn > 0 ? Kind.Warn : Kind.Ok);
        }

        void CmbPreview_SelectionChanged(object sender, SelectionChangedEventArgs e) => DrawPreview();
        void Preview_SizeChanged(object sender, SizeChangedEventArgs e) => DrawPreview();

        RoomPlan CurrentPlan =>
            CmbPreview.SelectedIndex >= 0 && CmbPreview.SelectedIndex < _plans.Count ? _plans[CmbPreview.SelectedIndex] : null;

        void DrawPreview()
        {
            Preview.Children.Clear();
            var p = CurrentPlan;
            TxtPreviewEmpty.Visibility = p == null ? Visibility.Visible : Visibility.Collapsed;
            ListWarnings.ItemsSource = p?.Warnings;
            if (p == null || p.Room.Outline.Count < 3) return;

            TxtPHeads.Text = p.HeadsWorld.Count.ToString();
            TxtPK.Text = $"{p.KType} {(p.Pendant ? "pendant" : "upright")}";
            TxtPSpacing.Text = $"{Fmt(p.SxMm)}×{Fmt(p.SyMm)}";
            TxtPWall.Text = $"{p.ExMm:0}/{p.EyMm:0}";
            TxtPCov.Text = p.CoverageM2.ToString("0.0");

            double w = Preview.ActualWidth, h = Preview.ActualHeight;
            if (w < 20 || h < 20) return;

            var pts = p.Room.Outline;
            double minX = pts.Min(q => q.X), maxX = pts.Max(q => q.X), minY = pts.Min(q => q.Y), maxY = pts.Max(q => q.Y);
            double pad = 22;
            double sc = Math.Min((w - 2 * pad) / Math.Max(maxX - minX, 1e-6), (h - 2 * pad) / Math.Max(maxY - minY, 1e-6));
            double ox = (w - (maxX - minX) * sc) / 2, oy = (h - (maxY - minY) * sc) / 2;
            Func<P2, Point> M = q => new Point(ox + (q.X - minX) * sc, h - (oy + (q.Y - minY) * sc));

            // room
            var outline = new Polygon
            {
                Stroke = new SolidColorBrush(WColor.FromRgb(0x0F, 0x17, 0x2A)), StrokeThickness = 1.6,
                Fill = new SolidColorBrush(WColor.FromArgb(0xD0, 0xFF, 0xFF, 0xFF))
            };
            foreach (var q in pts) outline.Points.Add(M(q));
            Preview.Children.Add(outline);
            foreach (var hole in p.Room.Holes)
            {
                var hp = new Polygon { Stroke = Brushes.SlateGray, StrokeThickness = 1, Fill = new SolidColorBrush(WColor.FromRgb(0xE2, 0xE8, 0xF0)) };
                foreach (var q in hole) hp.Points.Add(M(q));
                Preview.Children.Add(hp);
            }

            // pipework
            if (p.Net != null && p.Net.Rows.Count > 0)
            {
                var net = p.Net;
                Func<double, double, Point> W = (a, c) => M(p.Frame.ToWorld(net.Local(a, c)));
                var blue = new SolidColorBrush(WColor.FromRgb(0x1C, 0x6E, 0xB5));
                var deep = new SolidColorBrush(WColor.FromRgb(0x13, 0x4E, 0x86));
                foreach (var r in net.Rows)
                {
                    double a0 = r.Left.Count > 0 ? r.Left.Last() : net.Feed;
                    double a1 = r.Right.Count > 0 ? r.Right.Last() : net.Feed;
                    AddLine(W(a0, r.Cross), W(a1, r.Cross), blue, 1.6);
                }
                AddLine(W(net.Feed, net.InletCross), W(net.Feed, net.Rows.Last().Cross), deep, 3.4);

                var inlet = W(net.Feed, net.InletCross);
                var tri = new Ellipse { Width = 10, Height = 10, Fill = deep, Stroke = Brushes.White, StrokeThickness = 1.5 };
                Canvas.SetLeft(tri, inlet.X - 5); Canvas.SetTop(tri, inlet.Y - 5);
                Preview.Children.Add(tri);
                var lbl = new TextBlock { Text = "IN", FontSize = 9, FontWeight = FontWeights.Bold, Foreground = deep };
                Canvas.SetLeft(lbl, inlet.X + 7); Canvas.SetTop(lbl, inlet.Y - 6);
                Preview.Children.Add(lbl);
            }

            // heads
            double r0 = Math.Max(3.0, Math.Min(6.0, sc * U.Ft(220)));
            var red = new SolidColorBrush(WColor.FromRgb(0xD6, 0x28, 0x28));
            foreach (var q in p.HeadsWorld)
            {
                var pt = M(q);
                var el = new Ellipse { Width = r0 * 2, Height = r0 * 2, Fill = red, Stroke = Brushes.White, StrokeThickness = 1 };
                Canvas.SetLeft(el, pt.X - r0); Canvas.SetTop(el, pt.Y - r0);
                Preview.Children.Add(el);
            }

            var cap = new TextBlock
            {
                Text = $"{U.Mm(maxX - minX) / 1000:0.0} × {U.Mm(maxY - minY) / 1000:0.0} m · H {p.Room.HeightMm:0} mm ({p.Room.HeightSource})",
                FontSize = 10, Foreground = new SolidColorBrush(WColor.FromRgb(0x47, 0x55, 0x69))
            };
            Canvas.SetLeft(cap, 8); Canvas.SetTop(cap, 4);
            Preview.Children.Add(cap);
        }

        static string Fmt(double v) => v <= 0 ? "—" : v.ToString("0");

        void AddLine(Point a, Point b, Brush br, double t) =>
            Preview.Children.Add(new Line { X1 = a.X, Y1 = a.Y, X2 = b.X, Y2 = b.Y, Stroke = br, StrokeThickness = t, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round });

        // ================================================================== 3 · PLACE
        void BtnPlace_Click(object sender, RoutedEventArgs e)
        {
            if (_plans.Count == 0) { SetStatus("Plan first (step 3).", Kind.Warn); Tabs.SelectedIndex = 2; return; }
            ReadUiToSettings();
            _s.Save();
            SetStatus("Placing sprinklers and building pipework…", Kind.Busy);
            BtnPlace.IsEnabled = false;
            RevitTask.Run(app =>
            {
                try
                {
                    var doc = Doc(app);
                    if (doc == null) return;
                    var res = new SprinklerBuilder(doc, _s, _lib).Build(_plans);
                    FamilyService.RefreshStatus(doc, _lib);
                    TxtPlaceResult.Text = res.Summary + (res.Messages.Count > 0 ? "\n" + string.Join("\n", res.Messages) : "");
                    SetStatus("Done · " + res.Summary, res.FittingFailures > 0 || res.Messages.Count > 0 ? Kind.Warn : Kind.Ok);
                }
                finally { BtnPlace.IsEnabled = true; }
            });
        }

        // ================================================================== 4 · SIZE
        void CmbTable_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CmbTable.SelectedItem is string t) _s.SizingTableMode = t;
            RefreshTable();
        }

        class TableRowVm { public string Label { get; set; } public string Size { get; set; } public string AssumedText { get; set; } }

        void RefreshTable()
        {
            if (ListTable == null || _s.Rules.Count == 0) return;
            string k = _s.SizingTableMode == "Auto" ? "K80" : _s.SizingTableMode;
            var rule = _s.Rule(k);
            TxtTableTitle.Text = "Pipe schedule · " + rule.Name + (_s.SizingTableMode == "Auto" ? " (Auto picks K160 when heads are K160/K320)" : "");
            var rows = new List<TableRowVm>(); int prev = 0;
            foreach (var r in rule.Sizing.OrderBy(x => x.MaxHeads))
            {
                string label = r.MaxHeads >= 9999 ? $"{prev + 1}+ heads" : (r.MaxHeads == prev + 1 ? $"{r.MaxHeads} head(s)" : $"{prev + 1}–{r.MaxHeads} heads");
                rows.Add(new TableRowVm { Label = label, Size = r.SizeMm + " mm", AssumedText = r.Assumed ? "assumed" : "" });
                prev = r.MaxHeads;
            }
            ListTable.ItemsSource = rows;
        }

        class PipeOnly : ISelectionFilter
        {
            public bool AllowElement(Element e) => e is Pipe;
            public bool AllowReference(Reference r, XYZ p) => true;
        }

        void BtnSizeNetwork_Click(object sender, RoutedEventArgs e)
        {
            SetStatus("Click the inlet pipe near its SUPPLY end (Esc to cancel)…", Kind.Busy);
            RevitTask.Run(app =>
            {
                var uidoc = app.ActiveUIDocument;
                if (uidoc == null) return;
                Reference picked;
                try { picked = uidoc.Selection.PickObject(ObjectType.Element, new PipeOnly(), "Click the inlet pipe near its supply end"); }
                catch (Autodesk.Revit.Exceptions.OperationCanceledException) { SetStatus("Sizing cancelled."); return; }

                var pipe = uidoc.Document.GetElement(picked) as Pipe;
                var res = NetworkSizer.Size(uidoc.Document, pipe, picked.GlobalPoint, _s, _lib);
                TxtSizeResult.Text = res.Summary + (res.Notes.Count > 0 ? "\n" + string.Join("\n", res.Notes) : "");
                SetStatus("Sized · " + res.Summary, res.Looped || res.Heads == 0 ? Kind.Warn : Kind.Ok);
            });
        }

        void BtnManualSize_Click(object sender, RoutedEventArgs e)
        {
            if (!(CmbManualSize.SelectedItem is int mm)) return;
            RevitTask.Run(app =>
            {
                var uidoc = app.ActiveUIDocument;
                var pipes = uidoc.Selection.GetElementIds().Select(id => uidoc.Document.GetElement(id)).OfType<Pipe>().ToList();
                if (pipes.Count == 0) { SetStatus("Select one or more pipes in Revit first.", Kind.Warn); return; }
                using (var t = new Transaction(uidoc.Document, "MorphLab · Manual pipe size"))
                {
                    t.Start();
                    foreach (var p in pipes)
                    {
                        p.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM)?.Set(U.Ft(mm));
                        Tagging.SetLock(p, true);
                    }
                    t.Commit();
                }
                SetStatus($"{pipes.Count} pipe(s) set to {mm} mm and locked.");
            });
        }

        void BtnUnlock_Click(object sender, RoutedEventArgs e)
        {
            RevitTask.Run(app =>
            {
                var uidoc = app.ActiveUIDocument;
                var pipes = uidoc.Selection.GetElementIds().Select(id => uidoc.Document.GetElement(id)).OfType<Pipe>().ToList();
                if (pipes.Count == 0) { SetStatus("Select pipes to unlock.", Kind.Warn); return; }
                using (var t = new Transaction(uidoc.Document, "MorphLab · Unlock pipe size"))
                {
                    t.Start();
                    foreach (var p in pipes) Tagging.SetLock(p, false);
                    t.Commit();
                }
                SetStatus($"{pipes.Count} pipe(s) unlocked — auto-sizing may change them again.");
            });
        }

        // ================================================================== 5 · CHECK
        void BtnQc_Click(object sender, RoutedEventArgs e)
        {
            SetStatus("Checking sprinklers…", Kind.Busy);
            RevitTask.Run(app =>
            {
                var uidoc = app.ActiveUIDocument;
                if (uidoc == null) return;
                if (_rooms.Count == 0) ScanInto(uidoc.Document, uidoc.ActiveView, ScanScope.AllLevels);
                _qc = QcChecker.Run(uidoc.Document, uidoc.ActiveView, _rooms.ToList(), _s, _lib);
                TxtQcChecked.Text = _qc.Checked.ToString("N0");
                TxtQcPass.Text = _qc.Passed.ToString("N0");
                TxtQcFail.Text = _qc.Fails.ToString("N0");
                TxtQcWarn.Text = _qc.Warns.ToString("N0");
                ListIssues.ItemsSource = _qc.Issues.OrderByDescending(i => i.IsFail).ThenBy(i => i.Room).ToList();
                SetStatus(_qc.Summary, _qc.Fails > 0 ? Kind.Warn : Kind.Ok);
            });
        }

        void ListIssues_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (!(ListIssues.SelectedItem is QcIssue issue)) return;
            RevitTask.Run(app =>
            {
                var uidoc = app.ActiveUIDocument;
                var ids = new List<ElementId> { issue.Id };
                uidoc.Selection.SetElementIds(ids);
                uidoc.ShowElements(ids);
            });
        }

        void BtnSelectFailing_Click(object sender, RoutedEventArgs e)
        {
            if (_qc == null) return;
            var ids = _qc.Issues.Where(i => i.IsFail).Select(i => i.Id).GroupBy(i => i.ToString()).Select(g => g.First()).ToList();
            RevitTask.Run(app =>
            {
                app.ActiveUIDocument.Selection.SetElementIds(ids);
                SetStatus($"{ids.Count} failing head(s) selected.");
            });
        }

        void BtnExportCsv_Click(object sender, RoutedEventArgs e)
        {
            if (_qc == null) { SetStatus("Run the check first.", Kind.Warn); return; }
            var folder = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "MorphLab", "SprinklerQC");
            var path = QcChecker.WriteCsv(_qc, folder);
            SetStatus("Saved " + path);
            try { System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + path + "\""); } catch { }
        }

        void BtnCount_Click(object sender, RoutedEventArgs e)
        {
            RevitTask.Run(app =>
            {
                var doc = Doc(app); if (doc == null) return;
                var c = ZoneTools.Counts(doc, _lib);
                TxtQtyUp.Text = c.up.ToString("N0");
                TxtQtyPend.Text = c.pend.ToString("N0");
                TxtQtyOther.Text = c.other.ToString("N0");
                TxtQtyByK.Text = c.byK;
                SetStatus($"Project: {c.up:N0} upright · {c.pend:N0} pendant · {c.other:N0} other.");
            });
        }

        void BtnSchedule_Click(object sender, RoutedEventArgs e)
        {
            RevitTask.Run(app =>
            {
                var uidoc = app.ActiveUIDocument; if (uidoc == null) return;
                var msg = ZoneTools.CreateLegendSchedule(uidoc.Document, out var vs);
                if (vs != null) uidoc.RequestViewChange(vs);
                SetStatus(msg);
            });
        }

        void BtnAssignZone_Click(object sender, RoutedEventArgs e)
        {
            string zone = TxtZone.Text;
            RevitTask.Run(app =>
            {
                var uidoc = app.ActiveUIDocument;
                var ids = uidoc.Selection.GetElementIds();
                if (ids.Count == 0) { SetStatus("Select the zone's pipes, fittings and heads first.", Kind.Warn); return; }
                int n = ZoneTools.Assign(uidoc.Document, ids, zone, out int skipped);
                SetStatus($"{n} element(s) set to {ZoneTools.Normalise(zone)}" + (skipped > 0 ? $" · {skipped} skipped (no Comments)" : "") + ". Now colour zones.");
            });
        }

        void BtnZoneColours_Click(object sender, RoutedEventArgs e)
        {
            RevitTask.Run(app =>
            {
                var uidoc = app.ActiveUIDocument;
                var msg = ZoneTools.ApplyColours(uidoc.Document, uidoc.ActiveView);
                SetStatus(msg, msg.StartsWith("Coloured") ? Kind.Ok : Kind.Warn);
            });
        }

        // ================================================================== 2 · HEADS (library)
        public class SlotOption { public string Value { get; set; } public string Label { get; set; } }

        /// <summary>One K-type row in "Which family is placed". Writes straight into the settings.</summary>
        public class SlotRow : INotifyPropertyChanged
        {
            readonly SprinklerSettings _s;
            public SlotRow(SprinklerSettings s, string k) { _s = s; K = k; }
            public string K { get; }
            public List<SlotOption> UprightOptions { get; set; }
            public List<SlotOption> PendantOptions { get; set; }
            public string Upright { get => _s.SlotValue(K, false); set { _s.SetSlot(K, false, value); Changed(); } }
            public string Pendant { get => _s.SlotValue(K, true); set { _s.SetSlot(K, true, value); Changed(); } }
            void Changed() { _s.Save(); PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null)); }
            public event PropertyChangedEventHandler PropertyChanged;
        }

        void ReloadLibraryList()
        {
            var sel = (ListLib.SelectedItem as LibraryFamily)?.Id;
            _libItems.Clear();
            foreach (var f in _lib.Items.OrderBy(i => SprinklerSettings.KValue(i.KType)).ThenBy(i => i.Orientation).ThenBy(i => i.BuiltIn ? 0 : 1))
                _libItems.Add(f);
            ListLib.SelectedItem = _libItems.FirstOrDefault(i => i.Id == sel);
        }

        void BuildSlots()
        {
            if (ListSlots == null) return;
            _slots.Clear();
            foreach (var k in _s.KNames)
            {
                var row = new SlotRow(_s, k) { UprightOptions = Options(k, false), PendantOptions = Options(k, true) };
                _slots.Add(row);
            }
        }

        List<SlotOption> Options(string k, bool pendant)
        {
            var list = new List<SlotOption>();
            var best = _lib.Best(k, pendant, out bool exact);
            string auto = best == null ? "Auto · none in library"
                        : exact ? "Auto · " + best.FamilyName
                        : $"Auto · {best.FamilyName} ({best.Orientation.ToLowerInvariant()})";
            list.Add(new SlotOption { Value = "", Label = auto });
            foreach (var f in _lib.Items.Where(i => i.Enabled && i.KType == k))
                list.Add(new SlotOption { Value = f.Id, Label = $"{f.FamilyName} · {f.Orientation}" });
            if (_catalog != null)
                foreach (var p in _catalog.Projects.Where(p => p.K == k && _lib.FindByFamilyName(p.FamilyName) == null))
                    list.Add(new SlotOption { Value = "proj:" + p.Name, Label = "Project · " + p.Name });
            // keep a saved choice visible even if it no longer matches
            var cur = _s.SlotValue(k, pendant);
            if (!string.IsNullOrEmpty(cur) && list.All(o => o.Value != cur))
                list.Add(new SlotOption { Value = cur, Label = "(missing) " + cur });
            return list;
        }

        /// <summary>Used by the planner: is there a family for this K + orientation?</summary>
        bool FamilyAvailable(string k, bool pendant)
        {
            var slot = _s.SlotValue(k, pendant);
            if (slot.StartsWith("proj:")) return _catalog == null || _catalog.Sprinklers.Contains(slot.Substring(5));
            if (!string.IsNullOrEmpty(slot) && _lib.Find(slot) != null) return true;
            if (_lib.Has(k, pendant)) return true;
            return _catalog != null && _catalog.Projects.Any(p => p.K == k && (p.Orientation == "Pendant") == pendant);
        }

        void LibChanged(string status = null)
        {
            _lib.Save();
            _s.Save();
            ReloadLibraryList();
            RefreshKOptions();
            ListRules.ItemsSource = null; ListRules.ItemsSource = _s.Rules;
            BuildSlots();
            if (status != null) SetStatus(status, status.Contains("⚠") ? Kind.Warn : Kind.Ok);
        }

        void BtnAddRfa_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Add sprinkler families to the MorphLab library",
                Filter = "Revit family (*.rfa)|*.rfa",
                Multiselect = true
            };
            if (dlg.ShowDialog() != true) return;
            var files = dlg.FileNames;
            SetStatus($"Reading {files.Length} famil{(files.Length == 1 ? "y" : "ies")}…", Kind.Busy);

            RevitTask.Run(app =>
            {
                var added = new List<LibraryFamily>(); int warn = 0;
                foreach (var path in files)
                {
                    try
                    {
                        var entry = _lib.AddFile(path);
                        FamilyService.Inspect(app.Application, entry);
                        _s.EnsureRule(entry.KType);
                        if (entry.HasWarnings) warn++;
                        added.Add(entry);
                    }
                    catch (Exception ex) { SetStatus($"{System.IO.Path.GetFileName(path)}: {ex.Message}", Kind.Error); }
                }
                var doc = Doc(app);
                if (doc != null) FamilyService.RefreshStatus(doc, _lib);
                LibChanged($"Added {added.Count}: " + string.Join(", ", added.Select(a => a.Title)) +
                           (warn > 0 ? $" · ⚠ {warn} with notes — check the amber boxes" : ""));
                if (added.Count > 0) ListLib.SelectedItem = _libItems.FirstOrDefault(i => i.Id == added.Last().Id);
            });
        }

        void BtnImportProject_Click(object sender, RoutedEventArgs e)
        {
            RevitTask.Run(app =>
            {
                var uidoc = app.ActiveUIDocument; if (uidoc == null) return;
                var doc = uidoc.Document;
                var fams = uidoc.Selection.GetElementIds().Select(id => doc.GetElement(id)).OfType<FamilyInstance>()
                    .Where(fi => NetworkSizer.IsSprinkler(fi)).Select(fi => fi.Symbol.Family).ToList();
                bool fromSelection = fams.Count > 0;
                if (!fromSelection)
                    fams = new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>()
                        .Where(f => f.FamilyCategory != null && f.FamilyCategory.Id.Equals(new ElementId(BuiltInCategory.OST_Sprinklers)))
                        .Where(f => _lib.FindByFamilyName(f.Name) == null).ToList();
                fams = fams.GroupBy(f => f.Id.ToString()).Select(g => g.First()).ToList();
                if (fams.Count == 0) { SetStatus("Every sprinkler family in this project is already in the library."); return; }

                int ok = 0; var errors = new List<string>();
                foreach (var f in fams)
                {
                    try { var en = FamilyService.ImportFromProject(doc, f, _lib); _s.EnsureRule(en.KType); ok++; }
                    catch (Exception ex) { errors.Add(f.Name + ": " + ex.Message); }
                }
                FamilyService.RefreshStatus(doc, _lib);
                LibChanged($"Imported {ok} famil{(ok == 1 ? "y" : "ies")} from the {(fromSelection ? "selection" : "project")}." +
                           (errors.Count > 0 ? " ⚠ " + string.Join("; ", errors) : ""));
            });
        }

        void BtnLoadAll_Click(object sender, RoutedEventArgs e)
        {
            ReadUiToSettings();
            SetStatus("Loading library families into the project…", Kind.Busy);
            RevitTask.Run(app =>
            {
                var doc = Doc(app); if (doc == null) return;
                var msg = FamilyService.LoadAll(doc, _lib, _s);
                FillCatalog(doc);
                SetStatus(msg);
            });
        }

        void LibEnabled_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded) return;
            _lib.Save();
            BuildSlots();
        }

        void ListLib_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var f = ListLib.SelectedItem as LibraryFamily;
            CardEdit.Visibility = f == null ? Visibility.Collapsed : Visibility.Visible;
            if (f == null) return;
            TxtEditTitle.Text = f.FamilyName;
            TxtEditK.Text = f.KFactor.ToString("0");
            CmbEditOrient.SelectedItem = f.Orientation;
            CmbEditType.ItemsSource = f.Types;
            CmbEditType.SelectedItem = f.TypeName;
            BtnRemoveLib.IsEnabled = !f.BuiltIn;
            BtnRemoveLib.ToolTip = f.BuiltIn ? "Built-in families can be switched off with the tick box, not removed." : null;
        }

        void BtnEditApply_Click(object sender, RoutedEventArgs e)
        {
            if (!(ListLib.SelectedItem is LibraryFamily f)) return;
            if (!int.TryParse(TxtEditK.Text.Trim().TrimStart('K', 'k'), out int k) || k < 20 || k > 1000)
            { SetStatus("Enter a K-factor like 80, 115, 160 or 240.", Kind.Warn); return; }
            f.KFactor = k;
            f.KType = "K" + k;
            f.Orientation = CmbEditOrient.SelectedItem as string ?? f.Orientation;
            f.TypeName = CmbEditType.SelectedItem as string ?? f.TypeName;
            _s.EnsureRule(f.KType);
            f.RefreshUi();
            LibChanged($"{f.FamilyName} → {f.Title}" + (f.BuiltIn ? " (built-in edits last until the plugin is updated)" : ""));
        }

        void BtnRemoveLib_Click(object sender, RoutedEventArgs e)
        {
            if (!(ListLib.SelectedItem is LibraryFamily f) || f.BuiltIn) return;
            var r = MessageBox.Show($"Remove {f.FamilyName} from your library?\nFamilies already placed in projects are not affected.",
                                    "MorphLab Sprinkler", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (r != MessageBoxResult.Yes) return;
            _lib.Remove(f);
            LibChanged($"Removed {f.FamilyName}.");
        }

        void BtnShowFile_Click(object sender, RoutedEventArgs e)
        {
            if (!(ListLib.SelectedItem is LibraryFamily f) || f.FileMissing) return;
            try { System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + f.FullPath + "\""); } catch { }
        }

        void BtnVariant_Click(object sender, RoutedEventArgs e)
        {
            if (!(ListLib.SelectedItem is LibraryFamily f)) { SetStatus("Select the head to copy first.", Kind.Warn); return; }
            if (!int.TryParse(TxtVariantK.Text.Trim().TrimStart('K', 'k'), out int k) || k < 20 || k > 1000)
            { SetStatus("Enter the new K-factor, e.g. 115.", Kind.Warn); return; }
            if (k == (int)f.KFactor) { SetStatus($"{f.FamilyName} is already K{k}.", Kind.Warn); return; }
            ReadUiToSettings();
            SetStatus($"Creating K{k} version of {f.FamilyName}…", Kind.Busy);
            RevitTask.Run(app =>
            {
                var doc = Doc(app);
                if (doc == null || doc.IsFamilyDocument) { SetStatus("Open a project (not a family) first.", Kind.Warn); return; }
                var entry = FamilyService.CreateVariant(doc, f, k, _lib, _s, out string msg);
                FamilyService.RefreshStatus(doc, _lib);
                LibChanged(entry == null ? "⚠ " + msg : msg + (_s.Rule(entry.KType).Note.StartsWith("Auto-created") ? $" A {entry.KType} rule was added — confirm it in Rules." : ""));
                if (entry != null) ListLib.SelectedItem = _libItems.FirstOrDefault(i => i.Id == entry.Id);
            });
        }

        // ================================================================== ⚙ · RULES
        void BtnSaveRules_Click(object sender, RoutedEventArgs e)
        {
            double D(TextBox tb, double fallback) =>
                double.TryParse(tb.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ||
                double.TryParse(tb.Text, out v) ? v : fallback;

            _s.DoubleHeightThresholdMm = D(TxtThreshold, _s.DoubleHeightThresholdMm);
            _s.DoubleHeightKType = CmbHighK.SelectedItem as string ?? "K160";
            _s.UprightBelowDeckMm = D(TxtUpBelow, _s.UprightBelowDeckMm);
            _s.UprightRiserNippleMm = D(TxtRiser, _s.UprightRiserNippleMm);
            _s.PendantBelowCeilingMm = D(TxtPendBelow, _s.PendantBelowCeilingMm);
            _s.PendantDropMm = D(TxtDrop, _s.PendantDropMm);
            _s.SpacingRoundingMm = D(TxtRound, _s.SpacingRoundingMm);
            _s.ReadCeilings = ChkCeilings.IsChecked == true;
            _s.ExcludedRoomKeywords = TxtKeywords.Text
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(k => k.Trim()).Where(k => k.Length > 0).ToList();

            foreach (var r in _s.Rules)
            {
                r.Sizing = r.Sizing.Where(x => x.MaxHeads > 0 && x.SizeMm > 0).OrderBy(x => x.MaxHeads).ToList();
                if (r.HeadToHeadMinMm > r.HeadToHeadMaxMm || r.WallMinMm > r.WallMaxMm)
                {
                    SetStatus($"{r.Name}: a minimum is larger than its maximum — fix before saving.", Kind.Error);
                    return;
                }
            }
            _s.SyncKFactor = ChkSyncK.IsChecked == true;
            _s.Save();
            LoadSettingsToUi();

            // re-apply exclusions / auto-K to already scanned rooms
            foreach (var room in _rooms)
            {
                room.ExcludedReason = RoomScanner.MatchExclusion(room.Name, _s.ExcludedRoomKeywords);
                room.AutoK = room.HeightMm >= _s.DoubleHeightThresholdMm ? _s.DoubleHeightKType : _s.NormalKType;
                if (room.IsExcluded) { room.Include = false; room.Status = "No sprinkler · " + room.ExcludedReason; }
            }
            _roomView.Refresh();
            UpdateRoomChips();
            SetStatus("Rules saved. Re-plan to apply them.");
        }

        void BtnResetRules_Click(object sender, RoutedEventArgs e)
        {
            var keep = _s;
            _s = SprinklerSettings.CreateDefault();
            _s.Slots = keep.Slots; _s.SyncKFactor = keep.SyncKFactor;
            foreach (var r in keep.Rules) if (!_s.HasRule(r.Name)) _s.Rules.Add(r);   // keep K-types added for library families
            _s.PipeTypeName = keep.PipeTypeName; _s.SystemTypeName = keep.SystemTypeName;
            LoadSettingsToUi();
            SetStatus("Rules reset to the notes defaults (not saved yet — press Save rules).", Kind.Warn);
        }
    }
}
