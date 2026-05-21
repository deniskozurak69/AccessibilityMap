namespace KyivAccessibilityMap.Controllers
{
    using Microsoft.AspNetCore.Mvc;
    using System.Text;
    using System.Text.Json;

    [ApiController]
    [Route("api/gemini")]
    public class GeminiController : ControllerBase
    {
        private readonly IWebHostEnvironment _env;
        private readonly IConfiguration _config;
        private readonly HttpClient _http;

        public GeminiController(IWebHostEnvironment env, IConfiguration config, IHttpClientFactory httpFactory)
        {
            _env = env;
            _config = config;
            _http = httpFactory.CreateClient();
        }

        // ── Отримати типи мобільності ─────────────────────────────────────────
        [HttpGet("mobility-types")]
        public async Task<ActionResult<object>> GetMobilityTypes()
        {
            try
            {
                var requirementsPath = Path.Combine(_env.WebRootPath, "data", "requirements.json");
                if (!System.IO.File.Exists(requirementsPath))
                    return NotFound(new { message = "requirements.json не знайдено" });

                var json = await System.IO.File.ReadAllTextAsync(requirementsPath);
                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;

                var types = new List<string>();
                if (root.TryGetProperty("rows", out var rows))
                {
                    foreach (var row in rows.EnumerateArray())
                    {
                        if (row.TryGetProperty("type", out var t))
                            types.Add(t.GetString() ?? "");
                    }
                }

                return Ok(new { types });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = ex.Message });
            }
        }

        // ── Проксі до Gemini API ──────────────────────────────────────────────
        [HttpPost("chat")]
        public async Task<ActionResult<object>> Chat([FromBody] ChatRequest request)
        {
            try
            {
                var apiKey = _config["GeminiApiKey"];
                if (string.IsNullOrEmpty(apiKey))
                    return StatusCode(500, new { message = "GeminiApiKey не налаштовано" });

                var model = "gemini-2.5-flash";
                var url = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent?key={apiKey}";

                var body = new
                {
                    contents = request.Messages.Select(m => new
                    {
                        role = m.Role,
                        parts = new[] { new { text = m.Text } }
                    }).ToArray(),
                    systemInstruction = new
                    {
                        parts = new[] { new { text = BuildSystemPrompt(request.MobilityTypes) } }
                    },
                    generationConfig = new
                    {
                        temperature = 0.7,
                        maxOutputTokens = 1024
                    }
                };

                var jsonBody = JsonSerializer.Serialize(body);
                var content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

                var response = await _http.PostAsync(url, content);
                var responseJson = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                    return StatusCode((int)response.StatusCode, new { message = responseJson });

                using var doc = JsonDocument.Parse(responseJson);
                var text = doc.RootElement
                    .GetProperty("candidates")[0]
                    .GetProperty("content")
                    .GetProperty("parts")[0]
                    .GetProperty("text")
                    .GetString();

                // Спроба розпарсити JSON-команду з відповіді
                var command = TryParseCommand(text ?? "");

                return Ok(new { text, command });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = ex.Message });
            }
        }

        // ── Системний промпт ──────────────────────────────────────────────────
        private string BuildSystemPrompt(List<string> mobilityTypes)
        {
            var typesStr = string.Join(", ", mobilityTypes);

            return $@"Ти — AI-асистент карти доступності Києва. Спілкуєшся ЛИШЕ українською мовою.

Твоя мета — допомогти користувачу побудувати маршрут. Веди діалог покроково:

КРОК 1: Запитай що користувач хоче зробити. Запропонуй два варіанти:
  А) Написати локацію в Києві (наприклад: ""Золоті ворота"", ""Хрещатик"", ""вул. Шевченка 10"") — і побудувати маршрут до неї
  Б) Обрати тип об'єкту і знайти оптимальний на карті

КРОК 2: Запитай тип мобільності. Доступні типи: {typesStr}. Або ""Всі пристосування"" якщо не важливо.

КРОК 3А (якщо вибрав А): Попроси написати локацію. Коли отримаєш — визнач її координати в Києві (ти знаєш географію Києва). Поверни JSON-команду.

КРОК 3Б (якщо вибрав Б): Запитай що цікавить — найближчий маршрут чи найдоступніший об'єкт. Потім запитай тип об'єкту з варіантів: Громадська вбиральня, Лікарня, Укриття, Паркінг, Підземний паркінг. Поверни JSON-команду.

ВАЖЛИВО: Коли маєш всі дані — обов'язково постав JSON-команду в кінці відповіді у такому форматі (між тегами):
<CMD>{{""action"":""route"",""endLat"":50.4513,""endLng"":30.5136,""mobilityType"":""Wheelchair users"",""locationName"":""Золоті ворота""}}</CMD>
або
<CMD>{{""action"":""buildingType"",""type"":""Лікарня"",""mode"":""route"",""mobilityType"":""all""}}</CMD>

Де mode: ""route"" = найближчий маршрут, ""building"" = найдоступніший об'єкт.
Якщо mobilityType = ""Всі пристосування"" — встанови значення ""all"".

Будь дружнім, лаконічним. Не задавай кілька питань одночасно — по одному кроку за раз.";
        }

        // ── Парсинг команди з відповіді ───────────────────────────────────────
        private object? TryParseCommand(string text)
        {
            var start = text.IndexOf("<CMD>");
            var end = text.IndexOf("</CMD>");
            if (start < 0 || end < 0) return null;

            var json = text.Substring(start + 5, end - start - 5).Trim();
            try
            {
                using var doc = JsonDocument.Parse(json);
                return JsonSerializer.Deserialize<object>(json);
            }
            catch { return null; }
        }

        public class ChatRequest
        {
            public List<ChatMessage> Messages { get; set; } = new();
            public List<string> MobilityTypes { get; set; } = new();
        }

        public class ChatMessage
        {
            public string Role { get; set; } = "user";
            public string Text { get; set; } = "";
        }
    }
}
