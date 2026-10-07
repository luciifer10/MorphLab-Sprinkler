using System;
using System.Collections.Generic;

namespace MorphLab.Sprinkler.Core
{
    /// <summary>Unit conversion. Revit internal units are decimal feet.</summary>
    public static class U
    {
        public static double Ft(double mm) => mm / 304.8;
        public static double Mm(double ft) => ft * 304.8;
    }

    public struct P2
    {
        public double X, Y;
        public P2(double x, double y) { X = x; Y = y; }
        public static P2 operator +(P2 a, P2 b) => new P2(a.X + b.X, a.Y + b.Y);
        public static P2 operator -(P2 a, P2 b) => new P2(a.X - b.X, a.Y - b.Y);
        public static P2 operator *(P2 a, double k) => new P2(a.X * k, a.Y * k);
        public double Len => Math.Sqrt(X * X + Y * Y);
        public double Dot(P2 o) => X * o.X + Y * o.Y;
        public double Dist(P2 o) => (this - o).Len;
    }

    /// <summary>A rotated local frame aligned to the room's dominant wall.</summary>
    public struct Frame
    {
        public P2 Origin, Ux, Uy;
        public Frame(P2 origin, double angle)
        {
            Origin = origin;
            Ux = new P2(Math.Cos(angle), Math.Sin(angle));
            Uy = new P2(-Math.Sin(angle), Math.Cos(angle));
        }
        public P2 ToLocal(P2 w) { var d = w - Origin; return new P2(d.Dot(Ux), d.Dot(Uy)); }
        public P2 ToWorld(P2 l) => Origin + Ux * l.X + Uy * l.Y;
        public P2 ToWorld(double x, double y) => ToWorld(new P2(x, y));
    }

    public static class Poly
    {
        public static bool Contains(IList<P2> poly, P2 p)
        {
            bool inside = false;
            for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
            {
                var a = poly[i]; var b = poly[j];
                if (((a.Y > p.Y) != (b.Y > p.Y)) &&
                    (p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y + 1e-12) + a.X))
                    inside = !inside;
            }
            return inside;
        }

        public static double SegDist(P2 p, P2 a, P2 b, out P2 closest)
        {
            var ab = b - a; double l2 = ab.Dot(ab);
            double t = l2 < 1e-12 ? 0 : Math.Max(0, Math.Min(1, (p - a).Dot(ab) / l2));
            closest = a + ab * t;
            return p.Dist(closest);
        }

        /// <summary>Distance from p to the nearest edge of the outline or any hole.</summary>
        public static double EdgeDist(P2 p, IList<P2> outer, IList<List<P2>> holes, out P2 nearest)
        {
            double best = double.MaxValue; nearest = p;
            Scan(outer, p, ref best, ref nearest);
            if (holes != null) foreach (var h in holes) Scan(h, p, ref best, ref nearest);
            return best;
        }

        static void Scan(IList<P2> poly, P2 p, ref double best, ref P2 nearest)
        {
            for (int i = 0; i < poly.Count; i++)
            {
                var a = poly[i]; var b = poly[(i + 1) % poly.Count];
                double d = SegDist(p, a, b, out var c);
                if (d < best) { best = d; nearest = c; }
            }
        }

        public static double Area(IList<P2> poly)
        {
            double s = 0;
            for (int i = 0; i < poly.Count; i++)
            {
                var a = poly[i]; var b = poly[(i + 1) % poly.Count];
                s += a.X * b.Y - b.X * a.Y;
            }
            return Math.Abs(s) / 2;
        }

        /// <summary>Angle of the longest edge, folded into [0, π/2) so grids stay "upright".</summary>
        public static double DominantAngle(IList<P2> poly)
        {
            double bestLen = -1, ang = 0;
            for (int i = 0; i < poly.Count; i++)
            {
                var d = poly[(i + 1) % poly.Count] - poly[i];
                if (d.Len > bestLen) { bestLen = d.Len; ang = Math.Atan2(d.Y, d.X); }
            }
            double half = Math.PI / 2;
            ang %= half; if (ang < 0) ang += half;
            if (ang > half - 1e-4) ang = 0;
            return ang;
        }
    }
}
