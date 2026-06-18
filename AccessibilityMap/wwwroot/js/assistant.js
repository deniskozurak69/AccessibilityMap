// assistant.js — AI-асистент на базі Gemini

let assistantOpen = false;
let chatHistory = [];       // { role, text }
let mobilityTypes = [];     // завантажені типи мобільності

// ── Ініціалізація ─────────────────────────────────────────────────────────────
async function initAssistant() {
    try {
        const res = await fetch('/api/gemini/mobility-types');
        const data = await res.json();
        mobilityTypes = ['Всі пристосування', ...(data.types || [])];
    } catch (e) {
        mobilityTypes = ['Всі пристосування'];
    }
}

// ── Відкрити/закрити ──────────────────────────────────────────────────────────
function toggleAssistant() {
    assistantOpen = !assistantOpen;
    const panel = document.getElementById('assistantPanel');
    const btn = document.getElementById('assistantBtn');

    if (assistantOpen) {
        panel.style.display = 'flex';
        btn.classList.add('active');
        if (chatHistory.length === 0) {
            startConversation();
        }
    } else {
        panel.style.display = 'none';
        btn.classList.remove('active');
    }
}

function closeAssistant() {
    assistantOpen = false;
    document.getElementById('assistantPanel').style.display = 'none';
    document.getElementById('assistantBtn').classList.remove('active');
}

// ── Початок розмови ───────────────────────────────────────────────────────────
async function startConversation() {
    chatHistory = [];
    renderMessages();
    await sendToGemini('Привіт! Допоможи мені знайти маршрут.');
}

function resetConversation() {
    chatHistory = [];
    renderMessages();
    startConversation();
}

// ── Відправка повідомлення користувача ────────────────────────────────────────
async function sendUserMessage() {
    const input = document.getElementById('assistantInput');
    const text = input.value.trim();
    if (!text) return;

    input.value = '';
    chatHistory.push({ role: 'user', text });
    renderMessages();

    await sendToGemini(text);
}

function handleAssistantKeydown(e) {
    if (e.key === 'Enter' && !e.shiftKey) {
        e.preventDefault();
        sendUserMessage();
    }
}

// ── Запит до Gemini через бекенд ──────────────────────────────────────────────
async function sendToGemini(userText) {
    // Додаємо повідомлення користувача якщо ще не додане
    if (chatHistory[chatHistory.length - 1]?.text !== userText ||
        chatHistory[chatHistory.length - 1]?.role !== 'user') {
        chatHistory.push({ role: 'user', text: userText });
    }

    showTyping(true);

    try {
        const res = await fetch('/api/gemini/chat', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({
                messages: chatHistory,
                mobilityTypes
            })
        });

        const data = await res.json();
        showTyping(false);

        if (!res.ok) {
            appendAssistantMessage('Вибачте, сталася помилка. Спробуйте ще раз.');
            return;
        }

        // Прибираємо <CMD>...</CMD> з тексту перед відображенням
        const displayText = (data.text || '').replace(/<CMD>[\s\S]*?<\/CMD>/g, '').trim();
        appendAssistantMessage(displayText);

        // Виконуємо команду якщо є
        if (data.command) {
            setTimeout(() => executeCommand(data.command), 800);
        }

    } catch (e) {
        showTyping(false);
        appendAssistantMessage('Помилка з\'єднання. Перевірте інтернет і спробуйте знову.');
    }
}

function appendAssistantMessage(text) {
    chatHistory.push({ role: 'model', text });
    renderMessages();
}

// ── Виконання команди від Gemini ──────────────────────────────────────────────
async function executeCommand(command) {
    if (!command || !command.action) return;

    if (command.action === 'route') {
        const mobilityType = command.mobilityType || 'all';
        if (typeof changeMobilityType === 'function') {
            await changeMobilityType(mobilityType);
            document.getElementById('mobilityType').value = mobilityType;
        }

        appendAssistantMessage(
            `Будую маршрут до "${command.locationName}"... 🗺️\nЗакриваю чат і відображаю на карті.`
        );

        // ── Геокодинг через Nominatim ──
        let endLat = command.endLat;
        let endLng = command.endLng;

        if (command.locationName) {
            try {
                const geoRes = await fetch(
                    `/api/gemini/geocode?query=${encodeURIComponent(command.locationName)}`
                );
                if (geoRes.ok) {
                    const geo = await geoRes.json();
                    endLat = geo.lat;
                    endLng = geo.lon;
                    console.log(`Nominatim: ${geo.displayName} → ${endLat}, ${endLng}`);
                }
            } catch (e) {
                console.warn('Геокодинг не вдався, використовуємо координати Gemini:', e);
            }
        }

        setTimeout(() => {
            closeAssistant();
            /*if (typeof changeLayer === 'function') {
                changeLayer('roads');
                document.getElementById('layerType').value = 'roads';
            }*/
            if (typeof buildRouteToCoords === 'function') {
                buildRouteToCoords(endLat, endLng, command.locationName);
            }
        }, 1500);
    } else if (command.action === 'buildingType') {
        const mobilityType = command.mobilityType || 'all';
        if (typeof changeMobilityType === 'function') {
            await changeMobilityType(mobilityType);
            document.getElementById('mobilityType').value = mobilityType;
        }

        appendAssistantMessage(
            `Шукаю "${command.type}"... 🏢\nЗакриваю чат і відображаю маршрут на карті.`
        );

        setTimeout(() => {
            closeAssistant();

            /*if (typeof changeLayer === 'function') {
                changeLayer('roads');
                document.getElementById('layerType').value = 'roads';
            }*/

            if (typeof findAndRouteToBuildingType === 'function') {
                findAndRouteToBuildingType(command.type, command.mode || 'route');
            }
        }, 1500);
    }
}

// ── Рендер повідомлень ────────────────────────────────────────────────────────
function renderMessages() {
    const container = document.getElementById('assistantMessages');
    if (!container) return;

    container.innerHTML = chatHistory
        .filter(m => m.role !== 'user' || m.text !== 'Привіт! Допоможи мені знайти маршрут.')
        .map(m => {
            const isUser = m.role === 'user';
            return `
                <div style="display:flex; justify-content:${isUser ? 'flex-end' : 'flex-start'}; margin-bottom:0.75rem;">
                    <div style="
                        max-width:80%;
                        padding:0.6rem 0.875rem;
                        border-radius:${isUser ? '1rem 1rem 0.25rem 1rem' : '1rem 1rem 1rem 0.25rem'};
                        background:${isUser ? '#2563eb' : '#f1f5f9'};
                        color:${isUser ? 'white' : '#1e293b'};
                        font-size:0.875rem;
                        line-height:1.5;
                        white-space:pre-wrap;
                    ">${escapeHtml(m.text)}</div>
                </div>
            `;
        }).join('');

    container.scrollTop = container.scrollHeight;
}

function showTyping(show) {
    const el = document.getElementById('assistantTyping');
    if (el) el.style.display = show ? 'flex' : 'none';
    if (show) {
        const container = document.getElementById('assistantMessages');
        if (container) container.scrollTop = container.scrollHeight;
    }
}

function escapeHtml(text) {
    return text
        .replace(/&/g, '&amp;')
        .replace(/</g, '&lt;')
        .replace(/>/g, '&gt;')
        .replace(/"/g, '&quot;');
}

// ── Маршрут до координат (нова функція для routing.js) ────────────────────────
function buildRouteToCoords(endLat, endLng, locationName) {
    addRouteMarkers(KHRESCHATYK.lat, KHRESCHATYK.lng, endLat, endLng);

    showLoading('Побудова маршруту...');

    fetch('/api/accessibility/route', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
            startLat: KHRESCHATYK.lat,
            startLng: KHRESCHATYK.lng,
            endLat,
            endLng,
            includeIntersections: false,
            mobilityType: currentMobilityType
        })
    })
    .then(res => res.json())
    .then(data => {
        hideLoading();
        displayRoute(data);
        alert(`Маршрут до "${locationName}"\n\nВідстань: ${data.totalDistanceKm.toFixed(2)} км`);
    })
    .catch(e => {
        hideLoading();
        alert('Помилка побудови маршруту: ' + e.message);
    });
}

// Ініціалізація при завантаженні
initAssistant();
