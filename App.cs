using System;
using System.Linq;
using System.Reflection;
using System.Windows.Media.Imaging;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using MorphLab.Sprinkler.Core;
using MorphLab.Sprinkler.Revit;
using MorphLab.Sprinkler.UI;

namespace MorphLab.Sprinkler
{
    public class App : IExternalApplication
    {
        public const string TabName = "MorphLab";
        public static readonly DockablePaneId PaneId = new DockablePaneId(new Guid("3F9A6C21-8B4E-4D7A-9E15-6C2B0F8A4D93"));
        internal static SprinklerPanel Panel;

        public Result OnStartup(UIControlledApplication app)
        {
            RevitTask.Init();

            // shared "MorphLab" tab — other MorphLab add-ins may have created it already
            try { app.CreateRibbonTab(TabName); } catch { }
            var panel = app.GetRibbonPanels(TabName).FirstOrDefault(p => p.Name == "Sprinkler Designer")
                        ?? app.CreateRibbonPanel(TabName, "Sprinkler Designer");

            string asm = Assembly.GetExecutingAssembly().Location;
            panel.AddItem(Btn("ML_Spk_Panel", "Sprinkler\nDesigner", asm, typeof(ShowPanelCommand), "head",
                "Open the MorphLab Sprinkler Designer panel: scan rooms → plan → place → size → check."));
            panel.AddSeparator();
            panel.AddItem(Btn("ML_Spk_Size", "Size\nNetwork", asm, typeof(SizeNetworkCommand), "pipe",
                "Pick the inlet pipe of a sprinkler network and size every pipe by the number of heads it feeds."));
            panel.AddItem(Btn("ML_Spk_Zones", "Zone\nColours", asm, typeof(ZoneColoursCommand), "zone",
                "Colour installation-valve zones (ZONE-1, ZONE-2 …) in the active view."));

            Panel = new SprinklerPanel();
            app.RegisterDockablePane(PaneId, "MorphLab Sprinkler", new PaneProvider(Panel));
            return Result.Succeeded;
        }

        public Result OnShutdown(UIControlledApplication app) => Result.Succeeded;

        static PushButtonData Btn(string name, string text, string asm, Type cmd, string icon, string tip)
        {
            return new PushButtonData(name, text, asm, cmd.FullName)
            {
                ToolTip = tip,
                LargeImage = Img(icon + "32.png"),
                Image = Img(icon + "16.png")
            };
        }

        static BitmapImage Img(string file)
        {
            try { return new BitmapImage(new Uri("pack://application:,,,/MorphLab.Sprinkler;component/Resources/" + file)); }
            catch { return null; }
        }
    }

    public class PaneProvider : IDockablePaneProvider
    {
        readonly SprinklerPanel _panel;
        public PaneProvider(SprinklerPanel panel) { _panel = panel; }

        public void SetupDockablePane(DockablePaneProviderData data)
        {
            data.FrameworkElement = _panel;
            data.InitialState = new DockablePaneState { DockPosition = DockPosition.Right };
        }
    }

    // ===================================================================== commands
    [Transaction(TransactionMode.Manual)]
    public class ShowPanelCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            try
            {
                var pane = data.Application.GetDockablePane(App.PaneId);
                if (pane.IsShown()) pane.Hide(); else pane.Show();
                return Result.Succeeded;
            }
            catch (Exception ex) { message = ex.Message; return Result.Failed; }
        }
    }

    [Transaction(TransactionMode.Manual)]
    public class SizeNetworkCommand : IExternalCommand
    {
        class PipeOnly : ISelectionFilter
        {
            public bool AllowElement(Element e) => e is Pipe;
            public bool AllowReference(Reference r, XYZ p) => true;
        }

        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            var uidoc = data.Application.ActiveUIDocument;
            try
            {
                var r = uidoc.Selection.PickObject(ObjectType.Element, new PipeOnly(), "Click the inlet pipe near its supply end");
                var pipe = (Pipe)uidoc.Document.GetElement(r);
                var res = NetworkSizer.Size(uidoc.Document, pipe, r.GlobalPoint, SprinklerSettings.Load(), FamilyLibrary.Load());
                TaskDialog.Show("MorphLab Sprinkler", res.Summary + (res.Notes.Count > 0 ? "\n\n" + string.Join("\n", res.Notes) : ""));
                return Result.Succeeded;
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException) { return Result.Cancelled; }
            catch (Exception ex) { message = ex.Message; return Result.Failed; }
        }
    }

    [Transaction(TransactionMode.Manual)]
    public class ZoneColoursCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            var uidoc = data.Application.ActiveUIDocument;
            try
            {
                TaskDialog.Show("MorphLab Sprinkler", ZoneTools.ApplyColours(uidoc.Document, uidoc.ActiveView));
                return Result.Succeeded;
            }
            catch (Exception ex) { message = ex.Message; return Result.Failed; }
        }
    }
}
