using KyivAccessibilityMap.Models;
using System.Text.Json;

namespace KyivAccessibilityMap.Services
{
    public class RoadGraphService
    {
        // Побудова графу з доріг
        public RoadGraph BuildGraph(List<Sidewalk> sidewalks)
        {
            var graph = new RoadGraph();

            foreach (var sidewalk in sidewalks)
            {
                if (sidewalk.Coordinates == null || sidewalk.Coordinates.Count < 2)
                    continue;

                // Проходимо по всіх відрізках ламаної лінії
                for (int i = 0; i < sidewalk.Coordinates.Count - 1; i++)
                {
                    var point1 = sidewalk.Coordinates[i];
                    var point2 = sidewalk.Coordinates[i + 1];

                    // Створюємо ID вершин (округлюємо до 7 знаків для уникнення дублікатів)
                    string nodeId1 = GetNodeId(point1[0], point1[1]);
                    string nodeId2 = GetNodeId(point2[0], point2[1]);

                    // Додаємо вершини якщо їх ще немає
                    if (!graph.Nodes.ContainsKey(nodeId1))
                    {
                        graph.Nodes[nodeId1] = new GraphNode
                        {
                            Id = nodeId1,
                            Latitude = point1[0],
                            Longitude = point1[1]
                        };
                    }

                    if (!graph.Nodes.ContainsKey(nodeId2))
                    {
                        graph.Nodes[nodeId2] = new GraphNode
                        {
                            Id = nodeId2,
                            Latitude = point2[0],
                            Longitude = point2[1]
                        };
                    }

                    // Додаємо зв'язки між вершинами
                    if (!graph.Nodes[nodeId1].ConnectedNodeIds.Contains(nodeId2))
                    {
                        graph.Nodes[nodeId1].ConnectedNodeIds.Add(nodeId2);
                    }
                    if (!graph.Nodes[nodeId2].ConnectedNodeIds.Contains(nodeId1))
                    {
                        graph.Nodes[nodeId2].ConnectedNodeIds.Add(nodeId1);
                    }

                    // Створюємо ребро
                    string edgeId = GetEdgeId(nodeId1, nodeId2);
                    if (!graph.Edges.ContainsKey(edgeId))
                    {
                        double distance = CalculateDistance(point1[0], point1[1], point2[0], point2[1]);

                        var edge = new GraphEdge
                        {
                            Id = edgeId,
                            NodeId1 = nodeId1,
                            NodeId2 = nodeId2,
                            SidewalkId = sidewalk.Id,
                            Distance = distance,
                            Surface = sidewalk.Surface,
                            Width = sidewalk.Width ?? 0.0,
                            Wheelchair = sidewalk.Wheelchair,
                            Lit = sidewalk.Lit,
                            Tactile_Paving = sidewalk.Tactile_Paving,
                            Smoothness = sidewalk.Smoothness
                        };

                        // Оцінка доступності ребра (базова)
                        edge.AccessibilityScore = CalculateEdgeAccessibility(edge);
                        edge.AccessibilityPercentage = edge.AccessibilityScore / 6.0;

                        // Вага ребра (менша вага = краще для пошуку шляху)
                        edge.Weight = CalculateEdgeWeight(edge);

                        graph.Edges[edgeId] = edge;
                    }
                }
            }

            return graph;
        }

        // Генерація ID вершини з координат
        private string GetNodeId(double lat, double lng)
        {
            return $"{lat:F7},{lng:F7}";
        }

        // Генерація ID ребра (завжди в одному порядку для уникнення дублікатів)
        private string GetEdgeId(string nodeId1, string nodeId2)
        {
            if (string.Compare(nodeId1, nodeId2) < 0)
                return $"{nodeId1}|{nodeId2}";
            else
                return $"{nodeId2}|{nodeId1}";
        }


        public string GetEdgeIdPublic(string nodeId1, string nodeId2)
        {
            if (string.Compare(nodeId1, nodeId2) < 0)
                return $"{nodeId1}|{nodeId2}";
            else
                return $"{nodeId2}|{nodeId1}";
        }

        // Розрахунок відстані між двома точками (формула Haversine)
        public double CalculateDistance(double lat1, double lng1, double lat2, double lng2)
        {
            const double R = 6371000; // Радіус Землі в метрах

            var dLat = ToRadians(lat2 - lat1);
            var dLng = ToRadians(lng2 - lng1);

            var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                    Math.Cos(ToRadians(lat1)) * Math.Cos(ToRadians(lat2)) *
                    Math.Sin(dLng / 2) * Math.Sin(dLng / 2);

            var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));

            return R * c;
        }

        private double ToRadians(double degrees)
        {
            return degrees * Math.PI / 180.0;
        }

        // Оцінка доступності ребра (повертає 0-6)
        private int CalculateEdgeAccessibility(GraphEdge edge)
        {
            int score = 0;

            if (edge.Wheelchair == "yes") score++;
            if (edge.Lit == "yes") score++;
            if (edge.Tactile_Paving == "yes") score++;
            if (!string.IsNullOrEmpty(edge.Surface) && edge.Surface != "unknown") score++;
            if (!string.IsNullOrEmpty(edge.Smoothness) && edge.Smoothness != "unknown") score++;
            if (edge.Width >= 1.5) score++;

            return score;
        }

        // Розрахунок ваги ребра для алгоритму пошуку шляху
        private double CalculateEdgeWeight(GraphEdge edge)
        {
            // Базова вага = відстань
            double weight = edge.Distance;

            // Збільшуємо вагу для менш доступних ребер
            // Чим менша доступність - тим більша вага
            double accessibilityPenalty = (1.0 - edge.AccessibilityPercentage) * edge.Distance * 0.5;

            return weight + accessibilityPenalty;
        }

        // Знайти найближчу вершину графу до заданої точки
        public GraphNode? FindNearestNode(RoadGraph graph, double lat, double lng)
        {
            GraphNode? nearestNode = null;
            double minDistance = double.MaxValue;

            foreach (var node in graph.Nodes.Values)
            {
                double distance = CalculateDistance(lat, lng, node.Latitude, node.Longitude);
                if (distance < minDistance)
                {
                    minDistance = distance;
                    nearestNode = node;
                }
            }

            return nearestNode;
        }

        public RoadGraph BuildGraphWithIntersections(List<Sidewalk> sidewalks, IntersectionService intersectionService)
        {
            Console.WriteLine("Пошук перетинів відрізків...");
            var intersections = intersectionService.FindAllIntersections(sidewalks);

            Console.WriteLine($"Знайдено {intersections.Count} перетинів");
            Console.WriteLine("Побудова графу з урахуванням перетинів...");

            var graph = new RoadGraph();
            var segmentToIntersections = new Dictionary<string, List<IntersectionService.IntersectionPoint>>();

            foreach (var intersection in intersections)
            {
                if (!segmentToIntersections.ContainsKey(intersection.SegmentId1))
                    segmentToIntersections[intersection.SegmentId1] = new List<IntersectionService.IntersectionPoint>();
                if (!segmentToIntersections.ContainsKey(intersection.SegmentId2))
                    segmentToIntersections[intersection.SegmentId2] = new List<IntersectionService.IntersectionPoint>();

                segmentToIntersections[intersection.SegmentId1].Add(intersection);
                segmentToIntersections[intersection.SegmentId2].Add(intersection);
            }

            foreach (var sidewalk in sidewalks)
            {
                if (sidewalk.Coordinates == null || sidewalk.Coordinates.Count < 2)
                    continue;

                for (int i = 0; i < sidewalk.Coordinates.Count - 1; i++)
                {
                    string segmentId = $"{sidewalk.Id}_{i}";
                    var point1 = sidewalk.Coordinates[i];
                    var point2 = sidewalk.Coordinates[i + 1];

                    if (segmentToIntersections.ContainsKey(segmentId))
                    {
                        var segmentIntersections = segmentToIntersections[segmentId];

                        segmentIntersections = segmentIntersections
                            .OrderBy(p => Math.Pow(p.Lat - point1[0], 2) + Math.Pow(p.Lng - point1[1], 2))
                            .ToList();

                        var points = new List<(double lat, double lng)>();
                        points.Add((point1[0], point1[1]));

                        foreach (var inter in segmentIntersections)
                        {
                            points.Add((inter.Lat, inter.Lng));
                        }

                        points.Add((point2[0], point2[1]));

                        for (int j = 0; j < points.Count - 1; j++)
                        {
                            AddEdgeToGraph(graph, points[j].lat, points[j].lng,
                                          points[j + 1].lat, points[j + 1].lng, sidewalk);
                        }
                    }
                    else
                    {
                        AddEdgeToGraph(graph, point1[0], point1[1], point2[0], point2[1], sidewalk);
                    }
                }
            }

            Console.WriteLine($"Граф побудовано: {graph.NodeCount} вершин, {graph.EdgeCount} ребер");
            return graph;
        }

        private void AddEdgeToGraph(RoadGraph graph, double lat1, double lng1,
                                    double lat2, double lng2, Sidewalk sidewalk)
        {
            string nodeId1 = GetNodeId(lat1, lng1);
            string nodeId2 = GetNodeId(lat2, lng2);

            if (!graph.Nodes.ContainsKey(nodeId1))
            {
                graph.Nodes[nodeId1] = new GraphNode
                {
                    Id = nodeId1,
                    Latitude = lat1,
                    Longitude = lng1
                };
            }

            if (!graph.Nodes.ContainsKey(nodeId2))
            {
                graph.Nodes[nodeId2] = new GraphNode
                {
                    Id = nodeId2,
                    Latitude = lat2,
                    Longitude = lng2
                };
            }

            if (!graph.Nodes[nodeId1].ConnectedNodeIds.Contains(nodeId2))
                graph.Nodes[nodeId1].ConnectedNodeIds.Add(nodeId2);

            if (!graph.Nodes[nodeId2].ConnectedNodeIds.Contains(nodeId1))
                graph.Nodes[nodeId2].ConnectedNodeIds.Add(nodeId1);

            string edgeId = GetEdgeId(nodeId1, nodeId2);
            if (!graph.Edges.ContainsKey(edgeId))
            {
                double distance = CalculateDistance(lat1, lng1, lat2, lng2);

                var edge = new GraphEdge
                {
                    Id = edgeId,
                    NodeId1 = nodeId1,
                    NodeId2 = nodeId2,
                    SidewalkId = sidewalk.Id,
                    Distance = distance,
                    Surface = sidewalk.Surface,
                    Width = sidewalk.Width ?? 0.0,
                    Wheelchair = sidewalk.Wheelchair,
                    Lit = sidewalk.Lit,
                    Tactile_Paving = sidewalk.Tactile_Paving,
                    Smoothness = sidewalk.Smoothness
                };

                edge.AccessibilityScore = CalculateEdgeAccessibilityFromSidewalk(sidewalk);
                edge.AccessibilityPercentage = edge.AccessibilityScore / 6.0;
                edge.Weight = CalculateEdgeWeight(edge);

                graph.Edges[edgeId] = edge;
            }
        }

        private int CalculateEdgeAccessibilityFromSidewalk(Sidewalk sidewalk)
        {
            int score = 0;
            if (sidewalk.Wheelchair == "yes") score++;
            if (sidewalk.Lit == "yes") score++;
            if (sidewalk.Tactile_Paving == "yes") score++;
            if (!string.IsNullOrEmpty(sidewalk.Surface) && sidewalk.Surface != "unknown") score++;
            if (!string.IsNullOrEmpty(sidewalk.Smoothness) && sidewalk.Smoothness != "unknown") score++;
            if ((sidewalk.Width ?? 0.0) >= 1.5) score++;
            return score;
        }
        public async Task EnrichGraphWithClassified(RoadGraph graph, string classifiedPath)
        {
            if (!File.Exists(classifiedPath)) return;

            var json = await File.ReadAllTextAsync(classifiedPath);
            using var document = JsonDocument.Parse(json);

            foreach (var entry in document.RootElement.EnumerateObject())
            {
                // Парсимо координати з ключа: "lat1_lng1_lat2_lng2"
                var parts = entry.Name.Split('_');
                if (parts.Length != 4) continue;

                if (!double.TryParse(parts[0], System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out double lat1) ||
                    !double.TryParse(parts[1], System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out double lng1) ||
                    !double.TryParse(parts[2], System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out double lat2) ||
                    !double.TryParse(parts[3], System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out double lng2))
                    continue;

                var e = entry.Value;

                string GetStr(string key) =>
                    e.TryGetProperty(key, out var v) ? v.GetString() ?? "" : "";

                // Нормалізація
                string lit = GetStr("lit") == "lit" ? "yes" : "no";
                string wheelchair = GetStr("ramp") == "ramp" ? "yes" : "no";
                string surface = NormalizeSurface(GetStr("surface_type"));
                string smoothness = GetStr("smoothness");  // slight/severe/smooth
                double width = GetStr("width") == "wide" ? 2.0 : 1.0;
                string tactile = GetStr("tactile_paving") == "yes" ? "yes" : "no";

                string nodeId1 = $"{lat1:F7},{lng1:F7}";
                string nodeId2 = $"{lat2:F7},{lng2:F7}";

                // Додаємо вершини якщо не існують
                if (!graph.Nodes.ContainsKey(nodeId1))
                    graph.Nodes[nodeId1] = new GraphNode { Id = nodeId1, Latitude = lat1, Longitude = lng1 };

                if (!graph.Nodes.ContainsKey(nodeId2))
                    graph.Nodes[nodeId2] = new GraphNode { Id = nodeId2, Latitude = lat2, Longitude = lng2 };

                // Зв'язки
                if (!graph.Nodes[nodeId1].ConnectedNodeIds.Contains(nodeId2))
                    graph.Nodes[nodeId1].ConnectedNodeIds.Add(nodeId2);
                if (!graph.Nodes[nodeId2].ConnectedNodeIds.Contains(nodeId1))
                    graph.Nodes[nodeId2].ConnectedNodeIds.Add(nodeId1);

                // Ребро
                string edgeId = GetEdgeId(nodeId1, nodeId2);
                if (!graph.Edges.ContainsKey(edgeId))
                {
                    double distance = CalculateDistance(lat1, lng1, lat2, lng2);

                    var edge = new GraphEdge
                    {
                        Id = edgeId,
                        NodeId1 = nodeId1,
                        NodeId2 = nodeId2,
                        SidewalkId = -2,  // -2 = з edges_classified
                        Distance = distance,
                        Surface = surface,
                        Width = width,
                        Wheelchair = wheelchair,
                        Lit = lit,
                        Tactile_Paving = tactile,
                        Smoothness = smoothness
                    };

                    edge.AccessibilityScore = CalculateEdgeAccessibility(edge);
                    edge.AccessibilityPercentage = edge.AccessibilityScore / 6.0;
                    edge.Weight = CalculateEdgeWeight(edge);

                    graph.Edges[edgeId] = edge;
                }
            }

            Console.WriteLine($"Граф після збагачення: {graph.NodeCount} вершин, {graph.EdgeCount} ребер");
        }

        private string NormalizeSurface(string s) => s switch
        {
            "asphalt" or "concrete" => "asphalt",
            "paving_stones" or "sett" => "paving_stones",
            "unpaved" => "gravel",
            _ => s
        };
    }
}
