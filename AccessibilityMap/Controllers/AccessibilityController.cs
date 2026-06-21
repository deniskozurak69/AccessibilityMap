namespace KyivAccessibilityMap.Controllers
{
    using KyivAccessibilityMap.Models;
    using KyivAccessibilityMap.Services;
    using Microsoft.AspNetCore.Mvc;
    using System.Text.Json;

    [ApiController]
    [Route("api/[controller]")]
    public class AccessibilityController : ControllerBase
    {
        private readonly IWebHostEnvironment _env;
        private readonly RoadGraphService _graphService;
        private readonly IntersectionService _intersectionService;
        private readonly GraphConnectivityService _connectivityService;
        private readonly RoutingService _routingService;
        private RoadGraph? _cachedGraph = null;

        public AccessibilityController(IWebHostEnvironment env,
                                       RoadGraphService graphService,
                                       IntersectionService intersectionService,
                                       GraphConnectivityService connectivityService,
                                       RoutingService routingService)
        {
            _env = env;
            _graphService = graphService;
            _intersectionService = intersectionService;
            _connectivityService = connectivityService;
            _routingService = routingService;
        }

        private Sidewalk ParseSidewalkFromJson(JsonElement item)
        {
            var sidewalk = new Sidewalk
            {
                Id = item.TryGetProperty("id", out var id) ? id.GetInt64() : 0,
                Name = item.TryGetProperty("name", out var name) && name.ValueKind != JsonValueKind.Null
                    ? name.GetString() : null,
                Type = item.TryGetProperty("type", out var type) ? type.GetString() ?? "" : "",
                Surface = item.TryGetProperty("surface", out var surface) ? surface.GetString() ?? "" : "",
                Width_Source = item.TryGetProperty("width_source", out var ws) ? ws.GetString() ?? "" : "",
                Wheelchair = item.TryGetProperty("wheelchair", out var wc) ? wc.GetString() ?? "" : "",
                Lit = item.TryGetProperty("lit", out var lit) ? lit.GetString() ?? "" : "",
                Tactile_Paving = item.TryGetProperty("tactile_paving", out var tp) ? tp.GetString() ?? "" : "",
                Smoothness = item.TryGetProperty("smoothness", out var sm) ? sm.GetString() ?? "" : ""
            };

            // Парсимо width гнучко
            if (item.TryGetProperty("width", out var widthElement))
            {
                if (widthElement.ValueKind == JsonValueKind.Number)
                {
                    sidewalk.Width = widthElement.GetDouble();
                }
                else if (widthElement.ValueKind == JsonValueKind.String)
                {
                    if (double.TryParse(widthElement.GetString(), out var widthValue))
                    {
                        sidewalk.Width = widthValue;
                    }
                }
            }

            // Парсимо coordinates
            if (item.TryGetProperty("coordinates", out var coords) && coords.ValueKind == JsonValueKind.Array)
            {
                sidewalk.Coordinates = new List<List<double>>();
                foreach (var coord in coords.EnumerateArray())
                {
                    if (coord.ValueKind == JsonValueKind.Array && coord.GetArrayLength() == 2)
                    {
                        var coordPair = new List<double>
                {
                    coord[0].GetDouble(),
                    coord[1].GetDouble()
                };
                        sidewalk.Coordinates.Add(coordPair);
                    }
                }
            }

            return sidewalk;
        }



        [HttpGet("graph")]
        public async Task<ActionResult<object>> GetGraph([FromQuery] bool includeIntersections = false)
        {
            try
            {
                var sidewalksPath = Path.Combine(_env.WebRootPath, "data", "sidewalks.json");

                if (!System.IO.File.Exists(sidewalksPath))
                {
                    return NotFound(new { message = "Файл sidewalks.json не знайдено" });
                }

                var sidewalksJson = await System.IO.File.ReadAllTextAsync(sidewalksPath);

                using var document = JsonDocument.Parse(sidewalksJson);
                var root = document.RootElement;

                if (!root.TryGetProperty("sidewalks", out var sidewalksArray))
                {
                    return BadRequest(new { message = "Не знайдено масив 'sidewalks' в JSON" });
                }

                var sidewalks = new List<Sidewalk>();
                int skipped = 0;

                foreach (var item in sidewalksArray.EnumerateArray())
                {
                    try
                    {
                        var sidewalk = ParseSidewalkFromJson(item);
                        if (sidewalk.Coordinates != null && sidewalk.Coordinates.Count >= 2)
                        {
                            sidewalks.Add(sidewalk);
                        }
                        else
                        {
                            skipped++;
                        }
                    }
                    catch (Exception ex)
                    {
                        skipped++;
                        Console.WriteLine($"Помилка парсингу дороги: {ex.Message}");
                    }
                }

                Console.WriteLine($"Завантажено доріг для графу: {sidewalks.Count}, пропущено: {skipped}");

                RoadGraph graph;
                if (includeIntersections)
                {
                    graph = _graphService.BuildGraphWithIntersections(sidewalks, _intersectionService);
                }
                else
                {
                    graph = _graphService.BuildGraph(sidewalks);
                }

                return Ok(new
                {
                    nodeCount = graph.NodeCount,
                    edgeCount = graph.EdgeCount,
                    sidewalksProcessed = sidewalks.Count,
                    sidewalksSkipped = skipped,
                    withIntersections = includeIntersections,
                    message = $"Граф побудовано успішно: {graph.NodeCount} вершин, {graph.EdgeCount} ребер"
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new
                {
                    message = $"Помилка побудови графу: {ex.Message}",
                    stack = ex.StackTrace
                });
            }
        }

        // Новий ендпоінт для отримання вершин графу
        [HttpGet("graph/nodes")]
        public async Task<ActionResult<object>> GetGraphNodes([FromQuery] bool includeIntersections = false)
        {
            try
            {
                var sidewalksPath = Path.Combine(_env.WebRootPath, "data", "sidewalks.json");

                if (!System.IO.File.Exists(sidewalksPath))
                {
                    return NotFound(new { message = "Файл sidewalks.json не знайдено" });
                }

                var sidewalksJson = await System.IO.File.ReadAllTextAsync(sidewalksPath);

                using var document = JsonDocument.Parse(sidewalksJson);
                var root = document.RootElement;

                if (!root.TryGetProperty("sidewalks", out var sidewalksArray))
                {
                    return BadRequest(new { message = "Не знайдено масив 'sidewalks' в JSON" });
                }

                var sidewalks = new List<Sidewalk>();

                foreach (var item in sidewalksArray.EnumerateArray())
                {
                    try
                    {
                        var sidewalk = ParseSidewalkFromJson(item);
                        if (sidewalk.Coordinates != null && sidewalk.Coordinates.Count >= 2)
                        {
                            sidewalks.Add(sidewalk);
                        }
                    }
                    catch { }
                }

                // Знаходимо перетини якщо потрібно
                List<IntersectionService.IntersectionPoint> intersections = new();
                if (includeIntersections)
                {
                    intersections = _intersectionService.FindAllIntersections(sidewalks);
                }

                RoadGraph graph;
                if (includeIntersections)
                {
                    graph = _graphService.BuildGraphWithIntersections(sidewalks, _intersectionService);
                }
                else
                {
                    graph = _graphService.BuildGraph(sidewalks);
                }

                // Створюємо набір координат перетинів для швидкого пошуку
                var intersectionCoords = new HashSet<string>();
                foreach (var inter in intersections)
                {
                    string coordKey = $"{inter.Lat:F7},{inter.Lng:F7}";
                    intersectionCoords.Add(coordKey);
                }

                // Повертаємо вершини з позначкою чи це перетин
                var nodes = graph.Nodes.Values.Select(n => new
                {
                    id = n.Id,
                    lat = n.Latitude,
                    lng = n.Longitude,
                    connections = n.ConnectedNodeIds.Count,
                    isIntersection = intersectionCoords.Contains(n.Id)
                }).ToList();

                return Ok(new
                {
                    nodes = nodes,
                    totalNodes = nodes.Count,
                    intersectionNodes = nodes.Count(n => n.isIntersection),
                    regularNodes = nodes.Count(n => !n.isIntersection),
                    withIntersections = includeIntersections
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new
                {
                    message = $"Помилка: {ex.Message}"
                });
            }
        }

        [HttpGet("buildings")]
        public async Task<ActionResult<List<BuildingDto>>> GetBuildings()
        {
            try
            {
                var filePath = Path.Combine(_env.WebRootPath, "data", "access_table.json");

                if (!System.IO.File.Exists(filePath))
                {
                    return NotFound(new { message = "Файл access_table.json не знайдено" });
                }

                var jsonString = await System.IO.File.ReadAllTextAsync(filePath);
                var options = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                };

                var rawBuildings = JsonSerializer.Deserialize<List<JsonElement>>(jsonString, options);
                if (rawBuildings == null)
                {
                    return BadRequest(new { message = "Не вдалося прочитати JSON" });
                }

                var buildings = new List<BuildingDto>();

                foreach (var item in rawBuildings)
                {
                    var building = ParseBuilding(item);
                    buildings.Add(building);
                }

                return Ok(buildings);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = $"Помилка: {ex.Message}" });
            }
        }

        [HttpGet("requirements")]
        public async Task<ActionResult<MobilityRequirementsDto>> GetRequirements()
        {
            try
            {
                var filePath = Path.Combine(_env.WebRootPath, "data", "requirements.json");

                if (!System.IO.File.Exists(filePath))
                {
                    return NotFound(new { message = "Файл requirements.json не знайдено" });
                }

                var jsonString = await System.IO.File.ReadAllTextAsync(filePath);
                var options = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                };

                var requirements = JsonSerializer.Deserialize<MobilityRequirements>(jsonString, options);
                if (requirements == null)
                {
                    return BadRequest(new { message = "Не вдалося прочитати requirements.json" });
                }

                var dto = new MobilityRequirementsDto
                {
                    Columns = requirements.Columns,
                    Types = requirements.Rows.Select(row => new MobilityTypeDto
                    {
                        Type = row.Type,
                        Requirements = requirements.Columns
                            .Select((col, index) => new { col, required = row.Data[index] == 1 })
                            .ToDictionary(x => x.col, x => x.required),
                        TotalRequired = row.Data.Count(x => x == 1)
                    }).ToList()
                };

                return Ok(dto);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = $"Помилка: {ex.Message}" });
            }
        }

        private BuildingDto ParseBuilding(JsonElement element)
        {
            var facilities = new Dictionary<string, bool>
            {
                { "Безпороговий вхід", GetBoolValue(element, "Безпороговий вхід") },
                { "Пандус", GetBoolValue(element, "Пандус") },
                { "Підйомник", GetBoolValue(element, "Підйомник") },
                { "Аудіопоказчик", GetBoolValue(element, "Аудіопоказчик") },
                { "Тактильні інформаційні показчики", GetBoolValue(element, "Тактильні інформаційні показчики") },
                { "Позначення кольором", GetBoolValue(element, "Позначення кольором") },
                { "Світлові сигнали", GetBoolValue(element, "Світлові сигнали") },
                { "Інформаційне табло", GetBoolValue(element, "Інформаційне табло") },
                { "Ліфт", GetBoolValue(element, "Ліфт") },
                { "Сходові підйомники", GetBoolValue(element, "Сходові підйомники") },
                { "Ескалатор", GetBoolValue(element, "Ескалатор") },
                { "Внутрішній пандус", GetBoolValue(element, "Внутрішній пандус") },
                { "Вбиральня для людей з інвалідністю", GetBoolValue(element, "Вбиральня для людей з інвалідністю") },
                { "WiFi", GetBoolValue(element, "WiFi") },
                { "Пеленальний столик", GetBoolValue(element, "Пеленальний столик") },
                { "Дитяча кімната", GetBoolValue(element, "Дитяча кімната") },
                { "Переклад с жестової мови", GetBoolValue(element, "Переклад с жестової мови") },
                { "Супровід людини з інвалідністю", GetBoolValue(element, "Супровід людини з інвалідністю") }
            };

            int score = facilities.Count(f => f.Value);
            string color = GetColorByScore(score);

            return new BuildingDto
            {
                ObjectId = GetIntValue(element, "OBJECTID *"),
                Address = GetStringValue(element, "address"),
                FullName = GetStringValue(element, "Повна назва закладу"),
                WorkingHours = GetStringValue(element, "Режим роботи закладу"),
                District = GetStringValue(element, "Район"),
                Category = GetStringValue(element, "Категорія закладу"),
                PointX = GetDoubleValue(element, "point_x"),
                PointY = GetDoubleValue(element, "point_y"),
                AccessibilityScore = score,
                Color = color,
                Facilities = facilities
            };
        }

        private bool GetBoolValue(JsonElement element, string propertyName)
        {
            try
            {
                if (element.TryGetProperty(propertyName, out JsonElement prop))
                {
                    if (prop.ValueKind == JsonValueKind.String)
                    {
                        var value = prop.GetString();
                        return value?.Trim().Equals("Так", StringComparison.OrdinalIgnoreCase) ?? false;
                    }
                }
            }
            catch { }
            return false;
        }

        private string GetStringValue(JsonElement element, string propertyName)
        {
            try
            {
                if (element.TryGetProperty(propertyName, out JsonElement prop))
                {
                    if (prop.ValueKind == JsonValueKind.String)
                    {
                        return prop.GetString() ?? string.Empty;
                    }
                }
            }
            catch { }
            return string.Empty;
        }

        private int GetIntValue(JsonElement element, string propertyName)
        {
            try
            {
                if (element.TryGetProperty(propertyName, out JsonElement prop))
                {
                    if (prop.ValueKind == JsonValueKind.Number)
                    {
                        return prop.GetInt32();
                    }
                }
            }
            catch { }
            return 0;
        }

        private double GetDoubleValue(JsonElement element, string propertyName)
        {
            try
            {
                if (element.TryGetProperty(propertyName, out JsonElement prop))
                {
                    if (prop.ValueKind == JsonValueKind.Number)
                    {
                        return prop.GetDouble();
                    }
                }
            }
            catch { }
            return 0.0;
        }

        private string GetColorByScore(int score)
        {
            double percentage = score / 18.0;
            if (percentage == 0) return "#DC2626";
            if (percentage <= 0.25) return "#F97316";
            if (percentage <= 0.5) return "#EAB308";
            if (percentage <= 0.75) return "#84CC16";
            return "#22C55E";
        }

        [HttpGet("sidewalks")]
        public async Task<ActionResult<List<SidewalkDto>>> GetSidewalks([FromQuery] string? mobilityType = null)
        {
            try
            {
                var sidewalksPath = Path.Combine(_env.WebRootPath, "data", "sidewalks.json");
                var requirementsPath = Path.Combine(_env.WebRootPath, "data", "road_requirements.json");

                if (!System.IO.File.Exists(sidewalksPath))
                {
                    return NotFound(new { message = "Файл sidewalks.json не знайдено" });
                }

                var sidewalksJson = await System.IO.File.ReadAllTextAsync(sidewalksPath);
                var options = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                };

                // Парсимо як JsonDocument для більшої гнучкості
                using var document = JsonDocument.Parse(sidewalksJson);
                var root = document.RootElement;

                if (!root.TryGetProperty("sidewalks", out var sidewalksArray))
                {
                    return BadRequest(new { message = "Не знайдено масив 'sidewalks' в JSON" });
                }

                var sidewalks = new List<Sidewalk>();
                int skipped = 0;

                foreach (var item in sidewalksArray.EnumerateArray())
                {
                    try
                    {
                        var sidewalk = new Sidewalk
                        {
                            Id = item.TryGetProperty("id", out var id) ? id.GetInt64() : 0,
                            Name = item.TryGetProperty("name", out var name) && name.ValueKind != JsonValueKind.Null
                                ? name.GetString() : null,
                            Type = item.TryGetProperty("type", out var type) ? type.GetString() ?? "" : "",
                            Surface = item.TryGetProperty("surface", out var surface) ? surface.GetString() ?? "" : "",
                            Width_Source = item.TryGetProperty("width_source", out var ws) ? ws.GetString() ?? "" : "",
                            Wheelchair = item.TryGetProperty("wheelchair", out var wc) ? wc.GetString() ?? "" : "",
                            Lit = item.TryGetProperty("lit", out var lit) ? lit.GetString() ?? "" : "",
                            Tactile_Paving = item.TryGetProperty("tactile_paving", out var tp) ? tp.GetString() ?? "" : "",
                            Smoothness = item.TryGetProperty("smoothness", out var sm) ? sm.GetString() ?? "" : ""
                        };

                        // Парсимо width гнучко
                        if (item.TryGetProperty("width", out var widthElement))
                        {
                            if (widthElement.ValueKind == JsonValueKind.Number)
                            {
                                sidewalk.Width = widthElement.GetDouble();
                            }
                            else if (widthElement.ValueKind == JsonValueKind.String)
                            {
                                if (double.TryParse(widthElement.GetString(), out var widthValue))
                                {
                                    sidewalk.Width = widthValue;
                                }
                            }
                        }

                        // Парсимо coordinates
                        if (item.TryGetProperty("coordinates", out var coords) && coords.ValueKind == JsonValueKind.Array)
                        {
                            sidewalk.Coordinates = new List<List<double>>();
                            foreach (var coord in coords.EnumerateArray())
                            {
                                if (coord.ValueKind == JsonValueKind.Array && coord.GetArrayLength() == 2)
                                {
                                    var coordPair = new List<double>
                            {
                                coord[0].GetDouble(),
                                coord[1].GetDouble()
                            };
                                    sidewalk.Coordinates.Add(coordPair);
                                }
                            }
                        }

                        if (sidewalk.Coordinates != null && sidewalk.Coordinates.Count >= 2)
                        {
                            sidewalks.Add(sidewalk);
                        }
                        else
                        {
                            skipped++;
                        }
                    }
                    catch (Exception ex)
                    {
                        skipped++;
                        // Пропускаємо проблемний запис
                        Console.WriteLine($"Пропущено запис: {ex.Message}");
                    }
                }

                RoadAccessibilityCriteria? criteria = null;
                if (!string.IsNullOrEmpty(mobilityType) && mobilityType != "all" && System.IO.File.Exists(requirementsPath))
                {
                    var requirementsJson = await System.IO.File.ReadAllTextAsync(requirementsPath);
                    var requirements = JsonSerializer.Deserialize<RoadRequirements>(requirementsJson, options);
                    criteria = requirements?.Accessibility_Criteria.FirstOrDefault(c => c.Type == mobilityType);
                }
                var updatesPath = Path.Combine(_env.WebRootPath, "data", "updates.json");
                if (System.IO.File.Exists(updatesPath))
                {
                    var updatesJson = await System.IO.File.ReadAllTextAsync(updatesPath);
                    var updates = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(updatesJson);

                    if (updates != null)
                    {
                        for (int i = 0; i < sidewalks.Count; i++)
                        {
                            var id = sidewalks[i].Id.ToString();
                            if (!updates.TryGetValue(id, out var update)) continue;

                            var sw = sidewalks[i];

                            if (update.TryGetProperty("surface", out var s))
                                sw.Surface = s.GetString() ?? sw.Surface;
                            if (update.TryGetProperty("smoothness", out var sm))
                                sw.Smoothness = sm.GetString() ?? sw.Smoothness;
                            if (update.TryGetProperty("lit", out var l))
                                sw.Lit = l.GetString() ?? sw.Lit;
                            if (update.TryGetProperty("tactile_paving", out var tp))
                                sw.Tactile_Paving = tp.GetString() ?? sw.Tactile_Paving;
                            if (update.TryGetProperty("width", out var w) && w.ValueKind == JsonValueKind.Number)
                                sw.Width = w.GetDouble();

                            sidewalks[i] = sw;
                        }
                        Console.WriteLine($"Застосовано {updates.Count} оновлень з updates.json");
                    }
                }
                var result = sidewalks.Select(sidewalk => EvaluateSidewalk(sidewalk, criteria)).ToList();

                Console.WriteLine($"Завантажено доріг: {result.Count}, пропущено: {skipped}");

                return Ok(result);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = $"Помилка: {ex.Message}", stack = ex.StackTrace });
            }
        }

        [HttpGet("road-requirements")]
        public async Task<ActionResult<RoadRequirements>> GetRoadRequirements()
        {
            try
            {
                var filePath = Path.Combine(_env.WebRootPath, "data", "road_requirements.json");

                if (!System.IO.File.Exists(filePath))
                {
                    return NotFound(new { message = "Файл road_requirements.json не знайдено" });
                }

                var jsonString = await System.IO.File.ReadAllTextAsync(filePath);
                var options = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                };

                var requirements = JsonSerializer.Deserialize<RoadRequirements>(jsonString, options);
                return Ok(requirements);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = $"Помилка: {ex.Message}" });
            }
        }

        [HttpGet("graph/connectivity")]
        public async Task<ActionResult<object>> GetGraphConnectivity([FromQuery] bool includeIntersections = false)
        {
            try
            {
                var graph = await BuildGraphInternal(includeIntersections);
                var stats = _connectivityService.GetConnectivityStats(graph);
                return Ok(stats);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = $"Помилка: {ex.Message}" });
            }
        }

        [HttpGet("graph/connect")]
        public async Task<ActionResult<object>> ConnectGraph([FromQuery] bool includeIntersections = false)
        {
            try
            {
                var graph = await BuildGraphInternal(includeIntersections);

                var statsBefore = _connectivityService.GetConnectivityStats(graph);
                Console.WriteLine($"До з'єднання: {statsBefore}");

                var connectedGraph = _connectivityService.ConnectComponents(graph);

                var statsAfter = _connectivityService.GetConnectivityStats(connectedGraph);

                return Ok(new
                {
                    before = statsBefore,
                    after = statsAfter,
                    nodeCount = connectedGraph.NodeCount,
                    edgeCount = connectedGraph.EdgeCount,
                    message = "Граф успішно з'єднано"
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = $"Помилка: {ex.Message}" });
            }
        }


        [HttpGet("sidewalks-with-bridges")]
        public async Task<ActionResult<object>> GetSidewalksWithBridges(
    [FromQuery] string? mobilityType = null,
    [FromQuery] bool includeIntersections = false)
        {
            try
            {
                var sidewalksPath = Path.Combine(_env.WebRootPath, "data", "sidewalks.json");

                if (!System.IO.File.Exists(sidewalksPath))
                    return NotFound(new { message = "Файл sidewalks.json не знайдено" });

                var sidewalksJson = await System.IO.File.ReadAllTextAsync(sidewalksPath);

                using var document = JsonDocument.Parse(sidewalksJson);
                var root = document.RootElement;

                if (!root.TryGetProperty("sidewalks", out var sidewalksArray))
                    return BadRequest(new { message = "Не знайдено масив 'sidewalks' в JSON" });

                var sidewalks = new List<Sidewalk>();

                foreach (var item in sidewalksArray.EnumerateArray())
                {
                    try
                    {
                        var sidewalk = ParseSidewalkFromJson(item);
                        if (sidewalk.Coordinates != null && sidewalk.Coordinates.Count >= 2)
                            sidewalks.Add(sidewalk);
                    }
                    catch { }
                }

                // Будуємо граф
                RoadGraph graph;
                if (includeIntersections)
                    graph = _graphService.BuildGraphWithIntersections(sidewalks, _intersectionService);
                else
                    graph = _graphService.BuildGraph(sidewalks);

                // Збагачуємо даними з edges_classified.json
                var classifiedPath = Path.Combine(_env.WebRootPath, "data", "edges_classified.json");
                await _graphService.EnrichGraphWithClassified(graph, classifiedPath);

                // З'єднуємо компоненти
                graph = _connectivityService.ConnectComponents(graph);

                RoadAccessibilityCriteria? criteria = null;
                if (!string.IsNullOrEmpty(mobilityType) && mobilityType != "all")
                {
                    var requirementsPath = Path.Combine(_env.WebRootPath, "data", "road_requirements.json");
                    if (System.IO.File.Exists(requirementsPath))
                    {
                        var requirementsJson = await System.IO.File.ReadAllTextAsync(requirementsPath);
                        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                        var requirements = JsonSerializer.Deserialize<RoadRequirements>(requirementsJson, options);
                        criteria = requirements?.Accessibility_Criteria.FirstOrDefault(c => c.Type == mobilityType);
                    }
                }

                // Конвертуємо звичайні дороги
                var result = sidewalks.Select(sidewalk => EvaluateSidewalk(sidewalk, criteria)).ToList();

                // Додаємо ребра з edges_classified як звичайні дороги (з кольором за доступністю)
                var classifiedDtos = graph.Edges.Values
                    .Where(e => e.SidewalkId == -2)
                    .Select(edge => {
                        var node1 = graph.Nodes[edge.NodeId1];
                        var node2 = graph.Nodes[edge.NodeId2];

                        var sidewalk = new Sidewalk
                        {
                            Id = -2,
                            Name = "Класифіковане ребро",
                            Type = "classified",
                            Coordinates = new List<List<double>>
                            {
                        new List<double> { node1.Latitude, node1.Longitude },
                        new List<double> { node2.Latitude, node2.Longitude }
                            },
                            Surface = edge.Surface,
                            Width = edge.Width,
                            Wheelchair = edge.Wheelchair,
                            Lit = edge.Lit,
                            Tactile_Paving = edge.Tactile_Paving,
                            Smoothness = edge.Smoothness
                        };

                        return EvaluateSidewalk(sidewalk, criteria);
                    })
                    .ToList();

                result.AddRange(classifiedDtos);

                // Додаємо штучні ребра (bridges) — тільки SidewalkId == -1
                var bridges = graph.Edges.Values
                    .Where(e => e.SidewalkId == -1)
                    .Select(edge => {
                        var node1 = graph.Nodes[edge.NodeId1];
                        var node2 = graph.Nodes[edge.NodeId2];

                        return new SidewalkDto
                        {
                            Id = -1,
                            Name = "З'єднувальна дорога",
                            Type = "bridge",
                            Coordinates = new List<List<double>>
                            {
                        new List<double> { node1.Latitude, node1.Longitude },
                        new List<double> { node2.Latitude, node2.Longitude }
                            },
                            Surface = edge.Surface,
                            Width = edge.Width,
                            Wheelchair = edge.Wheelchair,
                            Lit = edge.Lit,
                            Tactile_Paving = edge.Tactile_Paving,
                            Smoothness = edge.Smoothness,
                            CriteriaScore = edge.AccessibilityScore,
                            TotalCriteria = 6,
                            Percentage = edge.AccessibilityPercentage,
                            Color = "#9CA3AF",
                            CriteriaStatus = new Dictionary<string, bool>
                            {
                        { "wheelchair", false },
                        { "lit", false },
                        { "tactile_paving", false },
                        { "surface", false },
                        { "smoothness", false },
                        { "width", false }
                            }
                        };
                    })
                    .ToList();

                Console.WriteLine($"Повертаємо {result.Count} доріг (включно з classified) + {bridges.Count} штучних ребер");

                return Ok(new
                {
                    roads = result,
                    bridges = bridges,
                    totalRoads = result.Count,
                    totalBridges = bridges.Count
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new
                {
                    message = $"Помилка: {ex.Message}",
                    stack = ex.StackTrace
                });
            }
        }

        [HttpPost("route")]
        public async Task<ActionResult<object>> BuildRoute([FromBody] RouteRequest request)
        {
            try
            {
                if (_cachedGraph == null)
                {
                    _cachedGraph = await BuildAndConnectGraph(request.IncludeIntersections);
                }

                // Завантажуємо дороги з оцінками якщо передано тип мобільності
                List<SidewalkDto>? sidewalks = null;
                if (!string.IsNullOrEmpty(request.MobilityType) && request.MobilityType != "all")
                {
                    var sidewalksResponse = await GetSidewalks(request.MobilityType);
                    if (sidewalksResponse.Value is List<SidewalkDto> list)
                        sidewalks = list;
                }

                var route = _routingService.FindRoute(
                    _cachedGraph,
                    request.StartLat, request.StartLng,
                    request.EndLat, request.EndLng,
                    sidewalks);

                if (route == null)
                    return NotFound(new { message = "Маршрут не знайдено" });

                return Ok(new
                {
                    path = route.Path.Select(n => new { lat = n.Latitude, lng = n.Longitude }).ToList(),
                    totalDistance = route.TotalDistance,
                    totalDistanceKm = route.TotalDistance / 1000.0,
                    startPoint = new { lat = route.StartPoint.Latitude, lng = route.StartPoint.Longitude },
                    endPoint = new { lat = route.EndPoint.Latitude, lng = route.EndPoint.Longitude },
                    nearestStartNode = new { lat = route.NearestStartNode.Latitude, lng = route.NearestStartNode.Longitude },
                    nearestEndNode = new { lat = route.NearestEndNode.Latitude, lng = route.NearestEndNode.Longitude },
                    nodeCount = route.Path.Count,
                    message = $"Маршрут знайдено: {route.TotalDistance / 1000.0:F2} км через {route.Path.Count} вершин"
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = $"Помилка: {ex.Message}", stack = ex.StackTrace });
            }
        }

        // Кешування та з'єднання графу
        private async Task<RoadGraph> BuildAndConnectGraph(bool includeIntersections)
        {
            var graph = await BuildGraphInternal(includeIntersections);
            var classifiedPath = Path.Combine(_env.WebRootPath, "data", "edges_classified.json");
            await _graphService.EnrichGraphWithClassified(graph, classifiedPath);
            graph = _connectivityService.ConnectComponents(graph);
            return graph;
        }

        // Очистити кеш графу
        [HttpPost("route/clear-cache")]
        public ActionResult ClearGraphCache()
        {
            _cachedGraph = null;
            return Ok(new { message = "Кеш графу очищено" });
        }

        public class RouteRequest
        {
            public double StartLat { get; set; }
            public double StartLng { get; set; }
            public double EndLat { get; set; }
            public double EndLng { get; set; }
            public bool IncludeIntersections { get; set; }
            public string? MobilityType { get; set; }  // <-- додати
        }

        // Допоміжний метод для побудови графу
        private async Task<RoadGraph> BuildGraphInternal(bool includeIntersections)
        {
            var sidewalksPath = Path.Combine(_env.WebRootPath, "data", "sidewalks.json");

            if (!System.IO.File.Exists(sidewalksPath))
            {
                throw new FileNotFoundException("Файл sidewalks.json не знайдено");
            }

            var sidewalksJson = await System.IO.File.ReadAllTextAsync(sidewalksPath);

            using var document = JsonDocument.Parse(sidewalksJson);
            var root = document.RootElement;

            if (!root.TryGetProperty("sidewalks", out var sidewalksArray))
            {
                throw new InvalidDataException("Не знайдено масив 'sidewalks' в JSON");
            }

            var sidewalks = new List<Sidewalk>();

            foreach (var item in sidewalksArray.EnumerateArray())
            {
                try
                {
                    var sidewalk = ParseSidewalkFromJson(item);
                    if (sidewalk.Coordinates != null && sidewalk.Coordinates.Count >= 2)
                    {
                        sidewalks.Add(sidewalk);
                    }
                }
                catch { }
            }

            RoadGraph graph;
            if (includeIntersections)
            {
                graph = _graphService.BuildGraphWithIntersections(sidewalks, _intersectionService);
            }
            else
            {
                graph = _graphService.BuildGraph(sidewalks);
            }

            return graph;
        }

        private SidewalkDto EvaluateSidewalk(Sidewalk sidewalk, RoadAccessibilityCriteria? criteria)
        {
            var dto = new SidewalkDto
            {
                Id = sidewalk.Id,
                Name = sidewalk.Name,
                Type = sidewalk.Type,
                Coordinates = sidewalk.Coordinates,
                Surface = sidewalk.Surface,
                Width = sidewalk.Width ?? 0.0,
                Wheelchair = sidewalk.Wheelchair,
                Lit = sidewalk.Lit,
                Tactile_Paving = sidewalk.Tactile_Paving,
                Smoothness = sidewalk.Smoothness
            };

            if (criteria == null)
            {
                // Режим "всі пристосування" - оцінюємо базові характеристики
                var criteriaStatus = new Dictionary<string, bool>();
                int score = 0;
                int total = 6;

                // Wheelchair
                bool wheelchairOk = sidewalk.Wheelchair == "yes";
                criteriaStatus["wheelchair"] = wheelchairOk;
                if (wheelchairOk) score++;

                // Lit
                bool litOk = sidewalk.Lit == "yes";
                criteriaStatus["lit"] = litOk;
                if (litOk) score++;

                // Tactile paving
                bool tactileOk = sidewalk.Tactile_Paving == "yes";
                criteriaStatus["tactile_paving"] = tactileOk;
                if (tactileOk) score++;

                // Surface - перевіряємо чи не unknown
                bool surfaceOk = !string.IsNullOrEmpty(sidewalk.Surface) &&
                                 sidewalk.Surface != "unknown";
                criteriaStatus["surface"] = surfaceOk;
                if (surfaceOk) score++;

                // Smoothness - перевіряємо чи не unknown
                bool smoothnessOk = !string.IsNullOrEmpty(sidewalk.Smoothness) &&
                                   sidewalk.Smoothness != "unknown";
                criteriaStatus["smoothness"] = smoothnessOk;
                if (smoothnessOk) score++;

                // Width - мінімум 1.5м
                bool widthOk = (sidewalk.Width ?? 0.0) >= 1.5;
                criteriaStatus["width"] = widthOk;
                if (widthOk) score++;

                dto.CriteriaScore = score;
                dto.TotalCriteria = total;
                dto.Percentage = total > 0 ? (double)score / total : 0;
                dto.Color = GetColorByPercentage(dto.Percentage);
                dto.CriteriaStatus = criteriaStatus;

                return dto;
            }

            // Режим конкретного типу мобільності
            var criteriaStatusSpecific = new Dictionary<string, bool>();
            int scoreSpecific = 0;
            int totalSpecific = 0;

            // Wheelchair
            if (criteria.Criteria.Wheelchair == "yes")
            {
                totalSpecific++;
                bool ok = sidewalk.Wheelchair == "yes";
                criteriaStatusSpecific["wheelchair"] = ok;
                if (ok) scoreSpecific++;
            }

            // Lit
            if (criteria.Criteria.Lit == "yes")
            {
                totalSpecific++;
                bool ok = sidewalk.Lit == "yes";
                criteriaStatusSpecific["lit"] = ok;
                if (ok) scoreSpecific++;
            }

            // Tactile paving
            if (criteria.Criteria.Tactile_Paving == "yes")
            {
                totalSpecific++;
                bool ok = sidewalk.Tactile_Paving == "yes";
                criteriaStatusSpecific["tactile_paving"] = ok;
                if (ok) scoreSpecific++;
            }

            // Surface
            if (criteria.Criteria.Surface != null && criteria.Criteria.Surface.Any())
            {
                totalSpecific++;
                bool ok = criteria.Criteria.Surface.Contains(sidewalk.Surface, StringComparer.OrdinalIgnoreCase);
                criteriaStatusSpecific["surface"] = ok;
                if (ok) scoreSpecific++;
            }

            // Smoothness
            if (criteria.Criteria.Smoothness != null && criteria.Criteria.Smoothness.Any())
            {
                totalSpecific++;
                bool ok = !string.IsNullOrEmpty(sidewalk.Smoothness) &&
                          !sidewalk.Smoothness.Equals("unknown", StringComparison.OrdinalIgnoreCase) &&
                          criteria.Criteria.Smoothness.Contains(sidewalk.Smoothness, StringComparer.OrdinalIgnoreCase);
                criteriaStatusSpecific["smoothness"] = ok;
                if (ok) scoreSpecific++;
            }

            // Width
            if (criteria.Criteria.Width != null && criteria.Criteria.Width.Comparator == "more")
            {
                totalSpecific++;
                bool ok = (sidewalk.Width ?? 0.0) >= criteria.Criteria.Width.Value;
                criteriaStatusSpecific["width"] = ok;
                if (ok) scoreSpecific++;
            }

            dto.CriteriaScore = scoreSpecific;
            dto.TotalCriteria = totalSpecific;
            dto.Percentage = totalSpecific > 0 ? (double)scoreSpecific / totalSpecific : 0;
            dto.Color = GetColorByPercentage(dto.Percentage);
            dto.CriteriaStatus = criteriaStatusSpecific;

            return dto;
        }

        private string GetColorByPercentage(double percentage)
        {
            if (percentage == 0) return "#DC2626";
            if (percentage < 0.33) return "#F97316";
            if (percentage < 0.66) return "#EAB308";
            if (percentage < 1.0) return "#84CC16";
            return "#22C55E";
        }

        [HttpGet("rerouted-bridges")]
        public async Task<ActionResult<object>> GetReroutedBridges([FromQuery] string? criteriaType = null)
        {
            try
            {
                //Console.WriteLine($"criteriaType: {criteriaType}, criteria null: {criteria == null}");
                Console.WriteLine($"criteriaType param: '{criteriaType}'");
                var filePath = Path.Combine(_env.WebRootPath, "data", "bridge_edges_rerouted.json");
                var classifiedPath = Path.Combine(_env.WebRootPath, "data", "edges_classified.json");

                if (!System.IO.File.Exists(filePath))
                    return NotFound(new { message = "Файл bridge_edges_rerouted.json не знайдено." });

                // Завантажуємо класифіковані дані якщо є
                Dictionary<string, JsonElement>? classified = null;
                if (System.IO.File.Exists(classifiedPath))
                {
                    var classifiedJson = await System.IO.File.ReadAllTextAsync(classifiedPath);
                    classified = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(classifiedJson);
                }

                // Завантажуємо критерії якщо передано тип
                RoadAccessibilityCriteria? criteria = null;

                if (!string.IsNullOrEmpty(criteriaType))
                {
                    var criteriaPath = Path.Combine(_env.WebRootPath, "data", "road_requirements.json");
                    Console.WriteLine($"criteriaPath exists: {System.IO.File.Exists(criteriaPath)}");
                    Console.WriteLine($"criteriaPath: {criteriaPath}");
                    if (System.IO.File.Exists(criteriaPath))
                    {
                        var criteriaJson = await System.IO.File.ReadAllTextAsync(criteriaPath);
                        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                        var rootElement = JsonSerializer.Deserialize<JsonElement>(criteriaJson, options);
                        var allCriteria = JsonSerializer.Deserialize<List<RoadAccessibilityCriteria>>(
                            rootElement.GetProperty("accessibility_criteria").GetRawText(), options);
                        criteria = allCriteria?.FirstOrDefault(c =>
                            c.Type.Equals(criteriaType, StringComparison.OrdinalIgnoreCase));
                        Console.WriteLine($"criteria знайдено: '{criteria?.Type}'");
                    }
                }

                var json = await System.IO.File.ReadAllTextAsync(filePath);
                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;

                if (!root.TryGetProperty("edges", out var edgesArray))
                    return BadRequest(new { message = "Не знайдено масив 'edges' в JSON" });

                var bridges = new List<SidewalkDto>();

                foreach (var edge in edgesArray.EnumerateArray())
                {
                    // Координати
                    var coordinates = new List<List<double>>();
                    if (edge.TryGetProperty("Waypoints", out var waypoints) &&
                        waypoints.ValueKind == JsonValueKind.Array &&
                        waypoints.GetArrayLength() >= 2)
                    {
                        foreach (var wp in waypoints.EnumerateArray())
                            coordinates.Add(new List<double>
                    {
                        wp.GetProperty("Lat").GetDouble(),
                        wp.GetProperty("Lon").GetDouble()
                    });
                    }
                    else
                    {
                        var n1 = edge.GetProperty("Node1");
                        var n2 = edge.GetProperty("Node2");
                        coordinates.Add(new List<double> { n1.GetProperty("Latitude").GetDouble(), n1.GetProperty("Longitude").GetDouble() });
                        coordinates.Add(new List<double> { n2.GetProperty("Latitude").GetDouble(), n2.GetProperty("Longitude").GetDouble() });
                    }

                    bool rerouted = edge.TryGetProperty("Rerouted", out var r) && r.GetBoolean();
                    bool failed = edge.TryGetProperty("RerouteFailed", out var f) && f.GetBoolean();
                    string method = edge.TryGetProperty("RerouteMethod", out var m) ? m.GetString() ?? "" : "";
                    string edgeId = edge.TryGetProperty("EdgeId", out var eid) ? eid.GetString() ?? "" : "";

                    // Шукаємо ребро в класифікованих даних
                    // EdgeId в JSON виглядає як "50.5017,30.4238|50.5018,30.4240"
                    // В edges_classified.json ключ виглядає як "50.5017_30.4238_50.5018_30.4240"
                    string classifiedKey = edgeId.Replace("|", "_").Replace(",", "_");
                    JsonElement edgeData = default;
                    bool foundInClassified = classified != null && classified.TryGetValue(classifiedKey, out edgeData);

                    Sidewalk sidewalk;
                    string edgeName;

                    if (foundInClassified)
                    {
                        // Читаємо класифіковані характеристики
                        string GetStr(string key) =>
                            edgeData.TryGetProperty(key, out var v) && v.ValueKind != JsonValueKind.Null
                                ? v.GetString() ?? "unknown"
                                : "unknown";

                        string surface = GetStr("surface_type");
                        // Нормалізуємо surface_type під формат EvaluateSidewalk
                        surface = surface switch
                        {
                            "asphalt" or "concrete" => "asphalt",
                            "paving_stones" or "sett" => "paving_stones",
                            "unpaved" => "gravel",
                            _ => surface
                        };

                        string smoothness = GetStr("smoothness");
                        // Нормалізуємо smoothness
                        smoothness = smoothness switch
                        {
                            "smooth" => "good",
                            "slight" => "intermediate",
                            "severe" => "bad",
                            _ => smoothness
                        };

                        sidewalk = new Sidewalk
                        {
                            Id = -1,
                            Name = rerouted ? $"Перемаршрутоване ребро ({method})" : "Пряме з'єднання",
                            Type = "bridge_rerouted",
                            Coordinates = coordinates,
                            Surface = surface,
                            Width = GetStr("width") == "wide" ? 2.0 : 1.0,
                            Wheelchair = "unknown",
                            Lit = GetStr("lit") == "lit" ? "yes" : "no",
                            Tactile_Paving = GetStr("tactile_paving") == "yes" ? "yes" : "no",
                            Smoothness = smoothness,
                        };
                        edgeName = sidewalk.Name!;
                    }
                    else
                    {
                        // Ребро не знайдено — всі критерії не виконані
                        sidewalk = new Sidewalk
                        {
                            Id = -1,
                            Name = rerouted ? $"Перемаршрутоване ребро ({method})" : "Пряме з'єднання",
                            Type = "bridge_rerouted",
                            Coordinates = coordinates,
                            Surface = "unknown",
                            Width = 0.0,
                            Wheelchair = "no",
                            Lit = "no",
                            Tactile_Paving = "no",
                            Smoothness = "unknown",
                        };
                        edgeName = sidewalk.Name!;
                    }

                    var dto = EvaluateSidewalk(sidewalk, criteria);
                    dto.Name = edgeName;
                    dto.Type = "bridge_rerouted";
                    dto.Coordinates = coordinates;

                    bridges.Add(dto);
                }

                // Статистика з metadata
                int totalEdges = 0, reroutedEdges = 0, clearEdges = 0, failedEdges = 0;
                if (root.TryGetProperty("metadata", out var meta))
                {
                    meta.TryGetProperty("totalEdges", out var te); totalEdges = te.ValueKind == JsonValueKind.Number ? te.GetInt32() : 0;
                    meta.TryGetProperty("reroutedEdges", out var re); reroutedEdges = re.ValueKind == JsonValueKind.Number ? re.GetInt32() : 0;
                    meta.TryGetProperty("clearEdges", out var ce); clearEdges = ce.ValueKind == JsonValueKind.Number ? ce.GetInt32() : 0;
                    meta.TryGetProperty("failedEdges", out var fe); failedEdges = fe.ValueKind == JsonValueKind.Number ? fe.GetInt32() : 0;
                }

                return Ok(new { bridges, totalEdges, reroutedEdges, clearEdges, failedEdges });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = $"Помилка: {ex.Message}", stack = ex.StackTrace });
            }
        }

        // ── Моделі для звітів ─────────────────────────────────────────
        public class PendingReport
        {
            public string Id { get; set; } = Guid.NewGuid().ToString();
            public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
            public string ImagePath { get; set; } = "";
            public double? Lat { get; set; }
            public double? Lon { get; set; }
            public double? RoadLat { get; set; }
            public double? RoadLon { get; set; }
            public string? NearestRoadId { get; set; }
            public string? NearestRoadName { get; set; }
            public string? RoadType { get; set; } // "sidewalk" або "edge"
            public Dictionary<string, object> MlResults { get; set; } = new();
            public string Status { get; set; } = "pending"; // pending / approved / rejected
        }

        // ── Прийом фото від користувача ──────────────────────────────
        [HttpPost("report")]
        public async Task<ActionResult<object>> SubmitReport(IFormFile photo)
        {
            var requestId = Guid.NewGuid().ToString().Substring(0, 8);
            var roadId = Request.Form["roadId"].ToString();
            var roadName = Request.Form["roadName"].ToString();
            double.TryParse(Request.Form["roadLat"], out var roadLat);
            double.TryParse(Request.Form["roadLon"], out var roadLon);
            bool hasManualRoad = !string.IsNullOrEmpty(roadId);
            Console.WriteLine($"\n[{requestId}] >>> Новий запит на звіт. Файл: {photo?.FileName}");

            try
            {
                if (photo == null || photo.Length == 0)
                {
                    Console.WriteLine($"[{requestId}] ⚠ Помилка: Фото не передано.");
                    return BadRequest(new { message = "Фото не передано" });
                }

                // 1. Створюємо шлях та зберігаємо фото
                var uploadsDir = Path.Combine(_env.WebRootPath, "uploads");
                Directory.CreateDirectory(uploadsDir);
                var fileName = $"{Guid.NewGuid()}{Path.GetExtension(photo.FileName)}";
                var filePath = Path.Combine(uploadsDir, fileName);

                using (var stream = new FileStream(filePath, FileMode.Create))
                {
                    await photo.CopyToAsync(stream);
                }
                Console.WriteLine($"[{requestId}] ✓ Фото збережено локально.");

                // 2. Надсилаємо на Python сервіс
                Console.WriteLine($"[{requestId}] ... Надсилання до ML-сервісу");
                using var httpClient = new HttpClient();
                httpClient.Timeout = TimeSpan.FromSeconds(60);

                using var form = new MultipartFormDataContent();

                // ВАЖЛИВО: Читаємо збережений файл для відправки
                var fileBytes = await System.IO.File.ReadAllBytesAsync(filePath);
                var fileContent = new ByteArrayContent(fileBytes);

                // ПРАВКА: Явно вказуємо Content-Type, щоб Python не видавав AttributeError
                fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");

                form.Add(fileContent, "file", fileName);
                var modelsParam = Request.Form["models"].ToString();
                if (!string.IsNullOrEmpty(modelsParam))
                    form.Add(new StringContent(modelsParam), "models");

                var mlServiceUrl = Environment.GetEnvironmentVariable("ML_SERVICE_URL") ?? "http://localhost:8000";
var mlResponse = await httpClient.PostAsync($"{mlServiceUrl}/classify", form);

                if (!mlResponse.IsSuccessStatusCode)
                {
                    var errorBody = await mlResponse.Content.ReadAsStringAsync();
                    Console.WriteLine($"[{requestId}] ✗ Помилка ML-сервісу: {mlResponse.StatusCode}. Деталі: {errorBody}");
                    return StatusCode(502, new { message = "ML сервіс повернув помилку", details = errorBody });
                }

                var mlJson = await mlResponse.Content.ReadAsStringAsync();
                var mlData = JsonSerializer.Deserialize<JsonElement>(mlJson);
                Console.WriteLine($"[{requestId}] ✓ Отримано дані від ML");

                // 3. Витягуємо GPS
                double? lat = null, lon = null;
                if (mlData.TryGetProperty("gps", out var gps) && gps.ValueKind != JsonValueKind.Null)
                {
                    lat = gps.GetProperty("lat").GetDouble();
                    lon = gps.GetProperty("lon").GetDouble();
                    Console.WriteLine($"[{requestId}] 📍 GPS: {lat}, {lon}");
                }

                // 4. Пошук найближчої дороги
                string? nearestId = null, nearestName = null, roadType = null;
                string? gpsWarning = null;

                if (hasManualRoad)
                {
                    // Дорога вказана вручну
                    nearestId = roadId;
                    nearestName = roadName;
                    roadType = "sidewalk";

                    // Перевіряємо GPS якщо є
                    if (lat.HasValue && lon.HasValue && roadLat != 0 && roadLon != 0)
                    {
                        double dist = Math.Sqrt(
                            Math.Pow((lat.Value - roadLat) * 111000, 2) +
                            Math.Pow((lon.Value - roadLon) * 111000 * Math.Cos(roadLat * Math.PI / 180), 2)
                        );
                        if (dist > 200)
                            gpsWarning = $"GPS координати фото ({lat:F4}, {lon:F4}) знаходяться далеко від обраної дороги ({dist:F0}м). Перевірте правильність.";
                    }
                    else if (!lat.HasValue)
                    {
                        gpsWarning = "Фото не містить GPS координат. Дорогу визначено вручну.";
                    }
                }
                else if (lat.HasValue && lon.HasValue)
                {
                    var (id, name, type) = await FindNearestRoad(lat.Value, lon.Value);
                    nearestId = id;
                    nearestName = name;
                    roadType = type;
                }

                // 5. Формуємо результати ML
                var mlResults = new Dictionary<string, object>();
                var requestedModels = Request.Form["models"].ToString()
                    .Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .ToHashSet();

                if (mlData.TryGetProperty("results", out var results))
                {
                    foreach (var prop in results.EnumerateObject())
                    {
                        // Фільтруємо — лише вибрані моделі
                        if (requestedModels.Count > 0 && !requestedModels.Contains(prop.Name))
                            continue;

                        if (prop.Value.TryGetProperty("class", out var cls))
                        {
                            mlResults[prop.Name] = new
                            {
                                @class = cls.GetString(),
                                confidence = prop.Value.GetProperty("confidence").GetDouble()
                            };
                        }
                    }
                }

                // 6. Зберігання в чергу
                var report = new PendingReport
                {
                    ImagePath = $"/uploads/{fileName}",
                    Lat = lat,
                    Lon = lon,
                    NearestRoadId = nearestId,
                    NearestRoadName = nearestName,
                    RoadType = roadType,
                    MlResults = mlResults,
                    RoadLat = roadLat != 0 ? roadLat : null,  // ← додай
                    RoadLon = roadLon != 0 ? roadLon : null,  // ← додай
                };

                await SaveReport(report);
                Console.WriteLine($"[{requestId}] ✓ Звіт збережено. ID: {report.Id}");

                return Ok(new
                {
                    message = "Звіт прийнято",
                    reportId = report.Id,
                    gps = lat.HasValue ? new { lat, lon } : null,
                    nearestRoad = nearestName,
                    gpsWarning,   // <- додати
                    mlResults
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{requestId}] !!! ПОМИЛКА: {ex.Message}");
                return StatusCode(500, new { message = ex.Message });
            }
        }

        // ── Допоміжні методи ─────────────────────────────────────────
        private async Task<(string? id, string? name, string? type)> FindNearestRoad(double lat, double lon)
        {
            // Шукаємо в sidewalks.json
            var sidewalksPath = Path.Combine(_env.WebRootPath, "data", "sidewalks.json");
            if (System.IO.File.Exists(sidewalksPath))
            {
                var json = await System.IO.File.ReadAllTextAsync(sidewalksPath);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("sidewalks", out var arr))
                {
                    string? bestId = null, bestName = null;
                    double minDist = double.MaxValue;

                    foreach (var item in arr.EnumerateArray())
                    {
                        if (!item.TryGetProperty("coordinates", out var coords)) continue;
                        foreach (var coord in coords.EnumerateArray())
                        {
                            if (coord.GetArrayLength() < 2) continue;
                            double dist = _graphService.CalculateDistance(
                                lat, lon, coord[0].GetDouble(), coord[1].GetDouble());
                            if (dist < minDist)
                            {
                                minDist = dist;
                                bestId = item.TryGetProperty("id", out var id) ? id.GetInt64().ToString() : null;
                                bestName = item.TryGetProperty("name", out var name) && name.ValueKind != JsonValueKind.Null
                                    ? name.GetString() : "Невідома дорога";
                            }
                        }
                        if (minDist < 50) break; // 50 метрів — достатньо близько
                    }

                    if (minDist < 200)
                        return (bestId, bestName, "sidewalk");
                }
            }

            // Шукаємо в edges_classified.json
            var edgesPath = Path.Combine(_env.WebRootPath, "data", "edges_classified.json");
            if (System.IO.File.Exists(edgesPath))
            {
                var json = await System.IO.File.ReadAllTextAsync(edgesPath);
                using var doc = JsonDocument.Parse(json);
                string? bestId = null;
                double minDist = double.MaxValue;

                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    var parts = prop.Name.Split('_');
                    if (parts.Length < 4) continue;
                    if (double.TryParse(parts[0] + "." + parts[1], out var eLat) &&
                        double.TryParse(parts[2] + "." + parts[3], out var eLon))
                    {
                        double dist = _graphService.CalculateDistance(lat, lon, eLat, eLon);
                        if (dist < minDist) { minDist = dist; bestId = prop.Name; }
                    }
                }

                if (minDist < 200)
                    return (bestId, bestId, "edge");
            }

            return (null, null, null);
        }

        private async Task SaveReport(PendingReport report)
        {
            var path = Path.Combine(_env.WebRootPath, "data", "pending_reports.json");
            List<PendingReport> reports = new();

            if (System.IO.File.Exists(path))
            {
                var json = await System.IO.File.ReadAllTextAsync(path);
                reports = JsonSerializer.Deserialize<List<PendingReport>>(json) ?? new();
            }

            reports.Add(report);
            await System.IO.File.WriteAllTextAsync(path,
                JsonSerializer.Serialize(reports, new JsonSerializerOptions { WriteIndented = true }));
        }

        // ── Адмін: отримати всі звіти ────────────────────────────────
        [HttpGet("admin/reports")]
        public async Task<ActionResult<object>> GetReports()
        {
            var path = Path.Combine(_env.WebRootPath, "data", "pending_reports.json");
            if (!System.IO.File.Exists(path))
                return Ok(new List<object>());

            var json = await System.IO.File.ReadAllTextAsync(path);
            var reports = JsonSerializer.Deserialize<List<PendingReport>>(json) ?? new();
            return Ok(reports.Where(r => r.Status == "pending").OrderByDescending(r => r.CreatedAt));
        }

        // ── Адмін: підтвердити / скасувати / редагувати ──────────────
        [HttpPost("reports/{id}/add-to-dataset")]
        public async Task<ActionResult> AddToDataset(string id, [FromBody] AddToDatasetRequest request)
        {
            try
            {
                var reportsPath = Path.Combine(_env.WebRootPath, "data", "pending_reports.json");
                if (!System.IO.File.Exists(reportsPath))
                    return NotFound(new { message = "Файл звітів не знайдено" });

                var json = await System.IO.File.ReadAllTextAsync(reportsPath);
                var reports = JsonSerializer.Deserialize<List<PendingReport>>(json) ?? new();
                var report = reports.FirstOrDefault(r => r.Id == id);

                if (report == null)
                    return NotFound(new { message = $"Звіт {id} не знайдено" });

                if (string.IsNullOrEmpty(report.ImagePath))
                    return BadRequest(new { message = "Фото не знайдено у звіті" });

                var fullImagePath = report.ImagePath.StartsWith("/")
                    ? Path.Combine(_env.WebRootPath, report.ImagePath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar))
                    : report.ImagePath;

                if (!System.IO.File.Exists(fullImagePath))
                    return NotFound(new { message = "Файл фото не знайдено на диску" });

                var datasetBase = Path.Combine(_env.WebRootPath, "data", "dataset");
                var copied = new List<string>();
                var errors = new List<string>();
                Console.WriteLine("Classifiers: " + string.Join(", ", request.Classifiers));
                Console.WriteLine("EditedValues keys: " + string.Join(", ", request.EditedValues.Keys));
                Console.WriteLine("Surface value: " + (request.EditedValues.TryGetValue("surface", out var sv) ? sv : "NOT FOUND"));
                foreach (var classifier in request.Classifiers)
                {
                    try
                    {
                        var subfolder = GetDatasetSubfolder(classifier, request.EditedValues);
                        if (subfolder == null)
                        {
                            errors.Add($"{classifier}: не вдалося визначити підпапку");
                            continue;
                        }

                        var targetDir = Path.Combine(datasetBase, GetClassifierFolder(classifier), subfolder);
                        Directory.CreateDirectory(targetDir);

                        var fileName = $"{id}_{Path.GetFileName(fullImagePath)}";
                        var targetPath = Path.Combine(targetDir, fileName);

                        System.IO.File.Copy(fullImagePath, targetPath, overwrite: true);
                        copied.Add($"{GetClassifierFolder(classifier)}/{subfolder}/{fileName}");
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"{classifier}: {ex.Message}");
                    }
                }

                return Ok(new { message = $"Додано до {copied.Count} класифікаторів", copied, errors });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = ex.Message });
            }
        }

        private string GetClassifierFolder(string classifier) => classifier switch
        {
            "lit" => "lit_classification",
            "smoothness" => "smooth_classification",
            "width" => "width_classification",
            "ramp" => "ramp_classification",
            "tactile_paving" => "tactile_classification",
            "surface_quality" => "quality_classification",
            "surface" => "surface_classification",
            _ => classifier
        };

        private string? GetDatasetSubfolder(string classifier, Dictionary<string, string> values)
        {
            if (!values.TryGetValue(classifier, out var val) || string.IsNullOrEmpty(val))
                return null;

            return classifier switch
            {
                "lit" => val == "yes" ? "lit" : "unlit",
                "tactile_paving" => val == "yes" ? "tactile" : "no_tactile",
                "ramp" => val == "yes" ? "ramp" : "no_ramp",
                "width" => double.TryParse(val,
                                        System.Globalization.NumberStyles.Any,
                                        System.Globalization.CultureInfo.InvariantCulture, out var w)
                                     ? (w >= 1.5 ? "wide" : "narrow") : null,
                "smoothness" => val is "slight" or "severe" or "smooth" ? val : null,
                "surface_quality" => val is "excellent" or "good" or "intermediate" or "bad" or "very_bad" ? val : null,
                "surface" => val is "asphalt" or "concrete" or "sett" or "paving_stones" ? val : null,
                _ => null
            };
        }

        public class AddToDatasetRequest
        {
            public List<string> Classifiers { get; set; } = new();
            public Dictionary<string, string> EditedValues { get; set; } = new();
        }


        [HttpPost("admin/reports/{id}/approve")]
        public async Task<ActionResult> ApproveReport(string id, [FromBody] JsonElement updatedData)
        {
            var path = Path.Combine(_env.WebRootPath, "data", "pending_reports.json");
            if (!System.IO.File.Exists(path))
                return NotFound();

            var json = await System.IO.File.ReadAllTextAsync(path);
            var reports = JsonSerializer.Deserialize<List<PendingReport>>(json) ?? new();
            var report = reports.FirstOrDefault(r => r.Id == id);
            if (report == null) return NotFound();

            report.Status = "approved";

            // Оновлюємо дані в відповідному файлі
            if (report.RoadType == "sidewalk" && report.NearestRoadId != null)
                await UpdateSidewalkData(report.NearestRoadId, updatedData);
            else if (report.RoadType == "edge" && report.NearestRoadId != null)
                await UpdateEdgeData(report.NearestRoadId, updatedData);

            await System.IO.File.WriteAllTextAsync(path,
                JsonSerializer.Serialize(reports, new JsonSerializerOptions { WriteIndented = true }));

            return Ok(new { message = "Звіт підтверджено, дані оновлено" });
        }

        [HttpPost("admin/reports/{id}/reject")]
        public async Task<ActionResult> RejectReport(string id)
        {
            var path = Path.Combine(_env.WebRootPath, "data", "pending_reports.json");
            if (!System.IO.File.Exists(path))
                return NotFound();

            var json = await System.IO.File.ReadAllTextAsync(path);
            var reports = JsonSerializer.Deserialize<List<PendingReport>>(json) ?? new();
            var report = reports.FirstOrDefault(r => r.Id == id);
            if (report == null) return NotFound();

            report.Status = "rejected";
            await System.IO.File.WriteAllTextAsync(path,
                JsonSerializer.Serialize(reports, new JsonSerializerOptions { WriteIndented = true }));

            return Ok(new { message = "Звіт відхилено" });
        }

        private async Task UpdateSidewalkData(string roadId, JsonElement data)
        {
            var updatesPath = Path.Combine(_env.WebRootPath, "data", "updates.json");
            Dictionary<string, JsonElement> updates = new();

            if (System.IO.File.Exists(updatesPath))
            {
                var json = await System.IO.File.ReadAllTextAsync(updatesPath);
                updates = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json) ?? new();
            }

            updates[roadId] = data;
            await System.IO.File.WriteAllTextAsync(updatesPath,
                JsonSerializer.Serialize(updates, new JsonSerializerOptions { WriteIndented = true }));
        }

        private async Task UpdateEdgeData(string edgeId, JsonElement data)
        {
            var edgesPath = Path.Combine(_env.WebRootPath, "data", "edges_classified.json");
            if (!System.IO.File.Exists(edgesPath)) return;

            var json = await System.IO.File.ReadAllTextAsync(edgesPath);
            var edges = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json) ?? new();
            edges[edgeId] = data;

            await System.IO.File.WriteAllTextAsync(edgesPath,
                JsonSerializer.Serialize(edges, new JsonSerializerOptions { WriteIndented = true }));
        }

    }
}


