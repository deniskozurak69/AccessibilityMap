using KyivAccessibilityMap.Models;

namespace KyivAccessibilityMap.Services
{
    public class RoutingService
    {
        private readonly RoadGraphService _graphService;

        public RoutingService(RoadGraphService graphService)
        {
            _graphService = graphService;
        }

        // Знайти найближчу вершину графу до точки
        public GraphNode? FindNearestNode(RoadGraph graph, double lat, double lng)
        {
            GraphNode? nearestNode = null;
            double minDistance = double.MaxValue;

            foreach (var node in graph.Nodes.Values)
            {
                double distance = _graphService.CalculateDistance(lat, lng, node.Latitude, node.Longitude);
                if (distance < minDistance)
                {
                    minDistance = distance;
                    nearestNode = node;
                }
            }

            return nearestNode;
        }

        private double GetPenalty(GraphNode from, GraphNode to, List<SidewalkDto>? sidewalks)
        {
            if (sidewalks == null || sidewalks.Count == 0)
                return 1.0;

            // Знаходимо найближчу дорогу до середини ребра
            double midLat = (from.Latitude + to.Latitude) / 2;
            double midLon = (from.Longitude + to.Longitude) / 2;

            SidewalkDto? nearest = null;
            double minDist = double.MaxValue;

            foreach (var sidewalk in sidewalks)
            {
                if (sidewalk.Coordinates == null || sidewalk.Coordinates.Count == 0)
                    continue;

                foreach (var coord in sidewalk.Coordinates)
                {
                    double dist = _graphService.CalculateDistance(midLat, midLon, coord[0], coord[1]);
                    if (dist < minDist)
                    {
                        minDist = dist;
                        nearest = sidewalk;
                    }
                }
            }

            // Якщо дорога занадто далеко — ігноруємо
            if (nearest == null || minDist > 0.05)
                return 1.0;

            // Штраф за відсотком відповідності
            double percentage = nearest.Percentage;
            if (percentage >= 1.0) return 1.0;
            if (percentage >= 0.75) return 1.5;
            if (percentage >= 0.50) return 2.5;
            if (percentage >= 0.25) return 5.0;
            return 10.0;
        }

        // A* алгоритм пошуку найкоротшого шляху
        public RouteResult? FindRoute(RoadGraph graph, double startLat, double startLng, double endLat, double endLng, List<SidewalkDto>? sidewalks = null)
        {
            Console.WriteLine($"Пошук маршруту від ({startLat}, {startLng}) до ({endLat}, {endLng})");

            // Знаходимо найближчі вершини
            var startNode = FindNearestNode(graph, startLat, startLng);
            var endNode = FindNearestNode(graph, endLat, endLng);

            if (startNode == null || endNode == null)
            {
                Console.WriteLine("Не вдалося знайти вершини графу");
                return null;
            }

            Console.WriteLine($"Старт: вершина {startNode.Id}, Фініш: вершина {endNode.Id}");

            // A* алгоритм
            var openSet = new PriorityQueue<string, double>();
            var cameFrom = new Dictionary<string, string>();
            var gScore = new Dictionary<string, double>();
            var fScore = new Dictionary<string, double>();

            gScore[startNode.Id] = 0;
            fScore[startNode.Id] = Heuristic(startNode, endNode);
            openSet.Enqueue(startNode.Id, fScore[startNode.Id]);

            int iterations = 0;
            int maxIterations = 100000;

            while (openSet.Count > 0 && iterations < maxIterations)
            {
                iterations++;

                var currentId = openSet.Dequeue();

                if (currentId == endNode.Id)
                {
                    Console.WriteLine($"Маршрут знайдено за {iterations} ітерацій");
                    return ReconstructPath(graph, cameFrom, currentId, startLat, startLng, endLat, endLng);
                }

                var currentNode = graph.Nodes[currentId];

                foreach (var neighborId in currentNode.ConnectedNodeIds)
                {
                    if (!graph.Nodes.ContainsKey(neighborId))
                        continue;

                    var neighborNode = graph.Nodes[neighborId];

                    double edgeDistance = _graphService.CalculateDistance(
                        currentNode.Latitude, currentNode.Longitude,
                        neighborNode.Latitude, neighborNode.Longitude);

                    // Штрафний коефіцієнт
                    double penalty = GetPenalty(currentNode, neighborNode, sidewalks);

                    double tentativeGScore = gScore.GetValueOrDefault(currentId, double.MaxValue)
                        + edgeDistance * penalty;

                    if (tentativeGScore < gScore.GetValueOrDefault(neighborId, double.MaxValue))
                    {
                        cameFrom[neighborId] = currentId;
                        gScore[neighborId] = tentativeGScore;
                        fScore[neighborId] = tentativeGScore + Heuristic(neighborNode, endNode);
                        openSet.Enqueue(neighborId, fScore[neighborId]);
                    }
                }

                if (iterations % 10000 == 0)
                {
                    Console.WriteLine($"A* прогрес: {iterations} ітерацій, openSet: {openSet.Count}");
                }
            }

            Console.WriteLine($"Маршрут не знайдено після {iterations} ітерацій");
            return null;
        }

        // Евристика - пряма відстань до цілі
        private double Heuristic(GraphNode from, GraphNode to)
        {
            return _graphService.CalculateDistance(
                from.Latitude, from.Longitude,
                to.Latitude, to.Longitude);
        }

        // Відновлення шляху
        private RouteResult ReconstructPath(RoadGraph graph, Dictionary<string, string> cameFrom,
                                           string currentId, double startLat, double startLng,
                                           double endLat, double endLng)
        {
            var path = new List<GraphNode>();
            var current = currentId;

            while (cameFrom.ContainsKey(current))
            {
                path.Add(graph.Nodes[current]);
                current = cameFrom[current];
            }
            path.Add(graph.Nodes[current]); // Додаємо стартову вершину
            path.Reverse();

            // Розраховуємо загальну відстань
            double totalDistance = 0;
            for (int i = 0; i < path.Count - 1; i++)
            {
                totalDistance += _graphService.CalculateDistance(
                    path[i].Latitude, path[i].Longitude,
                    path[i + 1].Latitude, path[i + 1].Longitude);
            }

            // Додаємо відстань від початкової точки до першої вершини
            totalDistance += _graphService.CalculateDistance(
                startLat, startLng, path[0].Latitude, path[0].Longitude);

            // Додаємо відстань від останньої вершини до кінцевої точки
            totalDistance += _graphService.CalculateDistance(
                path[^1].Latitude, path[^1].Longitude, endLat, endLng);

            return new RouteResult
            {
                Path = path,
                TotalDistance = totalDistance,
                StartPoint = new RoutePoint { Latitude = startLat, Longitude = startLng },
                EndPoint = new RoutePoint { Latitude = endLat, Longitude = endLng },
                NearestStartNode = path[0],
                NearestEndNode = path[^1]
            };
        }
    }

    // Результат маршруту
    public class RouteResult
    {
        public List<GraphNode> Path { get; set; } = new List<GraphNode>();
        public double TotalDistance { get; set; }
        public RoutePoint StartPoint { get; set; } = new RoutePoint();
        public RoutePoint EndPoint { get; set; } = new RoutePoint();
        public GraphNode NearestStartNode { get; set; } = null!;
        public GraphNode NearestEndNode { get; set; } = null!;
    }

    public class RoutePoint
    {
        public double Latitude { get; set; }
        public double Longitude { get; set; }
    }
}