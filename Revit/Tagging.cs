using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;

namespace MorphLab.Sprinkler.Revit
{
    /// <summary>
    /// Invisible data stored on every element the plugin creates:
    ///   RoomKey    -> which room generated it, so a re-run can replace instead of duplicate
    ///   SizeLocked -> designer fixed the size manually; auto-sizing must not touch it
    /// </summary>
    public static class Tagging
    {
        static readonly Guid SchemaGuid = new Guid("B7E2D0C4-61A9-4F3E-8C27-5D1E0A9F4B83");
        const string FRoom = "RoomKey";
        const string FLock = "SizeLocked";

        static Schema Get()
        {
            var s = Schema.Lookup(SchemaGuid);
            if (s != null) return s;
            var b = new SchemaBuilder(SchemaGuid);
            b.SetSchemaName("MorphLabSprinklerData");
            b.SetVendorId("MORPHLAB");
            b.SetReadAccessLevel(AccessLevel.Public);
            b.SetWriteAccessLevel(AccessLevel.Public);
            b.AddSimpleField(FRoom, typeof(string));
            b.AddSimpleField(FLock, typeof(bool));
            return b.Finish();
        }

        static Entity Read(Element e)
        {
            var s = Get();
            var ent = e.GetEntity(s);
            return ent != null && ent.IsValid() ? ent : new Entity(s);
        }

        public static void Mark(Element e, string roomKey)
        {
            if (e == null) return;
            try
            {
                var ent = Read(e);
                ent.Set(FRoom, roomKey ?? "");
                e.SetEntity(ent);
            }
            catch { }
        }

        public static string RoomKey(Element e)
        {
            try
            {
                var ent = e.GetEntity(Get());
                return ent != null && ent.IsValid() ? ent.Get<string>(FRoom) : null;
            }
            catch { return null; }
        }

        public static void SetLock(Element e, bool locked)
        {
            try
            {
                var ent = Read(e);
                ent.Set(FLock, locked);
                e.SetEntity(ent);
            }
            catch { }
        }

        public static bool IsLocked(Element e)
        {
            try
            {
                var ent = e.GetEntity(Get());
                return ent != null && ent.IsValid() && ent.Get<bool>(FLock);
            }
            catch { return false; }
        }

        static readonly BuiltInCategory[] Cats =
        {
            BuiltInCategory.OST_Sprinklers, BuiltInCategory.OST_PipeCurves,
            BuiltInCategory.OST_PipeFitting, BuiltInCategory.OST_PipeAccessory
        };

        /// <summary>All elements previously generated for the given room.</summary>
        public static List<ElementId> ElementsForRoom(Document doc, string roomKey)
        {
            var filter = new ElementMulticategoryFilter(Cats.ToList());
            return new FilteredElementCollector(doc).WherePasses(filter).WhereElementIsNotElementType()
                .Where(e => RoomKey(e) == roomKey)
                .Select(e => e.Id).ToList();
        }
    }
}
