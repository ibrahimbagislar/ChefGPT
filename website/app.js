// ============================================
// ChefGPT — App Logic (Streaming SSE)
// ============================================

const API_URL = "";  // Aynı origin, proxy yok
const FOOD_EMOJIS = ["🍅", "🥕", "🧅", "🌶️", "🥒", "🍋", "🫑", "🧄", "🥔", "🍆", "🌽", "🥦", "🍇", "🍊", "🫒", "🥜", "🌰"];
const VEG_CYCLE = ["🥕", "🍅", "🥒", "🧅", "🌶️", "🍆", "🥔", "🧄"];

let isWaiting = false;
let vegIndex = 0;

// ── Init ──
document.addEventListener("DOMContentLoaded", () => {
    initParticles();
    initInput();
    initCuttingBoard();
    checkServerHealth();
    setInterval(checkServerHealth, 30000);
});

// ── Food Particles Background ──
function initParticles() {
    const container = document.getElementById("foodParticles");
    for (let i = 0; i < 15; i++) {
        const particle = document.createElement("div");
        particle.className = "food-particle";
        particle.textContent = FOOD_EMOJIS[Math.floor(Math.random() * FOOD_EMOJIS.length)];
        particle.style.left = Math.random() * 100 + "%";
        particle.style.animationDelay = Math.random() * 18 + "s";
        particle.style.animationDuration = (15 + Math.random() * 10) + "s";
        particle.style.fontSize = (0.8 + Math.random() * 1) + "rem";
        container.appendChild(particle);
    }
}

// ── Cutting Board Animation ──
function initCuttingBoard() {
    const cutPieces = document.getElementById("cutPieces");
    if (!cutPieces) return;

    for (let i = 0; i < 3; i++) {
        const piece = document.createElement("span");
        piece.className = "cut-piece";
        piece.textContent = "🥕";
        cutPieces.appendChild(piece);
    }

    setInterval(() => {
        vegIndex = (vegIndex + 1) % VEG_CYCLE.length;
        const veg = VEG_CYCLE[vegIndex];
        const vegEl = document.getElementById("vegOnBoard");
        const pieces = document.querySelectorAll(".cut-piece");
        if (vegEl) vegEl.textContent = veg;
        pieces.forEach(p => p.textContent = veg);
    }, 4000);
}

// ── Input Handling ──
function initInput() {
    const input = document.getElementById("messageInput");
    const charCount = document.getElementById("charCount");

    input.addEventListener("input", () => {
        input.style.height = "auto";
        input.style.height = Math.min(input.scrollHeight, 120) + "px";
        charCount.textContent = `${input.value.length}/500`;
    });

    input.addEventListener("keydown", (e) => {
        if (e.key === "Enter" && !e.shiftKey) {
            e.preventDefault();
            sendMessage();
        }
    });
}

// ── Server Health ──
async function checkServerHealth() {
    const dot = document.querySelector(".status-dot");
    const text = document.querySelector(".status-text");
    const modelEl = document.getElementById("modelName");

    try {
        const res = await fetch(`/api/health`, { signal: AbortSignal.timeout(5000) });
        const data = await res.json();
        dot.className = "status-dot online";
        text.textContent = `Aktif — ${data.index_count?.toLocaleString() || "?"} tarif`;
        if (modelEl && data.model) modelEl.textContent = data.model;
    } catch {
        dot.className = "status-dot offline";
        text.textContent = "Sunucu bağlantısı yok";
    }
}

// ── Send Message (Streaming) ──
async function sendMessage() {
    const input = document.getElementById("messageInput");
    const message = input.value.trim();
    if (!message || isWaiting) return;

    // Hide welcome screen
    const welcome = document.getElementById("welcomeScreen");
    if (welcome) welcome.style.display = "none";

    // Add user message
    addMessage("user", message);
    input.value = "";
    input.style.height = "auto";
    document.getElementById("charCount").textContent = "0/500";

    // Show typing
    isWaiting = true;
    document.getElementById("sendBtn").disabled = true;
    showTypingIndicator();

    try {
        const res = await fetch(`/api/chat`, {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ message })
        });

        if (!res.ok) {
            const err = await res.json();
            hideTypingIndicator();
            addMessage("bot", `⚠️ Hata: ${err.error || "Bilinmeyen hata"}`);
            isWaiting = false;
            document.getElementById("sendBtn").disabled = false;
            return;
        }

        // SSE streaming okuma
        const reader = res.body.getReader();
        const decoder = new TextDecoder();
        let buffer = "";
        let botEl = null;
        let fullText = "";
        let sources = [];

        hideTypingIndicator();

        while (true) {
            const { done, value } = await reader.read();
            if (done) break;

            buffer += decoder.decode(value, { stream: true });

            // SSE formatını parse et
            const lines = buffer.split("\n");
            buffer = lines.pop(); // Son eksik satırı sakla

            for (const line of lines) {
                if (!line.startsWith("data: ")) continue;

                const jsonStr = line.substring(6).trim();
                if (!jsonStr) continue;

                try {
                    const data = JSON.parse(jsonStr);

                    if (data.type === "sources") {
                        sources = data.sources || [];
                    }

                    if (data.type === "token") {
                        if (!botEl) {
                            // İlk token → mesaj balonu oluştur
                            botEl = createStreamingMessage();
                        }
                        fullText += data.token;
                        updateStreamingMessage(botEl, fullText);
                        scrollToBottom();
                    }

                    if (data.type === "done") {
                        // Kaynakları ekle
                        if (botEl && sources.length > 0) {
                            appendSources(botEl, sources);
                        }
                        if (data.elapsed) {
                            document.getElementById("responseTime").textContent = `⚡ ${data.elapsed}s`;
                        }
                    }

                    if (data.type === "error") {
                        if (!botEl) {
                            addMessage("bot", `⚠️ ${data.error}`);
                        } else {
                            fullText += `\n\n⚠️ ${data.error}`;
                            updateStreamingMessage(botEl, fullText);
                        }
                    }
                } catch (parseErr) {
                    // JSON parse hatası, atla
                }
            }
        }

        // Eğer hiç token gelmediyse
        if (!botEl && fullText === "") {
            addMessage("bot", "⚠️ Cevap alınamadı.");
        }

    } catch (err) {
        hideTypingIndicator();
        addMessage("bot", "⚠️ Sunucuya bağlanılamadı. Server çalışıyor mu?");
    }

    isWaiting = false;
    document.getElementById("sendBtn").disabled = false;
    input.focus();
}

// ── Create Streaming Bot Message (empty) ──
function createStreamingMessage() {
    const container = document.getElementById("messagesContainer");
    const msgGroup = document.createElement("div");
    msgGroup.className = "message-group";
    msgGroup.innerHTML = `
        <div class="message bot">
            <div class="message-avatar">👨‍🍳</div>
            <div class="message-content">
                <div class="message-text"></div>
            </div>
        </div>
    `;
    container.appendChild(msgGroup);
    return msgGroup.querySelector(".message-text");
}

// ── Update Streaming Message ──
function updateStreamingMessage(el, text) {
    el.innerHTML = formatMessage(text);
}

// ── Append Sources to Message ──
function appendSources(textEl, sources) {
    const contentEl = textEl.closest(".message-content");
    if (!contentEl) return;

    const chips = sources.map(s => {
        if (s.url) {
            return `<a href="${escapeHtml(s.url)}" target="_blank" class="source-chip">📎 ${escapeHtml(s.baslik)}</a>`;
        }
        return `<span class="source-chip">📎 ${escapeHtml(s.baslik)}</span>`;
    }).join("");

    const div = document.createElement("div");
    div.className = "message-sources";
    div.innerHTML = `<div class="sources-title">📚 Kaynaklar</div>${chips}`;
    contentEl.appendChild(div);
}

// ── Add Static Message ──
function addMessage(role, text, sources = []) {
    const container = document.getElementById("messagesContainer");
    const msgGroup = document.createElement("div");
    msgGroup.className = "message-group";

    const avatar = role === "bot" ? "👨‍🍳" : "👤";
    const formattedText = formatMessage(text);

    let sourcesHTML = "";
    if (sources && sources.length > 0) {
        const chips = sources.map(s => {
            if (s.url) {
                return `<a href="${escapeHtml(s.url)}" target="_blank" class="source-chip">📎 ${escapeHtml(s.baslik)}</a>`;
            }
            return `<span class="source-chip">📎 ${escapeHtml(s.baslik)}</span>`;
        }).join("");
        sourcesHTML = `<div class="message-sources"><div class="sources-title">📚 Kaynaklar</div>${chips}</div>`;
    }

    msgGroup.innerHTML = `
        <div class="message ${role}">
            <div class="message-avatar">${avatar}</div>
            <div class="message-content">
                <div class="message-text">${formattedText}</div>
                ${sourcesHTML}
            </div>
        </div>
    `;

    container.appendChild(msgGroup);
    scrollToBottom();
}

// ── Format Message (Markdown-lite) ──
function formatMessage(text) {
    if (!text) return "";

    let html = escapeHtml(text);

    // Bold: **text**
    html = html.replace(/\*\*(.*?)\*\*/g, "<strong>$1</strong>");
    html = html.replace(/__(.*?)__/g, "<strong>$1</strong>");

    // Italic: *text*
    html = html.replace(/(?<!\*)\*(?!\*)(.*?)\*(?!\*)/g, "<em>$1</em>");

    // Bullet points
    html = html.replace(/^[•\-]\s+(.*)$/gm, "<li>$1</li>");
    html = html.replace(/(<li>.*<\/li>)/gs, "<ul>$1</ul>");
    html = html.replace(/<\/ul>\s*<ul>/g, "");

    // Line breaks
    html = html.replace(/\n/g, "<br>");
    html = html.replace(/(<br>\s*){3,}/g, "<br><br>");

    return html;
}

function escapeHtml(text) {
    const div = document.createElement("div");
    div.textContent = text;
    return div.innerHTML;
}

// ── Typing Indicator ──
function showTypingIndicator() {
    const container = document.getElementById("messagesContainer");
    const typingEl = document.createElement("div");
    typingEl.className = "typing-indicator";
    typingEl.id = "typingIndicator";

    const randomVeg = VEG_CYCLE[Math.floor(Math.random() * VEG_CYCLE.length)];

    typingEl.innerHTML = `
        <div class="typing-avatar">👨‍🍳</div>
        <div class="typing-bubble">
            <div class="typing-animation">
                <div class="mini-board">
                    <span class="mini-veg">${randomVeg}</span>
                    <span class="mini-knife">🔪</span>
                </div>
            </div>
            <div>
                <div class="typing-text">Tarif hazırlanıyor...</div>
                <div class="typing-dots">
                    <span class="typing-dot"></span>
                    <span class="typing-dot"></span>
                    <span class="typing-dot"></span>
                </div>
            </div>
        </div>
    `;

    container.appendChild(typingEl);
    scrollToBottom();
}

function hideTypingIndicator() {
    const el = document.getElementById("typingIndicator");
    if (el) el.remove();
}

// ── Scroll ──
function scrollToBottom() {
    const chatArea = document.getElementById("chatArea");
    requestAnimationFrame(() => {
        chatArea.scrollTo({ top: chatArea.scrollHeight, behavior: "smooth" });
    });
}

// ── Sidebar Toggle (Mobile) ──
function toggleSidebar() {
    document.getElementById("sidebar").classList.toggle("open");
}

// ── Suggestion Click ──
function useSuggestion(btn) {
    const text = btn.querySelector(".chip-text")?.textContent || btn.textContent;
    document.getElementById("messageInput").value = text.trim();
    document.getElementById("messageInput").focus();
    document.getElementById("sidebar").classList.remove("open");
    sendMessage();
}
