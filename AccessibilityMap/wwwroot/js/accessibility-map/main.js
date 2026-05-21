// main.js — ініціалізація, завантаження даних, обробники подій

function initMap() {
    map = L.map('map').setView([50.4501, 30.5234], 12);
    map.createPane('routePane').style.zIndex = 650;
    L.tileLayer('https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png', {
        attribution: '© OpenStreetMap contributors',
        maxZoom: 19
    }).addTo(map);
}

async function loadData() {
    try {
        const [buildingsResponse, buildingRequirementsResponse, roadRequirementsResponse] = await Promise.all([
            fetch('/api/accessibility/buildings'),
            fetch('/api/accessibility/requirements'),
            fetch('/api/accessibility/road-requirements')
        ]);

        buildings = await buildingsResponse.json();
        buildingRequirements = await buildingRequirementsResponse.json();
        roadRequirements = await roadRequirementsResponse.json();

        populateMobilityTypes();
        hideLoading();
        await updateDisplay();
        if (currentLayer === 'roads' || currentLayer === 'both') {
            loadReroutedBridges();
        }
    } catch (error) {
        console.error('Помилка завантаження:', error);
        document.getElementById('loading').textContent = 'Помилка завантаження даних';
    }
}

initBuildingSearch();
initBuildingTypeSearch();

async function updateDisplay() {
    if (currentLayer === 'roads' || currentLayer === 'both') {
        await loadSidewalks();
    }

    updateBuildingColors();

    let filteredBuildings = buildings;
    let filteredRoads = Array.isArray(sidewalks) ? sidewalks : [];

    if (currentFilter === 'good') {
        filteredBuildings = buildings.filter(b => b.currentPercentage >= 0.75);
        filteredRoads = filteredRoads.filter(r => r.percentage >= 0.75);
    } else if (currentFilter === 'medium') {
        filteredBuildings = buildings.filter(b => b.currentPercentage >= 0.5 && b.currentPercentage < 0.75);
        filteredRoads = filteredRoads.filter(r => r.percentage >= 0.5 && r.percentage < 0.75);
    } else if (currentFilter === 'poor') {
        filteredBuildings = buildings.filter(b => b.currentPercentage < 0.5);
        filteredRoads = filteredRoads.filter(r => r.percentage < 0.5);
    }

    console.log('Фільтрація:', currentFilter, 'Будівель:', filteredBuildings.length, 'Доріг:', filteredRoads.length);

    if (currentLayer === 'buildings') {
        displayBuildings(filteredBuildings);
        roadPolylines.forEach(p => map.removeLayer(p));
        roadPolylines = [];
    } else if (currentLayer === 'roads') {
        displayRoads(filteredRoads);
        buildingMarkers.forEach(m => map.removeLayer(m));
        buildingMarkers = [];

        if (showBridges && bridgeRoads.length > 0) {
            displayBridgeRoads(bridgeRoads);
        }
    } else if (currentLayer === 'both') {
        displayBuildings(filteredBuildings);
        displayRoads(filteredRoads);

        if (showBridges && bridgeRoads.length > 0) {
            displayBridgeRoads(bridgeRoads);
        }
    }
}

function filterData(filter) {
    currentFilter = filter;

    document.querySelectorAll('.filter-btn').forEach(btn => {
        btn.classList.remove('active');
        if (btn.dataset.filter === filter) {
            btn.classList.add('active');
        }
    });

    updateDisplay();
}

async function changeMobilityType(type) {
    currentMobilityType = type;
    await updateDisplay();
}

function changeLayer(layer) {
    currentLayer = layer;

    if (layer === 'roads' || layer === 'both') {
        loadReroutedBridges();
    } else {
        // Прибираємо оновлені ребра при переключенні на будівлі
        reroutedBridgePolylines.forEach(p => map.removeLayer(p));
        reroutedBridgePolylines = [];
        showReroutedBridges = false;
    }

    updateDisplay();
}

// Обробники подій
document.querySelectorAll('.filter-btn').forEach(btn => {
    btn.addEventListener('click', () => filterData(btn.dataset.filter));
});

document.getElementById('mobilityType').addEventListener('change', (e) => {
    changeMobilityType(e.target.value);
});

document.getElementById('layerType').addEventListener('change', (e) => {
    changeLayer(e.target.value);
});

document.getElementById('showNodesBtn')?.addEventListener('click', () => {
    const includeIntersections = document.getElementById('includeIntersections')?.checked || false;
    loadGraphNodes(includeIntersections);
});

document.getElementById('hideNodesBtn')?.addEventListener('click', () => {
    hideGraphNodes();
});

document.getElementById('connectGraphBtn')?.addEventListener('click', () => {
    if (showBridges) {
        hideBridgeRoads();
        document.getElementById('connectGraphBtn').textContent = "З'єднати граф";
        showBridges = false;
    } else {
        connectGraph();
        document.getElementById('connectGraphBtn').textContent = "Сховати з'єднання";
    }
});

document.getElementById('routingBtn')?.addEventListener('click', () => {
    toggleRoutingMode();
});

document.getElementById('loadReroutedBtn')?.addEventListener('click', loadReroutedBridges);

// Старт
initMap();
loadData();
