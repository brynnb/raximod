using System.Numerics;

namespace Raximod.Generation.Continents
{
    /// <summary>Subtracts placed structure/terrain overlap in native Z-up coordinates.</summary>
    public static class TerrainTriangleClipper
    {
        private const double Epsilon = 1e-9;

        public readonly record struct Vertex(Vector3 Position, Vector3 Normal);
        public readonly record struct Foundation(
            Vector3 A, Vector3 B, Vector3 C, int Id, string Record, int PlacementIndex);
        public sealed record Result(IReadOnlyList<IReadOnlyList<Vertex>> Polygons, double RemovedArea)
        {
            public bool Changed => RemovedArea > Epsilon;
        }

        // Keep clipping intersections in double precision until the final GLB vertex conversion.
        // A millimetre-wide HART skirt contact at (8000,8000) disappears in float shoelace sums;
        // rounding every intermediate intersection to a Vector3 also loses those thin strips.
        private readonly record struct Point(double X, double Y)
        {
            public static Point operator -(Point a, Point b) => new(a.X - b.X, a.Y - b.Y);
            public static Point Lerp(Point a, Point b, double t) =>
                new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);
            public double LengthSquared => X * X + Y * Y;
        }

        /// <summary>
        /// Clips height differences against the actual two planes, including sloped foundation
        /// skirts. The existing height window excludes unrelated upper floors; contactEpsilon
        /// absorbs source/output float quantization, not a guessed expanded footprint.
        /// </summary>
        public static Result Subtract(
            Vertex a, Vertex b, Vertex c, Foundation foundation,
            float maximumHeightAboveTerrain, float contactEpsilon)
        {
            var terrain = new List<Point> { ToPlanar(a.Position), ToPlanar(b.Position), ToPlanar(c.Position) };
            var footprint = new List<Point> { ToPlanar(foundation.A), ToPlanar(foundation.B), ToPlanar(foundation.C) };
            double terrainArea = SignedArea(terrain);
            double foundationArea = SignedArea(footprint);
            if (Math.Abs(terrainArea) <= Epsilon || Math.Abs(foundationArea) <= Epsilon)
                return new Result(new[] { (IReadOnlyList<Vertex>)new[] { a, b, c } }, 0);
            bool clockwise = terrainArea < 0;
            if (clockwise) terrain.Reverse();
            if (foundationArea < 0) footprint.Reverse();

            List<Point> overlap = terrain;
            for (int index = 0; index < footprint.Count && overlap.Count >= 3; index++)
            {
                Point start = footprint[index], edge = footprint[(index + 1) % footprint.Count] - start;
                overlap = Split(overlap, point => Cross(edge, point - start)).Inside;
            }
            double HeightDifference(Point point) =>
                SurfaceHeight(foundation.A, foundation.B, foundation.C, point)
                - SurfaceHeight(a.Position, b.Position, c.Position, point);
            overlap = Split(overlap, point => HeightDifference(point) + contactEpsilon).Inside;
            overlap = Split(overlap, point => maximumHeightAboveTerrain - HeightDifference(point)).Inside;
            double removed = Math.Abs(SignedArea(overlap));
            if (overlap.Count < 3 || removed <= Epsilon)
                return new Result(new[] { (IReadOnlyList<Vertex>)new[] { a, b, c } }, 0);

            var outside = new List<List<Point>>();
            List<Point> inside = terrain;
            for (int index = 0; index < overlap.Count && inside.Count >= 3; index++)
            {
                Point start = overlap[index], edge = overlap[(index + 1) % overlap.Count] - start;
                var split = Split(inside, point => Cross(edge, point - start));
                if (split.Outside.Count >= 3 && Math.Abs(SignedArea(split.Outside)) > Epsilon)
                    outside.Add(split.Outside);
                inside = split.Inside;
            }
            var polygons = outside.Select(polygon =>
            {
                if (clockwise) polygon.Reverse();
                return (IReadOnlyList<Vertex>)polygon.Select(point => OnTerrain(point, a, b, c)).ToArray();
            }).ToArray();
            return new Result(polygons, removed);
        }

        private static (List<Point> Inside, List<Point> Outside) Split(
            IReadOnlyList<Point> polygon, Func<Point, double> value)
        {
            var inside = new List<Point>();
            var outside = new List<Point>();
            if (polygon.Count == 0) return (inside, outside);
            Point previous = polygon[^1];
            double previousValue = value(previous);
            foreach (Point current in polygon)
            {
                double currentValue = value(current);
                // Split at zero on both sides. Classifying at -epsilon but intersecting at zero
                // can extrapolate beyond an edge and create overlapping or reversed fragments.
                bool previousInside = previousValue >= 0, currentInside = currentValue >= 0;
                if (previousInside != currentInside)
                {
                    Point intersection = Point.Lerp(previous, current,
                        previousValue / (previousValue - currentValue));
                    inside.Add(intersection);
                    outside.Add(intersection);
                }
                (currentInside ? inside : outside).Add(current);
                previous = current;
                previousValue = currentValue;
            }
            return (DistinctAdjacent(inside), DistinctAdjacent(outside));
        }

        private static (double First, double Second) Weights(Vector3 a, Vector3 b, Vector3 c, Point point)
        {
            Point ac = ToPlanar(a) - ToPlanar(c), bc = ToPlanar(b) - ToPlanar(c);
            Point pc = point - ToPlanar(c);
            double denominator = Cross(ac, bc);
            return (Cross(pc, bc) / denominator, Cross(ac, pc) / denominator);
        }

        private static double SurfaceHeight(Vector3 a, Vector3 b, Vector3 c, Point point)
        {
            var (first, second) = Weights(a, b, c, point);
            return first * a.Z + second * b.Z + (1 - first - second) * c.Z;
        }

        private static Vertex OnTerrain(Point point, Vertex a, Vertex b, Vertex c)
        {
            var (first, second) = Weights(a.Position, b.Position, c.Position, point);
            double third = 1 - first - second;
            var position = new Vector3((float)point.X, (float)point.Y,
                (float)(first * a.Position.Z + second * b.Position.Z + third * c.Position.Z));
            Vector3 normal = a.Normal * (float)first + b.Normal * (float)second + c.Normal * (float)third;
            if (normal.LengthSquared() > Epsilon * Epsilon) normal = Vector3.Normalize(normal);
            return new Vertex(position, normal);
        }

        private static List<Point> DistinctAdjacent(List<Point> values)
        {
            var result = new List<Point>(values.Count);
            foreach (Point value in values)
                if (result.Count == 0 || (result[^1] - value).LengthSquared > Epsilon * Epsilon)
                    result.Add(value);
            if (result.Count > 1 && (result[0] - result[^1]).LengthSquared <= Epsilon * Epsilon)
                result.RemoveAt(result.Count - 1);
            return result;
        }

        private static Point ToPlanar(Vector3 value) => new(value.X, value.Y);
        private static double Cross(Point a, Point b) => a.X * b.Y - a.Y * b.X;
        private static double SignedArea(IReadOnlyList<Point> polygon)
        {
            if (polygon.Count < 3) return 0;
            double sum = 0;
            // Local origin avoids subtracting nearly equal continent-sized products.
            for (int index = 1; index + 1 < polygon.Count; index++)
                sum += Cross(polygon[index] - polygon[0], polygon[index + 1] - polygon[0]);
            return sum * 0.5;
        }
    }
}
