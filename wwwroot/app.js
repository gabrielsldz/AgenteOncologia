// ============================================
// STATE MANAGEMENT
// ============================================

const state = {
    apiKey: null,
    messages: [],
    isLoading: false
};

// ============================================
// DOM ELEMENTS
// ============================================

const elements = {
    // Modal
    apiKeyModal: document.getElementById('apiKeyModal'),
    apiKeyInput: document.getElementById('apiKeyInput'),
    btnSaveApiKey: document.getElementById('btnSaveApiKey'),

    // Chat
    welcome: document.getElementById('welcome'),
    messages: document.getElementById('messages'),
    messageInput: document.getElementById('messageInput'),
    btnSend: document.getElementById('btnSend'),
    btnClear: document.getElementById('btnClear'),
    loading: document.getElementById('loading')
};

// ============================================
// INITIALIZATION
// ============================================

function init() {
    // Check for stored API key
    const storedApiKey = localStorage.getItem('gemini_api_key');

    if (storedApiKey) {
        state.apiKey = storedApiKey;
        hideApiKeyModal();
    } else {
        showApiKeyModal();
    }

    // Event listeners
    elements.btnSaveApiKey.addEventListener('click', handleSaveApiKey);
    elements.btnSend.addEventListener('click', handleSendMessage);
    elements.btnClear.addEventListener('click', handleClearChat);
    elements.messageInput.addEventListener('keydown', handleInputKeydown);

    // Auto-resize textarea
    elements.messageInput.addEventListener('input', handleInputResize);

    // Suggestion buttons (welcome screen)
    document.querySelectorAll('.suggestion-btn').forEach(btn => {
        btn.addEventListener('click', () => {
            const text = btn.getAttribute('data-text');
            elements.messageInput.value = text;
            elements.messageInput.focus();
            handleSendMessage();
        });
    });

    // Event delegation for pill suggestions (dynamically added in chat)
    document.addEventListener('click', (e) => {
        if (e.target.classList.contains('pill-suggestion')) {
            const question = e.target.getAttribute('data-question');
            if (question) {
                elements.messageInput.value = question;
                elements.messageInput.focus();
                handleSendMessage();
            }
        }
    });

    // Focus input
    elements.messageInput.focus();
}

// ============================================
// API KEY MANAGEMENT
// ============================================

function showApiKeyModal() {
    elements.apiKeyModal.classList.remove('hidden');
    elements.apiKeyInput.focus();
}

function hideApiKeyModal() {
    elements.apiKeyModal.classList.add('hidden');
}

function handleSaveApiKey() {
    const apiKey = elements.apiKeyInput.value.trim();

    if (!apiKey) {
        alert('Por favor, insira sua API Key');
        return;
    }

    // Validate API key format (basic check)
    if (!apiKey.startsWith('AIza')) {
        alert('API Key inválida. Deve começar com "AIza"');
        return;
    }

    // Save to state and localStorage
    state.apiKey = apiKey;
    localStorage.setItem('gemini_api_key', apiKey);

    hideApiKeyModal();
    elements.messageInput.focus();
}

// ============================================
// MESSAGE HANDLING
// ============================================

function handleInputKeydown(e) {
    // Send on Enter (without Shift)
    if (e.key === 'Enter' && !e.shiftKey) {
        e.preventDefault();
        handleSendMessage();
    }
}

function handleInputResize() {
    const input = elements.messageInput;
    input.style.height = 'auto';
    input.style.height = Math.min(input.scrollHeight, 120) + 'px';
}

async function handleSendMessage() {
    const message = elements.messageInput.value.trim();

    if (!message || state.isLoading) {
        return;
    }

    // Hide welcome
    elements.welcome.classList.add('hidden');

    // Add user message
    addMessage('user', message);

    // Clear input
    elements.messageInput.value = '';
    elements.messageInput.style.height = 'auto';

    // Show loading
    setLoading(true);

    try {
        // Send to API
        const response = await fetch('/api/chat', {
            method: 'POST',
            headers: {
                'Content-Type': 'application/json'
            },
            body: JSON.stringify({
                message: message,
                apiKey: state.apiKey
            })
        });

        const data = await response.json();

        if (!response.ok) {
            throw new Error(data.error || 'Erro ao processar mensagem');
        }

        if (data.needsApiKey) {
            showApiKeyModal();
            throw new Error(data.error);
        }

        // Add AI response
        addMessage('ai', data.response);

    } catch (error) {
        console.error('Error:', error);
        addMessage('ai', `❌ ${error.message}`);
    } finally {
        setLoading(false);
        elements.messageInput.focus();
    }
}

function handleClearChat() {
    if (state.messages.length === 0) {
        return;
    }

    if (confirm('Tem certeza que deseja limpar toda a conversa?')) {
        state.messages = [];
        elements.messages.innerHTML = '';
        elements.welcome.classList.remove('hidden');
        elements.messageInput.focus();
    }
}

// ============================================
// UI UPDATES
// ============================================

function addMessage(role, content) {
    // Add to state
    state.messages.push({ role, content });

    // Create message element
    const messageEl = document.createElement('div');
    messageEl.className = `message ${role}`;

    const avatar = role === 'user' ? '👤' : '🤖';
    const label = role === 'user' ? 'Você' : 'IA';

    messageEl.innerHTML = `
        <div class="message-avatar">${avatar}</div>
        <div class="message-content">
            <div class="message-label">${label}</div>
            <div class="message-bubble">${formatMessage(content)}</div>
        </div>
    `;

    // Add to DOM
    elements.messages.appendChild(messageEl);

    // Scroll to bottom
    scrollToBottom();
}

function formatMessage(content) {
    // Convert suggestions to pills FIRST (before other formatting)
    content = convertSuggestionsToPills(content);

    // Convert markdown-style code blocks
    content = content.replace(/```(\w+)?\n([\s\S]*?)```/g, '<pre><code>$2</code></pre>');

    // Convert inline code
    content = content.replace(/`([^`]+)`/g, '<code>$1</code>');

    // Convert bold
    content = content.replace(/\*\*([^*]+)\*\*/g, '<strong>$1</strong>');

    // Convert line breaks
    content = content.replace(/\n/g, '<br>');

    return content;
}

function convertSuggestionsToPills(content) {
    // Detect suggestions pattern: "• Question?" or "* Question?"
    const lines = content.split('\n');
    const suggestions = [];
    const cleanedLines = [];
    let inSuggestionBlock = false;

    for (let i = 0; i < lines.length; i++) {
        const line = lines[i].trim();

        // Check if this is a suggestion line (accepts •, ●, or *)
        if (line.match(/^[•●\*]\s*(.+\?)$/)) {
            const match = line.match(/^[•●\*]\s*(.+\?)$/);
            if (match) {
                suggestions.push(match[1].trim());
                inSuggestionBlock = true;
                continue; // Skip this line, we'll replace with pills
            }
        } else if (line === '' && inSuggestionBlock) {
            continue; // Skip empty lines in suggestion blocks
        } else {
            // Not a suggestion, add to cleaned lines
            if (inSuggestionBlock && suggestions.length > 0) {
                // We've finished collecting suggestions, add pills
                cleanedLines.push(createPillsHTML(suggestions));
                suggestions.length = 0; // Clear suggestions array
                inSuggestionBlock = false;
            }
            cleanedLines.push(lines[i]); // Keep original line (with original formatting)
        }
    }

    // If suggestions were at the end, add them
    if (suggestions.length > 0) {
        cleanedLines.push(createPillsHTML(suggestions));
    }

    return cleanedLines.join('\n');
}

function createPillsHTML(suggestions) {
    const pillsHtml = suggestions.map(suggestion => {
        // Escape HTML to prevent XSS
        const escaped = suggestion
            .replace(/&/g, '&amp;')
            .replace(/</g, '&lt;')
            .replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;');

        return `<button class="pill-suggestion" data-question="${escaped}">${escaped}</button>`;
    }).join('');

    return `<div class="suggestion-pills">${pillsHtml}</div>`;
}

function setLoading(isLoading) {
    state.isLoading = isLoading;
    elements.btnSend.disabled = isLoading;
    elements.messageInput.disabled = isLoading;

    if (isLoading) {
        elements.loading.style.display = 'flex';
        scrollToBottom();
    } else {
        elements.loading.style.display = 'none';
    }
}

function scrollToBottom() {
    setTimeout(() => {
        const container = document.querySelector('.chat-container');
        container.scrollTop = container.scrollHeight;
    }, 100);
}

// ============================================
// UTILITY FUNCTIONS
// ============================================

function formatTimestamp() {
    const now = new Date();
    return now.toLocaleTimeString('pt-BR', {
        hour: '2-digit',
        minute: '2-digit'
    });
}

// ============================================
// START APP
// ============================================

document.addEventListener('DOMContentLoaded', init);
