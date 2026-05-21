using System.Text.Json;
using System.Text.Json.Serialization;

namespace KyivAccessibilityMap.Models
{
    public class SidewalksData
    {
        public SidewalksMetadata Metadata { get; set; } = new SidewalksMetadata();
        public List<Sidewalk> Sidewalks { get; set; } = new List<Sidewalk>();
    }

    public class SidewalksMetadata
    {
        public string Source { get; set; } = string.Empty;
        public string Api { get; set; } = string.Empty;
        public string Exported { get; set; } = string.Empty;
        public int Total_Segments { get; set; }
        public string Bounds { get; set; } = string.Empty;
    }

    public class Sidewalk
    {
        public long Id { get; set; }
        public string? Name { get; set; }
        public string Type { get; set; } = string.Empty;
        public List<List<double>> Coordinates { get; set; } = new List<List<double>>();
        public string Surface { get; set; } = string.Empty;

        [JsonConverter(typeof(FlexibleDoubleConverter))]
        public double? Width { get; set; }

        public string Width_Source { get; set; } = string.Empty;
        public string Wheelchair { get; set; } = string.Empty;
        public string Lit { get; set; } = string.Empty;
        public string Tactile_Paving { get; set; } = string.Empty;
        public string Smoothness { get; set; } = string.Empty;
    }

    // Custom converter для обробки різних типів width
    public class FlexibleDoubleConverter : JsonConverter<double?>
    {
        public override double? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.Number:
                    return reader.GetDouble();

                case JsonTokenType.String:
                    var stringValue = reader.GetString();
                    if (string.IsNullOrWhiteSpace(stringValue))
                        return null;
                    if (double.TryParse(stringValue, out double result))
                        return result;
                    return null;

                case JsonTokenType.Null:
                    return null;

                default:
                    return null;
            }
        }

        public override void Write(Utf8JsonWriter writer, double? value, JsonSerializerOptions options)
        {
            if (value.HasValue)
                writer.WriteNumberValue(value.Value);
            else
                writer.WriteNullValue();
        }
    }

    public class RoadRequirements
    {
        public List<RoadAccessibilityCriteria> Accessibility_Criteria { get; set; } = new List<RoadAccessibilityCriteria>();
    }

    public class RoadAccessibilityCriteria
    {
        public string Type { get; set; } = string.Empty;
        public RoadCriteria Criteria { get; set; } = new RoadCriteria();
    }

    public class RoadCriteria
    {
        public string Wheelchair { get; set; } = string.Empty;
        public string Lit { get; set; } = string.Empty;
        public string Tactile_Paving { get; set; } = string.Empty;
        public List<string> Surface { get; set; } = new List<string>();
        public List<string> Smoothness { get; set; } = new List<string>();
        public WidthCriteria Width { get; set; } = new WidthCriteria();
    }

    public class WidthCriteria
    {
        public double Value { get; set; }
        public string Comparator { get; set; } = string.Empty;
    }

    public class SidewalkDto
    {
        public long Id { get; set; }
        public string? Name { get; set; }
        public string Type { get; set; } = string.Empty;
        public List<List<double>> Coordinates { get; set; } = new List<List<double>>();
        public string Surface { get; set; } = string.Empty;
        public double Width { get; set; }
        public string Wheelchair { get; set; } = string.Empty;
        public string Lit { get; set; } = string.Empty;
        public string Tactile_Paving { get; set; } = string.Empty;
        public string Smoothness { get; set; } = string.Empty;
        public int CriteriaScore { get; set; }
        public int TotalCriteria { get; set; }
        public double Percentage { get; set; }
        public string Color { get; set; } = string.Empty;
        public Dictionary<string, bool> CriteriaStatus { get; set; } = new Dictionary<string, bool>();
    }
    public class GraphNode
    {
        public string Id { get; set; } = string.Empty; // "lat,lng" з точністю до 7 знаків
        public double Latitude { get; set; }
        public double Longitude { get; set; }
        public List<string> ConnectedNodeIds { get; set; } = new List<string>(); // ID сусідніх вершин
    }

    // Ребро графу - відрізок дороги між двома точками
    public class GraphEdge
    {
        public string Id { get; set; } = string.Empty; // "nodeId1-nodeId2"
        public string NodeId1 { get; set; } = string.Empty;
        public string NodeId2 { get; set; } = string.Empty;
        public long SidewalkId { get; set; } // ID дороги з якої це ребро
        public double Distance { get; set; } // Відстань в метрах
        public double Weight { get; set; } // Вага для алгоритму пошуку шляху

        // Характеристики ребра (від дороги)
        public string Surface { get; set; } = string.Empty;
        public double Width { get; set; }
        public string Wheelchair { get; set; } = string.Empty;
        public string Lit { get; set; } = string.Empty;
        public string Tactile_Paving { get; set; } = string.Empty;
        public string Smoothness { get; set; } = string.Empty;
        public int AccessibilityScore { get; set; }
        public double AccessibilityPercentage { get; set; }
    }

    // Весь граф
    public class RoadGraph
    {
        public Dictionary<string, GraphNode> Nodes { get; set; } = new Dictionary<string, GraphNode>();
        public Dictionary<string, GraphEdge> Edges { get; set; } = new Dictionary<string, GraphEdge>();

        public int NodeCount => Nodes.Count;
        public int EdgeCount => Edges.Count;
    }
}