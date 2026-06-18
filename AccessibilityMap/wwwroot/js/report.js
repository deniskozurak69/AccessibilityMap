function openReportModal() {
    document.getElementById('reportModal').style.display = 'flex';
    document.getElementById('reportPhoto').value = '';
    document.getElementById('reportPreview').style.display = 'none';
    document.getElementById('reportStatus').style.display = 'none';
}

function closeReportModal() {
    document.getElementById('reportModal').style.display = 'none';
}

document.getElementById('reportPhoto')?.addEventListener('change', function () {
    const file = this.files[0];
    if (!file) return;

    const reader = new FileReader();
    reader.onload = (e) => {
        document.getElementById('reportPreviewImg').src = e.target.result;
        document.getElementById('reportPreview').style.display = 'block';
    };
    reader.readAsDataURL(file);
});

// ── Переклад значень ML ──
const ML_VALUE_LABELS = {
    // Нерівність
    smooth: 'Гладкий',
    slight: 'Нерівний',
    severe: 'Дуже нерівний',
    // Якість покриття
    excellent: 'Чудова',
    good: 'Гарна',
    intermediate: 'Середня',
    bad: 'Погана',
    very_bad: 'Дуже погана',
    // Тип покриття
    asphalt: 'Асфальт',
    concrete: 'Бетон',
    paving_stones: 'Бруківка',
    sett: 'Кругляк',
    gravel: 'Гравій',
    unpaved: 'Грунтова',
    // Ширина
    wide: 'Широкий',
    narrow: 'Вузький',
};

// Контекстні переклади — yes/no означає різне залежно від поля
const ML_VALUE_LABELS_BY_KEY = {
    lit: {
        yes: 'Наявне', no: 'Відсутнє',
        lit: 'Наявне', unlit: 'Відсутнє',
    },
    ramp: {
        yes: 'Наявний', no: 'Відсутній',
        ramp: 'Наявний', no_ramp: 'Відсутній',
    },
    tactile_paving: {
        yes: 'Наявна', no: 'Відсутня',
    },
};

function mlLabel(key, rawValue) {
    if (!rawValue) return rawValue;
    const byKey = ML_VALUE_LABELS_BY_KEY[key];
    if (byKey && byKey[rawValue] !== undefined) return byKey[rawValue];
    return ML_VALUE_LABELS[rawValue] || rawValue;
}

async function submitReport() {
    console.log('submitReport викликано');
    const photoInput = document.getElementById('reportPhoto');
    const statusDiv = document.getElementById('reportStatus');
    const submitBtn = document.getElementById('reportSubmitBtn');

    if (!photoInput.files[0]) {
        showReportStatus('Будь ласка, виберіть фото', 'error');
        return;
    }

    submitBtn.disabled = true;
    submitBtn.textContent = 'Надсилаємо...';
    showReportStatus('Аналізуємо фото через ML моделі...', 'info');

    const formData = new FormData();
    formData.append('photo', photoInput.files[0]);

    const selectedModels = getSelectedModels();
    if (selectedModels.length === 0) {
        showReportStatus('Оберіть хоча б один параметр для аналізу', 'error');
        return;
    }
    formData.append('models', selectedModels.join(','));
    if (selectedRoadForReport) {
        formData.append('roadId', String(selectedRoadForReport.id));
        formData.append('roadName', selectedRoadForReport.name || '');
        if (selectedRoadForReport.coordinates?.length > 0) {
            const mid = Math.floor(selectedRoadForReport.coordinates.length / 2);
            formData.append('roadLat', selectedRoadForReport.coordinates[mid][0]);
            formData.append('roadLon', selectedRoadForReport.coordinates[mid][1]);
        }
    }

    try {
        const response = await fetch('/api/accessibility/report', {
            method: 'POST',
            body: formData
        });
        console.log('response.ok:', response.ok);
        const data = await response.json();
        console.log('FULL DATA:', JSON.stringify(data));
        if (!response.ok) {
            console.log('response not ok, виходимо');
            showReportStatus('Помилка: ' + data.message, 'error');
            return;
        }

        // Показуємо результат
        let resultHtml = '✓ Звіт надіслано адміністратору!<br>';
        if (data.nearestRoad) {
            resultHtml += `📍 Найближча дорога: <strong>${data.nearestRoad}</strong><br>`;
        }
        if (data.gpsWarning) {
            resultHtml += `⚠️ <strong>Увага:</strong> ${data.gpsWarning}<br>`;
        }
        if (data.gps) {
            resultHtml += `🌍 GPS: ${data.gps.lat.toFixed(5)}, ${data.gps.lon.toFixed(5)}<br>`;
        }
        if (data.mlResults) {
            console.log(data.mlResults)
            resultHtml += '<br>🤖 Результати аналізу:<br>';
            const labels = {
                smoothness: 'Нерівність',
                ramp: 'Пандус',
                width: 'Ширина',
                lit: 'Освітлення',
                tactile_paving: 'Тактильна плитка',
                surface_type: 'Тип покриття',
                surface_quality: 'Якість покриття',
            };
            for (const [key, val] of Object.entries(data.mlResults)) {
                const translatedValue = mlLabel(key, val.class);
                resultHtml += `• ${labels[key] || key}: <strong>${translatedValue}</strong> (${Math.round(val.confidence * 100)}%)<br>`;
            }
        }

        showReportStatus(resultHtml, 'success');
        submitBtn.textContent = 'Надіслано ✓';
        console.log('перед onReportSubmitted');
        onReportSubmitted();

    } catch (error) {
        showReportStatus('Помилка з\'єднання: ' + error.message, 'error');
        submitBtn.disabled = false;
        submitBtn.textContent = 'Надіслати';
    }
}

function showReportStatus(message, type) {
    const div = document.getElementById('reportStatus');
    div.style.display = 'block';
    div.innerHTML = message;
    div.style.background = type === 'success' ? '#dcfce7' :
        type === 'error' ? '#fee2e2' : '#dbeafe';
    div.style.color = type === 'success' ? '#166534' :
        type === 'error' ? '#991b1b' : '#1e40af';
}

function onReportSubmitted() {
    console.log('onReportSubmitted викликано');
    console.log(document.getElementById('reportCancelBtn'));
    console.log(document.getElementById('reportPhotoWrapper'));
    document.getElementById('reportPhotoWrapper').style.display = 'none';

    const cancelBtn = document.getElementById('reportCancelBtn');
    cancelBtn.disabled = true;
    cancelBtn.style.setProperty('opacity', '0.4', 'important');
    cancelBtn.style.setProperty('cursor', 'default', 'important');
    cancelBtn.style.setProperty('pointer-events', 'none', 'important');

    const submitBtn = document.getElementById('reportSubmitBtn');
    submitBtn.disabled = true;
    submitBtn.style.setProperty('background', '#9ca3af', 'important');
    submitBtn.style.setProperty('cursor', 'default', 'important');
    submitBtn.style.setProperty('pointer-events', 'none', 'important');
}

function selectAllModels(checked) {
    document.querySelectorAll('.model-checkbox').forEach(cb => cb.checked = checked);
}

function getSelectedModels() {
    return Array.from(document.querySelectorAll('.model-checkbox:checked')).map(cb => cb.value);
}

let selectedRoadForReport = null;

function openReportForRoad(road) {
    selectedRoadForReport = road;
    document.querySelector('#reportModal h3').textContent = '📷 Повідомити про стан дороги';

    let roadInfo = document.getElementById('selectedRoadInfo');
    if (!roadInfo) {
        roadInfo = document.createElement('div');
        roadInfo.id = 'selectedRoadInfo';
        roadInfo.style.cssText = 'padding:0.6rem 0.75rem; background:#eff6ff; border:1px solid #bfdbfe; border-radius:0.375rem; font-size:0.8rem; margin-bottom:1rem; color:#1e40af;';
        const firstChild = document.querySelector('#reportModal > div > p');
        firstChild.after(roadInfo);
    }
    roadInfo.innerHTML = `📸 Поради для фото:<br>
- Пандус, тактильна плитка або освітлення — фотографуйте об'єкт впритул ; <br>
- Якість покриття та нерівність — знімайте тротуар зблизька ; <br>
- Ширина дороги — фотографуйте всю ширину тротуару з одного краю`;

    openReportModal();
}

function openReportModal() {
    const modal = document.getElementById('reportModal');
    modal.style.display = 'flex';
    document.getElementById('reportPhoto').value = '';
    document.getElementById('reportPreview').style.display = 'none';
    document.getElementById('reportStatus').style.display = 'none';
    document.getElementById('reportPhotoWrapper').style.display = 'block';

    const submitBtn = document.getElementById('reportSubmitBtn');
    submitBtn.disabled = false;
    submitBtn.textContent = 'Надіслати';
    submitBtn.style.setProperty('background', '#2563eb', 'important');
    submitBtn.style.removeProperty('cursor');
    submitBtn.style.removeProperty('pointer-events');

    const cancelBtn = document.getElementById('reportCancelBtn');
    cancelBtn.disabled = false;
    cancelBtn.style.removeProperty('opacity');
    cancelBtn.style.removeProperty('cursor');
    cancelBtn.style.removeProperty('pointer-events');

    document.querySelectorAll('.model-checkbox').forEach(cb => cb.checked = true);
}

function closeReportModal() {
    document.getElementById('reportModal').style.display = 'none';
}

document.getElementById('reportBtn')?.addEventListener('click', openReportModal);
document.getElementById('reportPhotoCamera')?.addEventListener('change', function () {
    const file = this.files[0];
    if (!file) return;
    // Копіюємо файл в основний input щоб submitReport його підхопив
    const dt = new DataTransfer();
    dt.items.add(file);
    document.getElementById('reportPhoto').files = dt.files;

    const reader = new FileReader();
    reader.onload = (e) => {
        document.getElementById('reportPreviewImg').src = e.target.result;
        document.getElementById('reportPreview').style.display = 'block';
    };
    reader.readAsDataURL(file);
});