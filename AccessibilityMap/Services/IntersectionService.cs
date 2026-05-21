
using KyivAccessibilityMap.Models;

namespace KyivAccessibilityMap.Services
{
    public class IntersectionService
    {
        // Spatial grid для швидкого пошуку
        private class SpatialGrid
        {
            private Dictionary<string, List<SegmentInfo>> grid = new();
            private double cellSize;

            public SpatialGrid(double cellSize = 0.001) // ~100м на клітинку
            {
                this.cellSize = cellSize;
            }

            private string GetCellKey(double lat, double lng)
            {
                int x = (int)(lng / cellSize);
                int y = (int)(lat / cellSize);
                return $"{x},{y}";
            }

            public void AddSegment(SegmentInfo segment)
            {
                var cells = GetCellsCoveredBySegment(segment);
                foreach (var cell in cells)
                {
                    if (!grid.ContainsKey(cell))
                        grid[cell] = new List<SegmentInfo>();
                    grid[cell].Add(segment);
                }
            }

            private List<string> GetCellsCoveredBySegment(SegmentInfo segment)
            {
                var cells = new HashSet<string>();

                double minLat = Math.Min(segment.Lat1, segment.Lat2);
                double maxLat = Math.Max(segment.Lat1, segment.Lat2);
                double minLng = Math.Min(segment.Lng1, segment.Lng2);
                double maxLng = Math.Max(segment.Lng1, segment.Lng2);

                int minX = (int)(minLng / cellSize);
                int maxX = (int)(maxLng / cellSize);
                int minY = (int)(minLat / cellSize);
                int maxY = (int)(maxLat / cellSize);

                for (int x = minX; x <= maxX; x++)
                {
                    for (int y = minY; y <= maxY; y++)
                    {
                        cells.Add($"{x},{y}");
                    }
                }

                return cells.ToList();
            }

            public List<SegmentInfo> GetPotentialIntersections(SegmentInfo segment)
            {
                var candidates = new HashSet<SegmentInfo>();
                var cells = GetCellsCoveredBySegment(segment);

                foreach (var cell in cells)
                {
                    if (grid.ContainsKey(cell))
                    {
                        foreach (var other in grid[cell])
                        {
                            if (other.Id != segment.Id)
                                candidates.Add(other);
                        }
                    }
                }

                return candidates.ToList();
            }
        }

        public class SegmentInfo
        {
            public string Id { get; set; } = string.Empty;
            public double Lat1 { get; set; }
            public double Lng1 { get; set; }
            public double Lat2 { get; set; }
            public double Lng2 { get; set; }
            public long SidewalkId { get; set; }
            public Sidewalk Sidewalk { get; set; } = null!;
        }

        public class IntersectionPoint
        {
            public double Lat { get; set; }
            public double Lng { get; set; }
            public string SegmentId1 { get; set; } = string.Empty;
            public string SegmentId2 { get; set; } = string.Empty;
        }

        public List<IntersectionPoint> FindAllIntersections(List<Sidewalk> sidewalks)
        {
            var intersections = new List<IntersectionPoint>();
            var grid = new SpatialGrid();
            var allSegments = new List<SegmentInfo>();

            Console.WriteLine("Створення просторового індексу...");

            foreach (var sidewalk in sidewalks)
            {
                if (sidewalk.Coordinates == null || sidewalk.Coordinates.Count < 2)
                    continue;

                for (int i = 0; i < sidewalk.Coordinates.Count - 1; i++)
                {
                    var segment = new SegmentInfo
                    {
                        Id = $"{sidewalk.Id}_{i}",
                        Lat1 = sidewalk.Coordinates[i][0],
                        Lng1 = sidewalk.Coordinates[i][1],
                        Lat2 = sidewalk.Coordinates[i + 1][0],
                        Lng2 = sidewalk.Coordinates[i + 1][1],
                        SidewalkId = sidewalk.Id,
                        Sidewalk = sidewalk
                    };

                    allSegments.Add(segment);
                    grid.AddSegment(segment);
                }
            }

            Console.WriteLine($"Створено {allSegments.Count} відрізків");
            Console.WriteLine("Пошук перетинів...");

            int checkedPairs = 0;
            int found = 0;

            foreach (var segment in allSegments)
            {
                var candidates = grid.GetPotentialIntersections(segment);

                foreach (var other in candidates)
                {
                    if (segment.SidewalkId == other.SidewalkId)
                        continue;

                    if (string.Compare(segment.Id, other.Id) >= 0)
                        continue;

                    checkedPairs++;

                    var intersection = FindSegmentIntersection(segment, other);
                    if (intersection != null)
                    {
                        intersections.Add(intersection);
                        found++;
                    }
                }

                if (checkedPairs % 100000 == 0)
                {
                    Console.WriteLine($"Перевірено {checkedPairs} пар, знайдено {found} перетинів");
                }
            }

            Console.WriteLine($"Завершено! Перевірено {checkedPairs} пар, знайдено {intersections.Count} перетинів");

            return intersections;
        }

        private IntersectionPoint? FindSegmentIntersection(SegmentInfo s1, SegmentInfo s2)
        {
            double x1 = s1.Lng1, y1 = s1.Lat1;
            double x2 = s1.Lng2, y2 = s1.Lat2;
            double x3 = s2.Lng1, y3 = s2.Lat1;
            double x4 = s2.Lng2, y4 = s2.Lat2;

            double denom = (x1 - x2) * (y3 - y4) - (y1 - y2) * (x3 - x4);

            if (Math.Abs(denom) < 1e-10)
                return null;

            double t = ((x1 - x3) * (y3 - y4) - (y1 - y3) * (x3 - x4)) / denom;
            double u = -((x1 - x2) * (y1 - y3) - (y1 - y2) * (x1 - x3)) / denom;

            if (t >= 0 && t <= 1 && u >= 0 && u <= 1)
            {
                const double epsilon = 1e-7;
                if (t < epsilon || t > 1 - epsilon || u < epsilon || u > 1 - epsilon)
                    return null;

                return new IntersectionPoint
                {
                    Lat = y1 + t * (y2 - y1),
                    Lng = x1 + t * (x2 - x1),
                    SegmentId1 = s1.Id,
                    SegmentId2 = s2.Id
                };
            }

            return null;
        }
    }
}