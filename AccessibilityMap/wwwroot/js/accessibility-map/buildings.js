// buildings.js — логіка будівель

function calculateBuildingAccessibility(building, mobilityType) {
    if (mobilityType === 'all') {
        const total = Object.keys(building.facilities).length;
        const available = Object.values(building.facilities).filter(v => v).length;
        return {
            percentage: available / total,
            available: available,
            required: total
        };
    }

    const typeReqs = buildingRequirements.types.find(t => t.type === mobilityType);
    if (!typeReqs) return { percentage: 0, available: 0, required: 0 };

    let availableCount = 0;
    let requiredCount = 0;

    Object.entries(typeReqs.requirements).forEach(([facility, required]) => {
        if (required) {
            requiredCount++;
            if (building.facilities[facility]) {
                availableCount++;
            }
        }
    });

    return {
        percentage: requiredCount > 0 ? availableCount / requiredCount : 0,
        available: availableCount,
        required: requiredCount
    };
}

function updateBuildingColors() {
    buildings.forEach(building => {
        const accessibility = calculateBuildingAccessibility(building, currentMobilityType);
        building.currentScore = accessibility.available;
        building.currentRequired = accessibility.required;
        building.currentPercentage = accessibility.percentage;
        building.currentColor = getColorByPercentage(accessibility.percentage);
    });
}

function displayBuildings(buildingsToShow) {
    buildingMarkers.forEach(marker => map.removeLayer(marker));
    buildingMarkers = [];

    buildingsToShow.forEach(building => {
        const marker = L.circleMarker([building.pointY, building.pointX], {
            radius: 8,
            fillColor: building.currentColor,
            color: '#fff',
            weight: 2,
            opacity: 1,
            fillOpacity: 0.8
        }).addTo(map);

        marker.bindTooltip(building.fullName || building.address, {
            permanent: false,
            direction: 'top'
        });

        marker.on('click', () => showBuildingInfo(building));
        buildingMarkers.push(marker);
    });
}
