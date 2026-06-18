// info-panel.js — бічна панель з інформацією

const criteriaLabels = {
    wheelchair: 'Доступність для інвалідних візків',
    lit: 'Освітлення',
    tactile_paving: 'Тактильна плитка',
    surface: 'Покриття',
    smoothness: 'Гладкість',
    width: 'Ширина'
};

function closeInfoPanel() {
    document.getElementById('infoPanel').classList.remove('active');
}

function showBuildingInfo(building) {
    const panel = document.getElementById('infoPanel');
    panel.classList.add('active');

    const typeReqs = currentMobilityType !== 'all'
        ? buildingRequirements.types.find(t => t.type === currentMobilityType)
        : null;

    const facilitiesList = Object.entries(building.facilities)
        .map(([name, available]) => {
            const isRequired = typeReqs && typeReqs.requirements[name];
            const className = available ? 'criteria-met' : 'criteria-not-met';
            return `
                <div class="criteria-item ${className}">
                    <span>${name}${isRequired ? ' ⭐' : ''}</span>
                    <span>${available ? '✓' : '✗'}</span>
                </div>
            `;
        }).join('');

    const scoreLabel = currentMobilityType !== 'all'
        ? `Доступність для: ${currentMobilityType}`
        : 'Загальна доступність';

    panel.innerHTML = `
        <div class="info-header">
            <div>
                <h3>🏢 ${building.fullName || 'Без назви'}</h3>
                <p>${building.address}</p>
            </div>
            <button class="close-btn" onclick="closeInfoPanel()">×</button>
        </div>
        <div class="info-content">
            <div class="score-box" style="background-color: ${building.currentColor}20;">
                <div class="score-header">
                    <span class="score-label">${scoreLabel}</span>
                    <span class="score-value" style="color: ${building.currentColor};">
                        ${building.currentScore}/${building.currentRequired}
                    </span>
                </div>
                <div class="progress-bar">
                    <div class="progress-fill" style="width: ${building.currentPercentage * 100}%; background-color: ${building.currentColor};"></div>
                </div>
                <div style="text-align: center; margin-top: 0.5rem; font-size: 0.875rem; color: #6b7280;">
                    ${Math.round(building.currentPercentage * 100)}% задоволено
                </div>
            </div>

            ${building.district ? `
                <div class="info-item">
                    <span class="info-item-label">Район:</span>
                    <span class="info-item-value">${building.district}</span>
                </div>
            ` : ''}

            ${building.category ? `
                <div class="info-item">
                    <span class="info-item-label">Категорія:</span>
                    <span class="info-item-value">${building.category}</span>
                </div>
            ` : ''}

            <div style="margin-top: 1rem;">
                <div class="criteria-title">
                    📋 Пристосування ${typeReqs ? '(⭐ = необхідне)' : ''}
                </div>
                ${facilitiesList}
            </div>
        </div>
    `;
}

function showRoadInfo(road) {
    const panel = document.getElementById('infoPanel');
    panel.classList.add('active');

    const roadCriteria = getRoadCriteriaForCurrentType();

    const criteriaList = Object.entries(road.criteriaStatus || {})
        .map(([key, met]) => {
            const label = criteriaLabels[key] || key;
            const required = isRoadCriteriaRequired(key, roadCriteria);
            return `
                <div class="criteria-item ${met ? 'criteria-met' : 'criteria-not-met'}">
                    <span>${label}${required ? ' ⭐' : ''}</span>
                    <span>${met ? '✓' : '✗'}</span>
                </div>
            `;
        }).join('');

    const scoreLabel = currentMobilityType !== 'all'
        ? `Доступність для: ${currentMobilityType}`
        : 'Загальна доступність';

    panel.innerHTML = `
        <div class="info-header">
            <div>
                <p>Тип дороги: ${road.type}</p>
            </div>
            <button class="close-btn" onclick="closeInfoPanel()">×</button>
        </div>
        <div class="info-content">
            <div class="score-box" style="background-color: ${road.color}20;">
                <div class="score-header">
                    <span class="score-label">${scoreLabel}</span>
                    <span class="score-value" style="color: ${road.color};">
                        ${road.criteriaScore}/${road.totalCriteria}
                    </span>
                </div>
                <div class="progress-bar">
                    <div class="progress-fill" style="width: ${road.percentage * 100}%; background-color: ${road.color};"></div>
                </div>
                <div style="text-align: center; margin-top: 0.5rem; font-size: 0.875rem; color: #6b7280;">
                    ${Math.round(road.percentage * 100)}% задоволено
                </div>
            </div>
            <div class="info-item">
                <span class="info-item-label">Покриття:</span>
                <span class="info-item-value">${road.surface || 'невідомо'}</span>
            </div>
            <div class="info-item">
                <span class="info-item-label">Ширина:</span>
                <span class="info-item-value">${road.width}м</span>
            </div>
            <div class="info-item">
                <span class="info-item-label">Освітлення:</span>
                <span class="info-item-value">${road.lit === 'yes' ? 'Так' : 'Ні'}</span>
            </div>
            <div class="info-item">
                <span class="info-item-label">Гладкість:</span>
                <span class="info-item-value">${road.smoothness || 'невідомо'}</span>
            </div>
            <div style="margin-top: 1rem;">
                <div class="criteria-title">
                    📋 Критерії доступності ${roadCriteria ? '(⭐ = необхідне)' : ''}
                </div>
                ${criteriaList}
            </div>
            <button onclick="openReportForRoad(${JSON.stringify(road).replace(/"/g, '&quot;')})"
                    style="margin-top:1rem; width:100%; padding:0.6rem; background:#2563eb; color:white;
                           border:none; border-radius:0.375rem; cursor:pointer; font-size:0.875rem; font-weight:500;">
                📷 Повідомити про стан дороги
            </button>
        </div>
    `;
}

function showBridgeInfo(bridge) {
    const panel = document.getElementById('infoPanel');
    panel.classList.add('active');

    const roadCriteria = getRoadCriteriaForCurrentType();

    const criteriaList = Object.entries(bridge.criteriaStatus || {})
        .map(([key, met]) => {
            const label = criteriaLabels[key] || key;
            const required = isRoadCriteriaRequired(key, roadCriteria);
            return `
                <div class="criteria-item ${met ? 'criteria-met' : 'criteria-not-met'}">
                    <span>${label}${required ? ' ⭐' : ''}</span>
                    <span>${met ? '✓' : '✗'}</span>
                </div>
            `;
        }).join('');

    const scoreLabel = currentMobilityType !== 'all'
        ? `Доступність для: ${currentMobilityType}`
        : 'Загальна доступність';

    panel.innerHTML = `
        <div class="info-header">
            <div>
                <h3>🌉 ${bridge.name || "З'єднувальна дорога"}</h3>
                <p>Штучне ребро для з'єднання графу</p>
            </div>
            <button class="close-btn" onclick="closeInfoPanel()">×</button>
        </div>
        <div class="info-content">
            <div class="score-box" style="background-color: ${bridge.color}20;">
                <div class="score-header">
                    <span class="score-label">${scoreLabel}</span>
                    <span class="score-value" style="color: ${bridge.color};">
                        ${bridge.criteriaScore}/${bridge.totalCriteria}
                    </span>
                </div>
                <div class="progress-bar">
                    <div class="progress-fill" style="width: ${bridge.percentage * 100}%; background-color: ${bridge.color};"></div>
                </div>
                <div style="text-align: center; margin-top: 0.5rem; font-size: 0.875rem; color: #6b7280;">
                    ${Math.round(bridge.percentage * 100)}% задоволено
                </div>
            </div>
            <div class="info-item">
                <span class="info-item-label">Покриття:</span>
                <span class="info-item-value">${bridge.surface || 'невідомо'}</span>
            </div>
            <div class="info-item">
                <span class="info-item-label">Ширина:</span>
                <span class="info-item-value">${bridge.width}м</span>
            </div>
            <div class="info-item">
                <span class="info-item-label">Освітлення:</span>
                <span class="info-item-value">${bridge.lit === 'yes' ? 'Так' : 'Ні'}</span>
            </div>
            <div class="info-item">
                <span class="info-item-label">Гладкість:</span>
                <span class="info-item-value">${bridge.smoothness || 'невідомо'}</span>
            </div>
            <div style="margin-top: 1rem; padding: 0.75rem; background: #FFF3E0; border-radius: 0.5rem; font-size: 0.875rem;">
                ⚠️ <strong>Увага:</strong> Це штучна дорога, додана для з'єднання ізольованих компонент графу.
                Реальна дорога на цьому місці може не існувати або мати інші характеристики.
            </div>
            <div style="margin-top: 1rem;">
                <div class="criteria-title">
                    📋 Критерії доступності ${roadCriteria ? '(⭐ = необхідне)' : ''}
                </div>
                ${criteriaList}
            </div>
        </div>
    `;
}
