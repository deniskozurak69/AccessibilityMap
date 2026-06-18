// roads.js — логіка доріг та тротуарів

async function loadSidewalks() {
    try {
        const response = await fetch(`/api/accessibility/sidewalks?mobilityType=${currentMobilityType}`);
        const data = await response.json();
        sidewalks = Array.isArray(data) ? data : [];
        console.log('Завантажено доріг:', sidewalks.length);
    } catch (error) {
        console.error('Помилка завантаження доріг:', error);
        sidewalks = [];
    }
}

function displayRoads(roadsToShow) {
    roadPolylines.forEach(polyline => map.removeLayer(polyline));
    roadPolylines = [];

    if (!Array.isArray(roadsToShow)) {
        console.error('roadsToShow не є масивом:', roadsToShow);
        return;
    }

    console.log('Відображення доріг:', roadsToShow.length);

    roadsToShow.forEach(road => {
        if (!road.coordinates || !Array.isArray(road.coordinates) || road.coordinates.length < 2) {
            console.warn('Пропущено дорогу з некоректними координатами:', road.id);
            return;
        }

        const latLngs = road.coordinates.map(coord => [coord[0], coord[1]]);

        const polyline = L.polyline(latLngs, {
            color: road.color,
            weight: 4,
            opacity: 0.7
        }).addTo(map);

        polyline.bindTooltip(road.name || `ID: ${road.id}`, {
            permanent: false,
            direction: 'top'
        });

        polyline.on('click', () => {
            if (routingMode) return;
            showRoadInfo(road);
        });
    });
}

function displayBridgeRoads(bridges) {
    bridges.forEach(bridge => {
        if (!bridge.coordinates || bridge.coordinates.length < 2) return;

        const latLngs = bridge.coordinates.map(coord => [coord[0], coord[1]]);

        const polyline = L.polyline(latLngs, {
            color: '#9CA3AF',
            weight: 4,
            opacity: 0.7,
            dashArray: '5, 5'
        }).addTo(map);

        polyline.bindTooltip("З'єднувальна дорога (штучне ребро)", {
            permanent: false,
            direction: 'top'
        });

        polyline.on('click', () => {
            if (routingMode) return;
            showBridgeInfo(bridge);
        });
    });
}

function hideBridgeRoads() {
    roadPolylines = roadPolylines.filter(polyline => {
        const isBridge = polyline.options.dashArray === '5, 5';
        if (isBridge) {
            map.removeLayer(polyline);
            return false;
        }
        return true;
    });
    bridgeRoads = [];
    showBridges = false;
}

async function loadReroutedBridges() {
    if (showReroutedBridges) return;

    try {
        //showLoading('Завантаження оновлених ребер...');
        const response = await fetch(`/api/accessibility/rerouted-bridges?criteriaType=${currentMobilityType}`);
        if (!response.ok) {
            const err = await response.json();
            alert('Помилка: ' + err.message);
            return;
        }
        const data = await response.json();
        console.log('mobilityType:', currentMobilityType);
        console.log(data.bridges[0].criteriaStatus);
        console.log(data.bridges[0].criteriaScore);
        console.log(data.bridges[0].totalCriteria);

        data.bridges.forEach(bridge => {
            if (!bridge.coordinates || bridge.coordinates.length < 2) return;
            const latLngs = bridge.coordinates.map(c => [c[0], c[1]]);
            const polyline = L.polyline(latLngs, {
                color: bridge.color,
                weight: bridge.color === '#22C55E' ? 3 : 2,
                opacity: 0.85,
                dashArray: bridge.color === '#9CA3AF' ? '4, 4' : null
            }).addTo(map);
            polyline.bindTooltip(bridge.name, { permanent: false, direction: 'top' });
            polyline.on('click', () => {
                if (routingMode) return;
                showBridgeInfo(bridge);
            });
            reroutedBridgePolylines.push(polyline);
        });

        showReroutedBridges = true;
    } catch (error) {
        alert('Помилка: ' + error.message);
    } finally {
        hideLoading();
    }
}
