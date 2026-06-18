let routingMode = false;
let routePoints = [];
let routeMarkers = [];
let routePolyline = null;

function toggleRoutingMode() {
    routingMode = !routingMode;
    const btn = document.getElementById('routingBtn');

    if (routingMode) {
        clearRoute();
        btn.textContent = 'Скасувати маршрут';
        btn.classList.add('active');
        alert('Клацніть на карті щоб обрати точку старту, потім точку фінішу');
        map.on('click', handleMapClickForRoute);
    } else {
        btn.textContent = 'Побудувати маршрут';
        btn.classList.remove('active');
        clearRoute();
        map.off('click', handleMapClickForRoute);
    }
}

function handleMapClickForRoute(e) {
    if (routePoints.length < 2) {
        routePoints.push({ lat: e.latlng.lat, lng: e.latlng.lng });

        const label = routePoints.length === 1 ? 'S' : 'F';
        const color = routePoints.length === 1 ? '#10B981' : '#EF4444';

        const marker = L.marker([e.latlng.lat, e.latlng.lng], {
            icon: L.divIcon({
                className: 'route-marker',
                html: `<div style="background:${color};color:white;width:30px;height:30px;border-radius:50%;display:flex;align-items:center;justify-content:center;font-weight:bold;border:2px solid white;box-shadow:0 2px 4px rgba(0,0,0,0.3);">${label}</div>`,
                iconSize: [30, 30]
            })
        }).addTo(map);

        routeMarkers.push(marker);

        if (routePoints.length === 2) {
            buildRoute();
        }
    }
}

async function buildRoute() {
    try {
        showLoading('Побудова маршруту...');

        const response = await fetch('/api/accessibility/route', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({
                startLat: routePoints[0].lat,
                startLng: routePoints[0].lng,
                endLat: routePoints[1].lat,
                endLng: routePoints[1].lng,
                includeIntersections: document.getElementById('includeIntersections')?.checked || false,
                mobilityType: currentMobilityType
            })
        });

        const data = await response.json();
        console.log('Відповідь від сервера:', JSON.stringify(data));
        console.log('startPoint:', data.startPoint);
        console.log('endPoint:', data.endPoint);
        console.log('path:', data.path);
        console.log('totalDistanceKm:', data.totalDistanceKm);

        hideLoading();

        if (!response.ok) {
            alert('Помилка: ' + data.message);
            return;
        }

        displayRoute(data);
        alert(`Маршрут побудовано!\n\nВідстань: ${data.totalDistanceKm.toFixed(2)} км`);

        routingMode = false;
        document.getElementById('routingBtn').textContent = 'Побудувати маршрут';
        document.getElementById('routingBtn').classList.remove('active');
        map.off('click', handleMapClickForRoute);

    } catch (error) {
        console.error('Помилка побудови маршруту:', error);
        hideLoading();
        alert('Помилка побудови маршруту: ' + error.message);
    }
}

function displayRoute(routeData) {
    if (routePolyline) {
        map.removeLayer(routePolyline);
    }

    const coordinates = [
        [routeData.startPoint.lat, routeData.startPoint.lng],
        ...routeData.path.map(p => [p.lat, p.lng]),
        [routeData.endPoint.lat, routeData.endPoint.lng]
    ];

    routePolyline = L.polyline(coordinates, {
        color: '#1D4ED8',
        weight: 7,
        opacity: 1.0
    }).addTo(map);

    routePolyline.bindTooltip(`Маршрут: ${routeData.totalDistanceKm.toFixed(2)} км`, {
        permanent: false,
        direction: 'top'
    });

    map.fitBounds(routePolyline.getBounds(), { padding: [50, 50] });
}

function clearRoute() {
    routeMarkers.forEach(marker => map.removeLayer(marker));
    routeMarkers = [];

    if (routePolyline) {
        map.removeLayer(routePolyline);
        routePolyline = null;
    }

    routePoints = [];
}

// ── Допоміжна функція створення маркерів S/F ─────────────────────────────────
function addRouteMarkers(startLat, startLng, endLat, endLng) {
    clearRoute();

    const startMarker = L.marker([startLat, startLng], {
        icon: L.divIcon({
            className: 'route-marker',
            html: `<div style="background:#10B981;color:white;width:30px;height:30px;border-radius:50%;display:flex;align-items:center;justify-content:center;font-weight:bold;border:2px solid white;box-shadow:0 2px 4px rgba(0,0,0,0.3);">S</div>`,
            iconSize: [30, 30]
        })
    }).addTo(map);
    routeMarkers.push(startMarker);

    const endMarker = L.marker([endLat, endLng], {
        icon: L.divIcon({
            className: 'route-marker',
            html: `<div style="background:#EF4444;color:white;width:30px;height:30px;border-radius:50%;display:flex;align-items:center;justify-content:center;font-weight:bold;border:2px solid white;box-shadow:0 2px 4px rgba(0,0,0,0.3);">F</div>`,
            iconSize: [30, 30]
        })
    }).addTo(map);
    routeMarkers.push(endMarker);
}

// ── Пошук і маршрут до будівлі за назвою/адресою ─────────────────────────────
const KHRESCHATYK = { lat: 50.4501, lng: 30.5234 };
let buildingSearchResults = [];

function initBuildingSearch() {
    const input = document.getElementById('buildingSearchInput');
    const dropdown = document.getElementById('buildingSearchDropdown');
    input.addEventListener('input', () => {
        const query = input.value.trim().toLowerCase();
        if (query.length < 2) {
            dropdown.style.display = 'none';
            return;
        }
        buildingSearchResults = buildings.filter(b =>
            (b.fullName || '').toLowerCase().includes(query) ||
            (b.address || '').toLowerCase().includes(query)
        ).slice(0, 10);
        if (buildingSearchResults.length === 0) {
            dropdown.style.display = 'none';
            return;
        }
        dropdown.innerHTML = buildingSearchResults.map((b, i) => `
            <div class="building-option" onclick="selectBuilding(${i})">
                <div class="building-option-name">${b.fullName || 'Без назви'}</div>
                <div class="building-option-addr">${b.address}</div>
            </div>
        `).join('');
        dropdown.style.display = 'block';
    });
    document.addEventListener('click', e => {
        if (!e.target.closest('.building-search-wrapper')) {
            dropdown.style.display = 'none';
        }
    });

    // ── Мобільний пошук ──
    const mobileInput = document.getElementById('buildingSearchInputMobile');
    const mobileDropdown = document.getElementById('buildingSearchDropdownMobile');
    if (!mobileInput || !mobileDropdown) return;

    mobileInput.addEventListener('input', () => {
        const query = mobileInput.value.trim().toLowerCase();
        if (query.length < 2) {
            mobileDropdown.style.display = 'none';
            return;
        }
        buildingSearchResults = buildings.filter(b =>
            (b.fullName || '').toLowerCase().includes(query) ||
            (b.address || '').toLowerCase().includes(query)
        ).slice(0, 10);
        if (buildingSearchResults.length === 0) {
            mobileDropdown.style.display = 'none';
            return;
        }
        mobileDropdown.innerHTML = buildingSearchResults.map((b, i) => `
            <div class="building-option" onclick="selectBuilding(${i}); closeMobileFilters()">
                <div class="building-option-name">${b.fullName || 'Без назви'}</div>
                <div class="building-option-addr">${b.address}</div>
            </div>
        `).join('');
        mobileDropdown.style.display = 'block';
    });

    document.addEventListener('click', e => {
        if (!e.target.closest('#buildingSearchInputMobile') &&
            !e.target.closest('#buildingSearchDropdownMobile')) {
            mobileDropdown.style.display = 'none';
        }
    });
}

function selectBuilding(index) {
    const building = buildingSearchResults[index];
    if (!building) return;

    document.getElementById('buildingSearchInput').value =
        building.fullName || building.address;
    document.getElementById('buildingSearchDropdown').style.display = 'none';

    buildRouteToBuilding(building);
}

async function buildRouteToBuilding(building) {
    if (!building.pointX || !building.pointY) {
        alert('Координати будівлі не визначені');
        return;
    }

    addRouteMarkers(KHRESCHATYK.lat, KHRESCHATYK.lng, building.pointY, building.pointX);

    try {
        showLoading('Побудова маршруту до будівлі...');

        const response = await fetch('/api/accessibility/route', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({
                startLat: KHRESCHATYK.lat,
                startLng: KHRESCHATYK.lng,
                endLat: building.pointY,
                endLng: building.pointX,
                includeIntersections: document.getElementById('includeIntersections')?.checked || false,
                mobilityType: currentMobilityType
            })
        });

        const data = await response.json();
        hideLoading();

        if (!response.ok) {
            alert('Помилка: ' + data.message);
            return;
        }

        displayRoute(data);
        alert(`Маршрут до "${building.fullName || building.address}"\n\nВідстань: ${data.totalDistanceKm.toFixed(2)} км`);

    } catch (error) {
        hideLoading();
        alert('Помилка побудови маршруту: ' + error.message);
    }
}

// ── Пошук за типом об'єкту ───────────────────────────────────────────────────
function initBuildingTypeSearch() {
    const modeOptBuilding = document.getElementById('modeOptBuilding');
    const btn = document.getElementById('routeToBuildingTypeBtn');

    btn.addEventListener('click', () => {
        const type = document.getElementById('buildingTypeSelect').value;
        if (!type) { alert('Оберіть тип об\'єкту'); return; }
        const mode = modeOptBuilding.checked ? 'building' : 'route';
        findAndRouteToBuildingType(type, mode);
    });
}

async function findAndRouteToBuildingType(type, mode) {
    const candidates = buildings.filter(b =>
        (b.fullName || '').toLowerCase().includes(type.toLowerCase()) ||
        (b.category || '').toLowerCase().includes(type.toLowerCase())
    );

    if (candidates.length === 0) {
        alert(`Об'єктів типу "${type}" не знайдено`);
        return;
    }

    showLoading(`Пошук оптимального об'єкту (${candidates.length} кандидатів)...`);

    try {
        if (mode === 'building') {
            await findByBestBuilding(candidates, type);
        } else {
            await findByBestRoute(candidates, type);
        }
    } catch (e) {
        hideLoading();
        alert('Помилка: ' + e.message);
    }
}

async function findByBestRoute(candidates, type) {
    let bestBuilding = null;
    let bestRoute = null;
    let bestDistance = Infinity;

    for (const building of candidates) {
        if (!building.pointX || !building.pointY) continue;
        try {
            const res = await fetch('/api/accessibility/route', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({
                    startLat: KHRESCHATYK.lat,
                    startLng: KHRESCHATYK.lng,
                    endLat: building.pointY,
                    endLng: building.pointX,
                    includeIntersections: false,
                    mobilityType: currentMobilityType
                })
            });
            if (!res.ok) continue;
            const data = await res.json();
            if (data.totalDistanceKm < bestDistance) {
                bestDistance = data.totalDistanceKm;
                bestBuilding = building;
                bestRoute = data;
            }
        } catch { continue; }
    }

    hideLoading();

    if (!bestRoute) {
        alert('Не вдалося побудувати маршрут до жодного об\'єкту');
        return;
    }

    addRouteMarkers(KHRESCHATYK.lat, KHRESCHATYK.lng, bestBuilding.pointY, bestBuilding.pointX);
    displayRoute(bestRoute);
    alert(`Найближчий "${type}"\n\n🏢 ${bestBuilding.fullName || bestBuilding.address}\n📍 ${bestBuilding.address}\n📏 ${bestRoute.totalDistanceKm.toFixed(2)} км`);
}

async function findByBestBuilding(candidates, type) {
    const scored = candidates
        .filter(b => b.pointX && b.pointY)
        .map(b => ({ building: b, score: b.accessibilityScore ?? 0 }))
        .sort((a, b) => b.score - a.score);

    if (scored.length === 0) {
        hideLoading();
        alert('Координати об\'єктів не визначені');
        return;
    }

    const maxScore = scored[0].score;
    const topCandidates = scored.filter(s => s.score === maxScore);

    let bestBuilding = null;
    let bestRoute = null;
    let bestDistance = Infinity;

    if (topCandidates.length === 1) {
        bestBuilding = topCandidates[0].building;
        const res = await fetch('/api/accessibility/route', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({
                startLat: KHRESCHATYK.lat,
                startLng: KHRESCHATYK.lng,
                endLat: bestBuilding.pointY,
                endLng: bestBuilding.pointX,
                includeIntersections: false,
                mobilityType: currentMobilityType
            })
        });
        if (res.ok) bestRoute = await res.json();
    } else {
        for (const { building } of topCandidates) {
            try {
                const res = await fetch('/api/accessibility/route', {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({
                        startLat: KHRESCHATYK.lat,
                        startLng: KHRESCHATYK.lng,
                        endLat: building.pointY,
                        endLng: building.pointX,
                        includeIntersections: false,
                        mobilityType: currentMobilityType
                    })
                });
                if (!res.ok) continue;
                const data = await res.json();
                if (data.totalDistanceKm < bestDistance) {
                    bestDistance = data.totalDistanceKm;
                    bestBuilding = building;
                    bestRoute = data;
                }
            } catch { continue; }
        }
    }

    hideLoading();

    if (!bestRoute || !bestBuilding) {
        alert('Не вдалося побудувати маршрут');
        return;
    }

    addRouteMarkers(KHRESCHATYK.lat, KHRESCHATYK.lng, bestBuilding.pointY, bestBuilding.pointX);
    displayRoute(bestRoute);

    const scoreLabel = `${bestBuilding.accessibilityScore ?? 0}/18 пристосувань`;
    alert(`Найдоступніший "${type}"\n\n🏢 ${bestBuilding.fullName || bestBuilding.address}\n📍 ${bestBuilding.address}\n♿ ${scoreLabel}\n📏 ${bestRoute.totalDistanceKm.toFixed(2)} км`);
}