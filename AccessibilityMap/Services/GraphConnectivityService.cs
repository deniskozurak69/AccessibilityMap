using System.Text.Json;
using KyivAccessibilityMap.Models;

namespace KyivAccessibilityMap.Services
{
    public class GraphConnectivityService
    {
        private readonly RoadGraphService _graphService;
        private readonly List<BridgeEdgeRecord> _bridgeEdgesLog = new();

        public GraphConnectivityService(RoadGraphService graphService)
        {
            _graphService = graphService;
        }

        // Знайти всі компоненти зв'язності (ітеративний DFS)
        public List<HashSet<string>> FindConnectedComponents(RoadGraph graph)
        {
            var visited = new HashSet<string>();
            var components = new List<HashSet<string>>();

            foreach (var nodeId in graph.Nodes.Keys)
            {
                if (!visited.Contains(nodeId))
                {
                    var component = new HashSet<string>();
                    DFS(graph, nodeId, visited, component);
                    components.Add(component);
                }
            }

            return components;
        }

        private void DFS(RoadGraph graph, string startNodeId, HashSet<string> visited, HashSet<string> component)
        {
            var stack = new Stack<string>();
            stack.Push(startNodeId);

            while (stack.Count > 0)
            {
                var nodeId = stack.Pop();

                if (visited.Contains(nodeId))
                    continue;

                visited.Add(nodeId);
                component.Add(nodeId);

                if (graph.Nodes.TryGetValue(nodeId, out var node))
                {
                    foreach (var neighborId in node.ConnectedNodeIds)
                    {
                        if (!visited.Contains(neighborId))
                            stack.Push(neighborId);
                    }
                }
            }
        }

        // З'єднати компоненти - Borůvka's Algorithm з K-D Tree + DSU
        public RoadGraph ConnectComponents(RoadGraph graph, string? bridgeEdgesOutputPath = null)
        {
            _bridgeEdgesLog.Clear();

            Console.WriteLine("Пошук компонент зв'язності...");
            var components = FindConnectedComponents(graph);

            Console.WriteLine($"Знайдено {components.Count} компонент зв'язності");

            if (components.Count <= 1)
            {
                Console.WriteLine("Граф вже зв'язний!");
                return graph;
            }

            var sortedComponents = components.OrderByDescending(c => c.Count).ToList();
            for (int i = 0; i < Math.Min(10, sortedComponents.Count); i++)
                Console.WriteLine($"Компонента {i + 1}: {sortedComponents[i].Count} вершин");

            Console.WriteLine("Побудова K-D дерева...");
            var kdTree = new KDTree(graph);

            var componentMap = new Dictionary<string, int>();
            var componentList = new List<HashSet<string>>(components);

            for (int i = 0; i < componentList.Count; i++)
                foreach (var nodeId in componentList[i])
                    componentMap[nodeId] = i;

            var rng = new Random(42);
            int iteration = 0;
            int addedEdges = 0;
            int prevCount = componentList.Count;
            var totalSw = System.Diagnostics.Stopwatch.StartNew();

            while (componentList.Count > 1)
            {
                iteration++;
                int totalComponents = componentList.Count;
                Console.WriteLine($"\n=== Ітерація {iteration}: {totalComponents} компонент ===");

                var closestPairs = new Dictionary<int, (int targetComponent, GraphNode node1, GraphNode node2, double distance)>();

                int processed = 0;
                var iterSw = System.Diagnostics.Stopwatch.StartNew();

                // Знаходимо найближчу пару для кожної компоненти
                for (int i = 0; i < componentList.Count; i++)
                {
                    var component = componentList[i];
                    double minDistance = double.MaxValue;
                    GraphNode? bestNode1 = null;
                    GraphNode? bestNode2 = null;
                    int targetComponentId = -1;

                    var sampleSize = Math.Min(50, component.Count);
                    var sampledNodes = GetRandomSample(component, sampleSize, rng);

                    foreach (var nodeId in sampledNodes)
                    {
                        var node = graph.Nodes[nodeId];
                        var nearest = kdTree.FindNearestFromOtherComponent(node, componentMap, i);

                        if (nearest != null && nearest.Value.distance < minDistance)
                        {
                            minDistance = nearest.Value.distance;
                            bestNode1 = node;
                            bestNode2 = nearest.Value.node;
                            targetComponentId = componentMap[nearest.Value.node.Id];
                        }
                    }

                    if (targetComponentId != -1)
                        closestPairs[i] = (targetComponentId, bestNode1!, bestNode2!, minDistance);

                    processed++;

                    // Прогрес кожні 500 компонент або кожні 10 секунд
                    if (processed % 500 == 0 || iterSw.Elapsed.TotalSeconds >= 10)
                    {
                        iterSw.Restart();
                        double pct = processed * 100.0 / totalComponents;
                        Console.WriteLine($"  Прогрес: {processed}/{totalComponents} ({pct:F1}%) | Загальний час: {totalSw.Elapsed:mm\\:ss}");
                    }
                }

                // Злиття через DSU — всі знайдені пари зливаються за одну ітерацію
                var dsu = new DSU(componentList.Count);
                int mergedThisIteration = 0;

                foreach (var (i, pair) in closestPairs)
                {
                    int rootI = dsu.Find(i);
                    int rootT = dsu.Find(pair.targetComponent);

                    if (rootI != rootT)
                    {
                        AddBridgeEdge(graph, pair.node1, pair.node2, pair.distance, iteration);
                        addedEdges++;
                        mergedThisIteration++;
                        dsu.Union(rootI, rootT);
                    }
                }

                Console.WriteLine($"  Злито пар: {mergedThisIteration}, додано ребер: {addedEdges}");

                // Перебудова списку компонент на основі DSU
                var groupMap = new Dictionary<int, HashSet<string>>();
                for (int i = 0; i < componentList.Count; i++)
                {
                    int root = dsu.Find(i);
                    if (!groupMap.ContainsKey(root))
                        groupMap[root] = new HashSet<string>();
                    groupMap[root].UnionWith(componentList[i]);
                }

                var newComponents = groupMap.Values.ToList();
                var newComponentMap = new Dictionary<string, int>();
                int idx = 0;
                foreach (var comp in newComponents)
                {
                    foreach (var nodeId in comp)
                        newComponentMap[nodeId] = idx;
                    idx++;
                }

                componentList = newComponents;
                componentMap = newComponentMap;

                Console.WriteLine($"Після ітерації {iteration}: {componentList.Count} компонент | Час: {totalSw.Elapsed:mm\\:ss}");

                if (componentList.Count == prevCount)
                {
                    Console.WriteLine("Не вдалося знайти нові з'єднання, зупиняємось");
                    break;
                }
                prevCount = componentList.Count;
            }

            Console.WriteLine($"\nГотово! Додано {addedEdges} ребер за {iteration} ітерацій, загальний час: {totalSw.Elapsed:mm\\:ss}");

            // Зберігаємо bridge edges у JSON
            var outputPath = bridgeEdgesOutputPath ?? "bridge_edges.json";
            SaveBridgeEdges(outputPath);

            return graph;
        }

        private void SaveBridgeEdges(string path)
        {
            var output = new
            {
                metadata = new
                {
                    totalBridgeEdges = _bridgeEdgesLog.Count,
                    generatedAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")
                },
                bridgeEdges = _bridgeEdgesLog
            };

            var json = JsonSerializer.Serialize(output, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json, System.Text.Encoding.UTF8);
            Console.WriteLine($"Bridge edges збережено: {path} ({_bridgeEdgesLog.Count} ребер)");
        }

        // O(n) reservoir sampling замість O(n log n) через Guid.NewGuid()
        private static List<string> GetRandomSample(HashSet<string> set, int count, Random rng)
        {
            if (set.Count <= count)
                return set.ToList();

            var result = new List<string>(count);
            int needed = count;
            int remaining = set.Count;

            foreach (var item in set)
            {
                if (rng.Next(remaining) < needed)
                {
                    result.Add(item);
                    needed--;
                    if (needed == 0) break;
                }
                remaining--;
            }
            return result;
        }

        private void AddBridgeEdge(RoadGraph graph, GraphNode node1, GraphNode node2, double distance, int iteration = 0)
        {
            if (!node1.ConnectedNodeIds.Contains(node2.Id))
                node1.ConnectedNodeIds.Add(node2.Id);

            if (!node2.ConnectedNodeIds.Contains(node1.Id))
                node2.ConnectedNodeIds.Add(node1.Id);

            string edgeId = _graphService.GetEdgeIdPublic(node1.Id, node2.Id);

            if (!graph.Edges.ContainsKey(edgeId))
            {
                var edge = new GraphEdge
                {
                    Id = edgeId,
                    NodeId1 = node1.Id,
                    NodeId2 = node2.Id,
                    SidewalkId = -1,
                    Distance = distance,
                    Surface = "unknown",
                    Width = 0.0,
                    Wheelchair = "unknown",
                    Lit = "unknown",
                    Tactile_Paving = "unknown",
                    Smoothness = "unknown",
                    AccessibilityScore = 0,
                    AccessibilityPercentage = 0.0,
                    Weight = distance * 2.0
                };

                graph.Edges[edgeId] = edge;

                // Логуємо для збереження в JSON
                _bridgeEdgesLog.Add(new BridgeEdgeRecord
                {
                    EdgeId = edgeId,
                    Iteration = iteration,
                    Node1 = new NodeRecord { Id = node1.Id, Latitude = node1.Latitude, Longitude = node1.Longitude },
                    Node2 = new NodeRecord { Id = node2.Id, Latitude = node2.Latitude, Longitude = node2.Longitude },
                    DistanceMeters = Math.Round(distance, 2)
                });
            }
        }

        public object GetConnectivityStats(RoadGraph graph)
        {
            var components = FindConnectedComponents(graph);
            var sortedComponents = components.OrderByDescending(c => c.Count).ToList();

            return new
            {
                totalComponents = components.Count,
                isConnected = components.Count == 1,
                largestComponent = sortedComponents.First().Count,
                smallestComponent = sortedComponents.Last().Count,
                components = sortedComponents.Select((c, i) => new
                {
                    id = i + 1,
                    nodeCount = c.Count,
                    percentage = (c.Count * 100.0) / graph.NodeCount
                }).ToList()
            };
        }

        // Disjoint Set Union (Union-Find) з path compression та union by rank
        private class DSU
        {
            private readonly int[] parent;
            private readonly int[] rank;

            public DSU(int n)
            {
                parent = Enumerable.Range(0, n).ToArray();
                rank = new int[n];
            }

            public int Find(int x)
            {
                while (parent[x] != x)
                {
                    parent[x] = parent[parent[x]]; // path halving
                    x = parent[x];
                }
                return x;
            }

            public void Union(int x, int y)
            {
                if (rank[x] < rank[y]) (x, y) = (y, x);
                parent[y] = x;
                if (rank[x] == rank[y]) rank[x]++;
            }
        }

        // K-D Tree для швидкого пошуку найближчих сусідів
        private class KDTree
        {
            private class KDNode
            {
                public GraphNode GraphNode { get; set; } = null!;
                public KDNode? Left { get; set; }
                public KDNode? Right { get; set; }
                public int Axis { get; set; }
            }

            private KDNode? root;

            public KDTree(RoadGraph graph)
            {
                var nodes = graph.Nodes.Values.ToList();
                Console.WriteLine($"Побудова K-D дерева з {nodes.Count} вершин...");
                root = BuildTree(nodes, 0);
                Console.WriteLine("K-D дерево побудовано");
            }

            private KDNode? BuildTree(List<GraphNode> nodes, int depth)
            {
                if (nodes.Count == 0) return null;

                int axis = depth % 2;

                nodes.Sort((a, b) =>
                {
                    double valA = axis == 0 ? a.Latitude : a.Longitude;
                    double valB = axis == 0 ? b.Latitude : b.Longitude;
                    return valA.CompareTo(valB);
                });

                int median = nodes.Count / 2;

                return new KDNode
                {
                    GraphNode = nodes[median],
                    Axis = axis,
                    Left = BuildTree(nodes.Take(median).ToList(), depth + 1),
                    Right = BuildTree(nodes.Skip(median + 1).ToList(), depth + 1)
                };
            }

            public (GraphNode node, double distance)? FindNearestFromOtherComponent(
                GraphNode target, Dictionary<string, int> componentMap, int excludeComponentId)
            {
                GraphNode? bestNode = null;
                double bestDistance = double.MaxValue;

                void Search(KDNode? node, int depth)
                {
                    if (node == null) return;

                    if (componentMap[node.GraphNode.Id] != excludeComponentId)
                    {
                        double distance = CalculateDistance(target, node.GraphNode);
                        if (distance < bestDistance)
                        {
                            bestDistance = distance;
                            bestNode = node.GraphNode;
                        }
                    }

                    int axis = depth % 2;
                    double targetValue = axis == 0 ? target.Latitude : target.Longitude;
                    double nodeValue = axis == 0 ? node.GraphNode.Latitude : node.GraphNode.Longitude;

                    KDNode? first = targetValue < nodeValue ? node.Left : node.Right;
                    KDNode? second = targetValue < nodeValue ? node.Right : node.Left;

                    Search(first, depth + 1);

                    // ВИПРАВЛЕНО: конвертуємо різницю градусів у метри перед порівнянням
                    double metersPerDegree = axis == 0
                        ? 111000.0
                        : 111000.0 * Math.Cos(ToRadians(target.Latitude));
                    double axisDistanceMeters = Math.Abs(targetValue - nodeValue) * metersPerDegree;

                    if (axisDistanceMeters < bestDistance)
                        Search(second, depth + 1);
                }

                Search(root, 0);

                return bestNode != null ? (bestNode, bestDistance) : null;
            }

            private double CalculateDistance(GraphNode a, GraphNode b)
            {
                const double R = 6371000;
                double dLat = ToRadians(b.Latitude - a.Latitude);
                double dLng = ToRadians(b.Longitude - a.Longitude);

                double x = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                           Math.Cos(ToRadians(a.Latitude)) * Math.Cos(ToRadians(b.Latitude)) *
                           Math.Sin(dLng / 2) * Math.Sin(dLng / 2);

                double c = 2 * Math.Atan2(Math.Sqrt(x), Math.Sqrt(1 - x));
                return R * c;
            }

            private double ToRadians(double degrees) => degrees * Math.PI / 180.0;
        }

        // Моделі для серіалізації в JSON
        private class BridgeEdgeRecord
        {
            public string EdgeId { get; set; } = "";
            public int Iteration { get; set; }
            public NodeRecord Node1 { get; set; } = null!;
            public NodeRecord Node2 { get; set; } = null!;
            public double DistanceMeters { get; set; }
        }

        private class NodeRecord
        {
            public string Id { get; set; } = "";
            public double Latitude { get; set; }
            public double Longitude { get; set; }
        }
    }
}