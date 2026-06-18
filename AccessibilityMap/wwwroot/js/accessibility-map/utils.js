// utils.js — допоміжні функції

function getColorByPercentage(percentage) {
    if (percentage === 0) return '#DC2626';
    if (percentage < 0.33) return '#F97316';
    if (percentage < 0.66) return '#EAB308';
    if (percentage < 1.0) return '#84CC16';
    return '#22C55E';
}

function showLoading(text = 'Завантаження даних...') {
    const el = document.getElementById('loading');
    el.textContent = text;
    el.style.display = 'block';
}

function hideLoading() {
    document.getElementById('loading').style.display = 'none';
}

function populateMobilityTypes() {
    const select = document.getElementById('mobilityType');
    const types = new Set();
    if (buildingRequirements && buildingRequirements.types) {
        buildingRequirements.types.forEach(type => types.add(type.type));
    }
    if (roadRequirements && roadRequirements.accessibility_criteria) {
        roadRequirements.accessibility_criteria.forEach(criteria => types.add(criteria.type));
    }
    Array.from(types).forEach(type => {
        const option = document.createElement('option');
        option.value = type;
        option.textContent = type;
        select.appendChild(option);
    });

    // Синхронізуємо мобільний селект
    const mobileDst = document.getElementById('mobilityTypeMobile');
    if (mobileDst) mobileDst.innerHTML = select.innerHTML;
}

function isRoadCriteriaRequired(key, roadCriteria) {
    if (!roadCriteria) return false;
    const c = roadCriteria.criteria;
    if (key === 'wheelchair') return c.wheelchair === 'yes';
    if (key === 'lit') return c.lit === 'yes';
    if (key === 'tactile_paving') return c.tactile_paving === 'yes';
    if (key === 'surface') return c.surface && c.surface.length > 0;
    if (key === 'smoothness') return c.smoothness && c.smoothness.length > 0;
    if (key === 'width') return c.width && c.width.comparator === 'more';
    return false;
}

function getRoadCriteriaForCurrentType() {
    return currentMobilityType !== 'all'
        ? roadRequirements.accessibility_criteria?.find(c => c.type === currentMobilityType)
        : null;
}
