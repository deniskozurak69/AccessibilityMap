namespace KyivAccessibilityMap.Controllers
{
    using KyivAccessibilityMap.Models;
    using KyivAccessibilityMap.Services;
    using Microsoft.AspNetCore.Mvc;
    using System.Text.Json;
    using KyivAccessibilityMap.Services;

    [ApiController]
    [Route("api/accessibility/admin")]
    public class AdminRoadsController : ControllerBase
    {
        private readonly IWebHostEnvironment _env;
        //private readonly SidewalkParser _parser;

        private string SidewalksPath => Path.Combine(_env.WebRootPath, "data", "sidewalks.json");

        public AdminRoadsController(IWebHostEnvironment env)
        {
            _env = env;
            //_parser = parser;
        }

        // ── Завантажити всі сегменти для карти ───────────────────────────────
        [HttpGet("roads")]
        public async Task<ActionResult<object>> GetRoads([FromQuery] string? search = null)
        {
            try
            {
                var (root, sidewalks) = await LoadSidewalksRaw();

                IEnumerable<JsonElement> filtered = sidewalks;

                if (!string.IsNullOrEmpty(search))
                {
                    filtered = sidewalks.Where(s =>
                        s.TryGetProperty("name", out var n) &&
                        n.ValueKind == JsonValueKind.String &&
                        (n.GetString() ?? "").Contains(search, StringComparison.OrdinalIgnoreCase));
                }

                var result = filtered.Select(MapToDto).ToList();
                return Ok(result);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = ex.Message });
            }
        }

        // ── Оновити існуючий сегмент ──────────────────────────────────────────
        [HttpPut("roads/{id}")]
        public async Task<ActionResult> UpdateRoad(long id, [FromBody] RoadEditDto dto)
        {
            try
            {
                var jsonString = await System.IO.File.ReadAllTextAsync(SidewalksPath);
                using var document = JsonDocument.Parse(jsonString);

                var rootObj = document.RootElement;
                var sidewalksArray = rootObj.GetProperty("sidewalks").EnumerateArray().ToList();

                int index = sidewalksArray.FindIndex(s =>
                    s.TryGetProperty("id", out var idProp) && idProp.GetInt64() == id);

                if (index < 0)
                    return NotFound(new { message = $"Сегмент {id} не знайдено" });

                // Беремо поточний об'єкт і оновлюємо поля
                var updated = MergeRoadEdit(sidewalksArray[index], dto);
                sidewalksArray[index] = updated;

                await SaveSidewalks(rootObj, sidewalksArray);
                return Ok(new { message = "Сегмент оновлено", id });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = ex.Message });
            }
        }

        // ── Додати новий сегмент ─────────────────────────────────────────────
        [HttpPost("roads")]
        public async Task<ActionResult> AddRoad([FromBody] RoadCreateDto dto)
        {
            try
            {
                if (dto.Coordinates == null || dto.Coordinates.Count != 2)
                    return BadRequest(new { message = "Потрібно рівно 2 координати" });

                var jsonString = await System.IO.File.ReadAllTextAsync(SidewalksPath);
                using var document = JsonDocument.Parse(jsonString);

                var rootObj = document.RootElement;
                var sidewalksArray = rootObj.GetProperty("sidewalks").EnumerateArray().ToList();

                // Новий ID = максимальний + 1
                long newId = sidewalksArray
                    .Where(s => s.TryGetProperty("id", out _))
                    .Max(s => s.GetProperty("id").GetInt64()) + 1;

                var newSegment = BuildNewSegment(newId, dto);
                sidewalksArray.Add(newSegment);

                await SaveSidewalks(rootObj, sidewalksArray);
                return Ok(new { message = "Сегмент додано", id = newId });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = ex.Message });
            }
        }

        // ── Приватні методи ───────────────────────────────────────────────────

        private async Task<(JsonElement root, List<JsonElement> sidewalks)> LoadSidewalksRaw()
        {
            if (!System.IO.File.Exists(SidewalksPath))
                throw new FileNotFoundException("sidewalks.json не знайдено");

            var jsonString = await System.IO.File.ReadAllTextAsync(SidewalksPath);
            var document = JsonDocument.Parse(jsonString);
            var root = document.RootElement;

            if (!root.TryGetProperty("sidewalks", out var arr))
                throw new InvalidDataException("Масив 'sidewalks' не знайдено");

            return (root, arr.EnumerateArray().ToList());
        }

        private async Task SaveSidewalks(JsonElement originalRoot, List<JsonElement> sidewalks)
        {
            // Збираємо новий JSON зберігаючи metadata
            var options = new JsonWriterOptions { Indented = true };
            using var stream = new MemoryStream();
            using var writer = new Utf8JsonWriter(stream, options);

            writer.WriteStartObject();

            // Копіюємо metadata
            if (originalRoot.TryGetProperty("metadata", out var meta))
            {
                writer.WritePropertyName("metadata");
                // Оновлюємо total_segments
                writer.WriteStartObject();
                foreach (var prop in meta.EnumerateObject())
                {
                    if (prop.Name == "total_segments")
                        writer.WriteNumber("total_segments", sidewalks.Count);
                    else
                        prop.WriteTo(writer);
                }
                writer.WriteEndObject();
            }

            writer.WritePropertyName("sidewalks");
            writer.WriteStartArray();
            foreach (var s in sidewalks)
                s.WriteTo(writer);
            writer.WriteEndArray();

            writer.WriteEndObject();
            await writer.FlushAsync();

            var result = System.Text.Encoding.UTF8.GetString(stream.ToArray());
            await System.IO.File.WriteAllTextAsync(SidewalksPath, result);
        }

        private object MapToDto(JsonElement s)
        {
            string GetStr(string key) =>
                s.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
                    ? v.GetString() ?? "" : "";

            double GetNum(string key) =>
                s.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number
                    ? v.GetDouble() : 0;

            List<List<double>> coords = new();
            if (s.TryGetProperty("coordinates", out var c) && c.ValueKind == JsonValueKind.Array)
            {
                foreach (var coord in c.EnumerateArray())
                {
                    if (coord.ValueKind == JsonValueKind.Array && coord.GetArrayLength() == 2)
                        coords.Add(new List<double> { coord[0].GetDouble(), coord[1].GetDouble() });
                }
            }

            var lit = GetStr("lit");
            var wheelchair = GetStr("wheelchair");
            var tactile = GetStr("tactile_paving");
            var surface = GetStr("surface");
            var smoothness = GetStr("smoothness");
            var width = GetNum("width");

            // Підрахунок критеріїв (як на головній сторінці)
            int score = 0, total = 6;
            if (lit == "yes") score++;
            if (wheelchair == "yes") score++;
            if (tactile == "yes") score++;
            if (!string.IsNullOrEmpty(surface) && surface != "unknown") score++;
            if (!string.IsNullOrEmpty(smoothness) && smoothness != "unknown") score++;
            if (width >= 1.5) score++;

            double percentage = total > 0 ? (double)score / total : 0;

            string color = percentage == 0 ? "#DC2626" :
                           percentage < 0.33 ? "#F97316" :
                           percentage < 0.66 ? "#EAB308" :
                           percentage < 1.0 ? "#84CC16" : "#22C55E";

            return new
            {
                id = s.TryGetProperty("id", out var id) ? id.GetInt64() : 0,
                name = GetStr("name"),
                type = GetStr("type"),
                surface,
                smoothness,
                width,
                wheelchair,
                lit,
                tactile_paving = tactile,
                coordinates = coords,
                percentage,
                color
            };
        }

        private JsonElement MergeRoadEdit(JsonElement original, RoadEditDto dto)
        {
            var options = new JsonWriterOptions { Indented = false };
            using var stream = new MemoryStream();
            using var writer = new Utf8JsonWriter(stream, options);

            writer.WriteStartObject();
            foreach (var prop in original.EnumerateObject())
            {
                switch (prop.Name)
                {
                    case "name"          when dto.Name         != null: writer.WriteString("name", dto.Name); break;
                    case "type"          when dto.Type         != null: writer.WriteString("type", dto.Type); break;
                    case "surface"       when dto.Surface      != null: writer.WriteString("surface", dto.Surface); break;
                    case "smoothness"    when dto.Smoothness   != null: writer.WriteString("smoothness", dto.Smoothness); break;
                    case "wheelchair"    when dto.Wheelchair   != null: writer.WriteString("wheelchair", dto.Wheelchair); break;
                    case "lit"           when dto.Lit          != null: writer.WriteString("lit", dto.Lit); break;
                    case "tactile_paving"when dto.TactilePaving!= null: writer.WriteString("tactile_paving", dto.TactilePaving); break;
                    case "width"         when dto.Width        != null: writer.WriteNumber("width", dto.Width.Value); break;
                    default: prop.WriteTo(writer); break;
                }
            }
            writer.WriteEndObject();
            writer.Flush();

            var json = System.Text.Encoding.UTF8.GetString(stream.ToArray());
            return JsonDocument.Parse(json).RootElement;
        }

        private JsonElement BuildNewSegment(long id, RoadCreateDto dto)
        {
            var options = new JsonWriterOptions { Indented = false };
            using var stream = new MemoryStream();
            using var writer = new Utf8JsonWriter(stream, options);

            writer.WriteStartObject();
            writer.WriteNumber("id", id);
            writer.WriteString("name", dto.Name ?? "");
            writer.WriteString("type", dto.Type ?? "footway");
            writer.WriteStartArray("coordinates");
            foreach (var coord in dto.Coordinates!)
            {
                writer.WriteStartArray();
                writer.WriteNumberValue(coord[0]);
                writer.WriteNumberValue(coord[1]);
                writer.WriteEndArray();
            }
            writer.WriteEndArray();
            writer.WriteString("surface", dto.Surface ?? "unknown");
            writer.WriteNumber("width", dto.Width ?? 0);
            writer.WriteString("width_source", "manual");
            writer.WriteString("wheelchair", dto.Wheelchair ?? "no");
            writer.WriteString("lit", dto.Lit ?? "no");
            writer.WriteString("tactile_paving", dto.TactilePaving ?? "no");
            writer.WriteString("smoothness", dto.Smoothness ?? "unknown");
            writer.WriteNumber("road_id", dto.RoadId ?? 0);
            writer.WriteEndObject();
            writer.Flush();

            var json = System.Text.Encoding.UTF8.GetString(stream.ToArray());
            return JsonDocument.Parse(json).RootElement;
        }

        [HttpDelete("roads/{id}")]
        public async Task<ActionResult> DeleteRoad(long id)
        {
            try
            {
                var jsonString = await System.IO.File.ReadAllTextAsync(SidewalksPath);
                using var document = JsonDocument.Parse(jsonString);

                var rootObj = document.RootElement;
                var sidewalksArray = rootObj.GetProperty("sidewalks").EnumerateArray().ToList();

                int index = sidewalksArray.FindIndex(s =>
                    s.TryGetProperty("id", out var idProp) && idProp.GetInt64() == id);

                if (index < 0)
                    return NotFound(new { message = $"Сегмент {id} не знайдено" });

                sidewalksArray.RemoveAt(index);
                await SaveSidewalks(rootObj, sidewalksArray);
                return Ok(new { message = "Сегмент видалено", id });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = ex.Message });
            }
        }

        // ── DTO класи ─────────────────────────────────────────────────────────
        public class RoadEditDto
        {
            public string? Name { get; set; }
            public string? Type { get; set; }
            public string? Surface { get; set; }
            public string? Smoothness { get; set; }
            public string? Wheelchair { get; set; }
            public string? Lit { get; set; }
            public string? TactilePaving { get; set; }
            public double? Width { get; set; }
        }

        public class RoadCreateDto
        {
            public string? Name { get; set; }
            public string? Type { get; set; }
            public List<List<double>>? Coordinates { get; set; }
            public string? Surface { get; set; }
            public double? Width { get; set; }
            public string? Wheelchair { get; set; }
            public string? Lit { get; set; }
            public string? TactilePaving { get; set; }
            public string? Smoothness { get; set; }
            public long? RoadId { get; set; }
        }
    }
}
