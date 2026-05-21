// graph.js — логіка графу (вершини, з'єднання)

async function loadGraphNodes(includeIntersections = false) {
    try {
        showLoading('Завантаження вершин графу...');

        const response = await fetch(`/api/accessibility/graph/nodes?includeIntersections=${includeIntersections}`);
        const data = await response.json();
        graphNodes = data.nodes;

        hideLoading();
        displayGraphNodes();

        console.log(`Завантажено ${graphNodes.length} вершин графу`);
        console.log(`Перетини: ${data.intersectionNodes}, Звичайні: ${data.regularNodes}`);
    } catch (error) {
        console.error('Помилка завантаження вершин:', error);
        hideLoading();
    }
}

function displayGraphNodes() {
    nodeMarkers.forEach(marker => map.removeLayer(marker));
    nodeMarkers = [];

    graphNodes.forEach(node => {
        const color = node.isIntersection ? '#EF4444' : '#3B82F6';
        const label = node.isIntersection ? 'Перетин' : 'Вершина';

        const marker = L.circleMarker([node.lat, node.lng], {
            radius: node.isIntersection ? 4 : 3,
            fillColor: color,
            color: '#fff',
            weight: 1,
            opacity: 1,
            fillOpacity: 0.9
        }).addTo(map);

        marker.bindTooltip(`${label}: ${node.connections} зв'язків`, {
            permanent: false,
            direction: 'top'
        });

        nodeMarkers.push(marker);
    });
}

function hideGraphNodes() {
    nodeMarkers.forEach(marker => map.removeLayer(marker));
    nodeMarkers = [];
    graphNodes = [];
}

async function connectGraph() {
    try {
        showLoading("З'єднання компонент графу...");

        const includeIntersections = document.getElementById('includeIntersections')?.checked || false;

        const response = await fetch(
            `/api/accessibility/sidewalks-with-bridges?mobilityType=${currentMobilityType}&includeIntersections=${includeIntersections}`
        );
        const data = await response.json();

        bridgeRoads = data.bridges || [];
        showBridges = true;

        hideLoading();
        displayBridgeRoads(bridgeRoads);

        console.log(`З'єднано граф! Додано ${bridgeRoads.length} штучних ребер`);
        alert(`Граф з'єднано!\n\nДодано ${bridgeRoads.length} з'єднувальних доріг (сірі пунктирні лінії)`);

    } catch (error) {
        console.error("Помилка з'єднання графу:", error);
        hideLoading();
        alert("Помилка з'єднання графу: " + error.message);
    }
}
