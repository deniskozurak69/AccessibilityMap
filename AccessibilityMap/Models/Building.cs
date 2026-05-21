namespace KyivAccessibilityMap.Models
{
    public class Building
    {
        public int ObjectId { get; set; }
        public string Address { get; set; } = string.Empty;
        public string GlobalId { get; set; } = string.Empty;
        public string FullName { get; set; } = string.Empty;
        public string? WorkingHours { get; set; }
        public string ThresholdFreeEntrance { get; set; } = string.Empty;
        public string Ramp { get; set; } = string.Empty;
        public string Lift { get; set; } = string.Empty;
        public string AudioIndicator { get; set; } = string.Empty;
        public string TactileIndicators { get; set; } = string.Empty;
        public string ColorMarking { get; set; } = string.Empty;
        public string LightSignals { get; set; } = string.Empty;
        public string InfoBoard { get; set; } = string.Empty;
        public string Elevator { get; set; } = string.Empty;
        public string StairLifts { get; set; } = string.Empty;
        public string Escalator { get; set; } = string.Empty;
        public string InternalRamp { get; set; } = string.Empty;
        public string AccessibleToilet { get; set; } = string.Empty;
        public string WiFi { get; set; } = string.Empty;
        public string ChangingTable { get; set; } = string.Empty;
        public string ChildrensRoom { get; set; } = string.Empty;
        public string SignLanguageTranslation { get; set; } = string.Empty;
        public string DisabilityAssistance { get; set; } = string.Empty;
        public string District { get; set; } = string.Empty;
        public string StreetType { get; set; } = string.Empty;
        public string StreetName { get; set; } = string.Empty;
        public string BuildingNumber { get; set; } = string.Empty;
        public string? CorpusNumber { get; set; }
        public string? ContactPhone { get; set; }
        public string? Website { get; set; }
        public string DisabilityParking { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public double PointX { get; set; }
        public double PointY { get; set; }
        public string? ParkingAccessibility { get; set; }
        public string? BarrierFreeEntrance { get; set; }
        public string? TactileAcousticMeans { get; set; }
        public string? VisualMeans { get; set; }
        public string? InterfloorAccessibility { get; set; }
        public string? Escort { get; set; }
        public string? Amenities { get; set; }

        public int AccessibilityScore { get; set; }
        public string Color { get; set; } = string.Empty;
    }

    public class BuildingDto
    {
        public int ObjectId { get; set; }
        public string Address { get; set; } = string.Empty;
        public string FullName { get; set; } = string.Empty;
        public string? WorkingHours { get; set; }
        public string District { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public double PointX { get; set; }
        public double PointY { get; set; }
        public int AccessibilityScore { get; set; }
        public string Color { get; set; } = string.Empty;
        public Dictionary<string, bool> Facilities { get; set; } = new Dictionary<string, bool>();
    }
}