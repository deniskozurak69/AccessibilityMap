namespace KyivAccessibilityMap.Models
{
    public class MobilityRequirements
    {
        public List<string> Columns { get; set; } = new List<string>();
        public List<MobilityType> Rows { get; set; } = new List<MobilityType>();
    }

    public class MobilityType
    {
        public string Type { get; set; } = string.Empty;
        public List<int> Data { get; set; } = new List<int>();
    }

    public class MobilityRequirementsDto
    {
        public List<string> Columns { get; set; } = new List<string>();
        public List<MobilityTypeDto> Types { get; set; } = new List<MobilityTypeDto>();
    }

    public class MobilityTypeDto
    {
        public string Type { get; set; } = string.Empty;
        public Dictionary<string, bool> Requirements { get; set; } = new Dictionary<string, bool>();
        public int TotalRequired { get; set; }
    }
}
