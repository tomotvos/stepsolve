// StepSolve dashboard — WebSocket real-time updates with polling fallback
(function () {
    'use strict';

    var POLL_INTERVAL_MS = 5000;
    var MAX_LOG_ENTRIES = 500;
    var STALE_THRESHOLD_MS = 5000;
    var WS_RECONNECT_BASE_MS = 1000;
    var WS_RECONNECT_MAX_MS = 30000;

    var els = {
        ra: document.getElementById('ra'),
        dec: document.getElementById('dec'),
        confidence: document.getElementById('confidence'),
        solver: document.getElementById('solver'),
        solveTime: document.getElementById('solve-time'),
        lastSolveTime: document.getElementById('last-solve-time'),
        stateBadge: document.getElementById('state-badge'),
        modeSelect: document.getElementById('mode-select'),
        themeToggle: document.getElementById('theme-toggle'),
        solveNowBtn: document.getElementById('solve-now-btn'),
        onstepStatus: document.getElementById('onstep-status'),
        onstepLastSync: document.getElementById('onstep-last-sync'),
        onstepResult: document.getElementById('onstep-result'),
        onstepSection: document.getElementById('onstep-display'),
        logContainer: document.getElementById('log-container'),
        logPause: document.getElementById('log-pause'),
        logClear: document.getElementById('log-clear'),
        logCount: document.getElementById('log-count'),
        solveDisplay: document.getElementById('solve-display'),
        wsIndicator: document.getElementById('ws-indicator'),
        wsLabel: document.getElementById('ws-label'),
        previewImg: document.getElementById('preview-img'),
        noImageMsg: document.getElementById('no-image-msg'),
        // Settings elements
        setBackend: document.getElementById('set-backend'),
        setHintTimeout: document.getElementById('set-hint-timeout'),
        setSolveRadius: document.getElementById('set-solve-radius'),
        setShutter: document.getElementById('set-shutter'),
        setGain: document.getElementById('set-gain'),
        setWidth: document.getElementById('set-width'),
        setHeight: document.getElementById('set-height'),
        setOnstepEnabled: document.getElementById('set-onstep-enabled'),
        setOnstepHost: document.getElementById('set-onstep-host'),
        setOnstepPort: document.getElementById('set-onstep-port'),
        setMaxSyncDelta: document.getElementById('set-max-sync-delta'),
        settingsSave: document.getElementById('settings-save'),
        settingsMsg: document.getElementById('settings-msg'),
    };

    var state = {
        ws: null,
        wsRetries: 0,
        pollTimer: null,
        logPaused: false,
        logEntryCount: 0,
        lastSolveTimestamp: null,
        staleTimer: null,
    };

    // -- Formatting helpers --

    function formatRa(deg) {
        if (deg == null) return '--';
        var hours = deg / 15.0;
        var h = Math.floor(hours);
        var m = Math.floor((hours - h) * 60);
        var s = Math.floor(((hours - h) * 60 - m) * 60);
        return pad2(h) + 'h ' + pad2(m) + 'm ' + pad2(s) + 's';
    }

    function formatDec(deg) {
        if (deg == null) return '--';
        var sign = deg >= 0 ? '+' : '-';
        var v = Math.abs(deg);
        var d = Math.floor(v);
        var m = Math.floor((v - d) * 60);
        var s = Math.floor(((v - d) * 60 - m) * 60);
        return sign + pad2(d) + '\u00B0 ' + pad2(m) + "' " + pad2(s) + '"';
    }

    function pad2(n) { return n < 10 ? '0' + n : '' + n; }

    function formatTime(isoStr) {
        if (!isoStr) return '--';
        try {
            return new Date(isoStr).toLocaleTimeString();
        } catch (e) {
            return '--';
        }
    }

    // -- Display updates (all via textContent for XSS safety) --

    function updateSolveDisplay(data) {
        els.ra.textContent = formatRa(data.ra);
        els.dec.textContent = formatDec(data.dec);
        els.confidence.textContent = data.confidence != null ? data.confidence.toFixed(2) : '--';
        els.solver.textContent = data.solver || '--';
        els.solveTime.textContent = data.solveTimeMs != null ? (data.solveTimeMs / 1000).toFixed(1) + 's' : '--';
        els.lastSolveTime.textContent = formatTime(data.timestamp || data.lastSolveTimestamp);

        state.lastSolveTimestamp = data.timestamp || data.lastSolveTimestamp || null;
        checkStale();

        // Refresh image preview (cache-bust with timestamp)
        refreshImagePreview();
    }

    function refreshImagePreview() {
        var img = els.previewImg;
        img.src = '/solve/image?t=' + Date.now();
        img.onload = function () {
            img.classList.add('visible');
            els.noImageMsg.classList.add('hidden');
        };
        img.onerror = function () {
            img.classList.remove('visible');
            els.noImageMsg.classList.remove('hidden');
        };
    }

    function updateStateBadge(st) {
        if (!st) return;
        els.stateBadge.textContent = st;
        els.stateBadge.className = 'badge badge-' + st;
    }

    function updateMode(mode) {
        if (!mode) return;
        var lower = mode.toLowerCase();
        els.modeSelect.value = lower;
    }

    function updateOnStep(onstep) {
        if (!onstep) {
            els.onstepSection.style.display = 'none';
            return;
        }
        els.onstepSection.style.display = '';
        var hasSync = onstep.lastSyncResult != null;
        els.onstepStatus.textContent = onstep.enabled !== false ? (hasSync ? 'Active' : 'Enabled') : 'Disabled';
        els.onstepLastSync.textContent = formatTime(onstep.lastSyncTimestamp);
        els.onstepResult.textContent = onstep.lastSyncResult || '--';
    }

    function updateFromStatus(data) {
        updateSolveDisplay(data);
        updateStateBadge(data.state);
        updateMode(data.mode);
        updateOnStep(data.onstep);
    }

    // -- Stale check --

    function checkStale() {
        if (state.staleTimer) clearInterval(state.staleTimer);
        state.staleTimer = setInterval(function () {
            if (!state.lastSolveTimestamp) return;
            var age = Date.now() - new Date(state.lastSolveTimestamp).getTime();
            if (age > STALE_THRESHOLD_MS) {
                els.solveDisplay.classList.add('stale');
            } else {
                els.solveDisplay.classList.remove('stale');
            }
        }, 1000);
    }

    // -- Log stream --

    function appendLog(level, message, timestamp) {
        if (state.logPaused) return;

        var entry = document.createElement('div');
        entry.className = 'log-entry log-' + (level || 'INFO');

        var timeSpan = document.createElement('span');
        timeSpan.className = 'log-time';
        timeSpan.textContent = formatTime(timestamp);

        var levelSpan = document.createElement('span');
        levelSpan.className = 'log-level';
        levelSpan.textContent = level || 'INFO';

        var msgSpan = document.createElement('span');
        msgSpan.className = 'log-msg';
        msgSpan.textContent = message || '';

        entry.appendChild(timeSpan);
        entry.appendChild(levelSpan);
        entry.appendChild(msgSpan);

        els.logContainer.appendChild(entry);
        state.logEntryCount++;

        // Trim oldest entries if over limit
        while (els.logContainer.childNodes.length > MAX_LOG_ENTRIES) {
            els.logContainer.removeChild(els.logContainer.firstChild);
            state.logEntryCount--;
        }

        els.logCount.textContent = state.logEntryCount + ' entries';

        // Auto-scroll to bottom
        els.logContainer.scrollTop = els.logContainer.scrollHeight;
    }

    // -- WebSocket --

    function connectWebSocket() {
        var protocol = location.protocol === 'https:' ? 'wss:' : 'ws:';
        var url = protocol + '//' + location.host + '/ws';

        try {
            state.ws = new WebSocket(url);
        } catch (e) {
            startPolling();
            return;
        }

        state.ws.onopen = function () {
            state.wsRetries = 0;
            setConnectionStatus(true);
            stopPolling();
        };

        state.ws.onmessage = function (e) {
            try {
                var msg = JSON.parse(e.data);
                switch (msg.type) {
                    case 'solve':
                        updateSolveDisplay(msg);
                        break;
                    case 'status':
                        updateStateBadge(msg.state);
                        updateMode(msg.mode);
                        updateOnStep(msg.onstep);
                        break;
                    case 'log':
                        appendLog(msg.level, msg.message, msg.timestamp);
                        break;
                }
            } catch (err) {
                // Ignore malformed messages
            }
        };

        state.ws.onclose = function () {
            setConnectionStatus(false);
            scheduleReconnect();
        };

        state.ws.onerror = function () {
            setConnectionStatus(false);
        };
    }

    function scheduleReconnect() {
        state.wsRetries++;
        var delay = Math.min(
            WS_RECONNECT_BASE_MS * Math.pow(2, state.wsRetries - 1),
            WS_RECONNECT_MAX_MS
        );
        startPolling(); // Fall back to polling while disconnected
        setTimeout(connectWebSocket, delay);
    }

    function setConnectionStatus(connected) {
        els.wsIndicator.className = 'indicator ' + (connected ? 'connected' : 'disconnected');
        els.wsLabel.textContent = connected ? 'Connected' : 'Disconnected';
    }

    // -- Polling fallback --

    function startPolling() {
        if (state.pollTimer) return;
        state.pollTimer = setInterval(pollStatus, POLL_INTERVAL_MS);
    }

    function stopPolling() {
        if (state.pollTimer) {
            clearInterval(state.pollTimer);
            state.pollTimer = null;
        }
    }

    function pollStatus() {
        fetch('/status')
            .then(function (r) { return r.ok ? r.json() : null; })
            .then(function (data) { if (data) updateFromStatus(data); })
            .catch(function () { /* retry next interval */ });
    }

    // -- User interactions --

    els.themeToggle.addEventListener('click', function () {
        document.documentElement.classList.toggle('night');
        var isNight = document.documentElement.classList.contains('night');
        els.themeToggle.textContent = isNight ? '☀️' : '🌙';
    });

    els.modeSelect.addEventListener('change', function () {
        var mode = els.modeSelect.value;
        fetch('/mode', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ mode: mode })
        })
            .then(function (r) {
                if (!r.ok) pollStatus();
            })
            .catch(function () { pollStatus(); });
    });

    els.logPause.addEventListener('click', function () {
        state.logPaused = !state.logPaused;
        els.logPause.textContent = state.logPaused ? '▶' : '⏸';
    });

    els.logClear.addEventListener('click', function () {
        els.logContainer.innerHTML = '';
        state.logEntryCount = 0;
        els.logCount.textContent = '0 entries';
    });

    els.solveNowBtn.addEventListener('click', function () {
        els.solveNowBtn.disabled = true;
        fetch('/solve?demo=1', { method: 'POST' })
            .then(function (r) { return r.json(); })
            .then(function (data) {
                if (data && data.ra != null) updateSolveDisplay(data);
            })
            .catch(function () { /* ignore */ })
            .finally(function () { els.solveNowBtn.disabled = false; });
    });

    // -- Settings panel --

    function loadSettings() {
        fetch('/settings')
            .then(function (r) { return r.ok ? r.json() : null; })
            .then(function (data) {
                if (!data) return;
                if (data.solver) {
                    els.setBackend.value = data.solver.backend || 'astrometry';
                    els.setHintTimeout.value = data.solver.hintTimeout || 10;
                    els.setSolveRadius.value = data.solver.solveRadius || 20;
                }
                if (data.camera) {
                    els.setShutter.value = data.camera.shutterUs || 1000000;
                    els.setGain.value = data.camera.gain || 8;
                    els.setWidth.value = data.camera.width || 1280;
                    els.setHeight.value = data.camera.height || 960;
                }
                if (data.onstep) {
                    els.setOnstepEnabled.checked = !!data.onstep.enabled;
                    els.setOnstepHost.value = data.onstep.host || 'localhost';
                    els.setOnstepPort.value = data.onstep.port || 9998;
                    els.setMaxSyncDelta.value = data.onstep.maxSyncDeltaDeg || 5;
                }
            })
            .catch(function () { /* ignore */ });
    }

    els.settingsSave.addEventListener('click', function () {
        els.settingsMsg.textContent = '';
        els.settingsMsg.className = 'settings-msg';

        var payload = {
            Solver: {
                Backend: els.setBackend.value,
                HintTimeout: els.setHintTimeout.value,
                SolveRadius: els.setSolveRadius.value
            },
            Camera: {
                ShutterUs: els.setShutter.value,
                Gain: els.setGain.value,
                Width: els.setWidth.value,
                Height: els.setHeight.value
            },
            OnStep: {
                Enabled: els.setOnstepEnabled.checked.toString(),
                Host: els.setOnstepHost.value,
                Port: els.setOnstepPort.value,
                MaxSyncDeltaDeg: els.setMaxSyncDelta.value
            }
        };

        fetch('/settings', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(payload)
        })
            .then(function (r) { return r.json().then(function (data) { return { ok: r.ok, data: data }; }); })
            .then(function (result) {
                if (result.ok) {
                    els.settingsMsg.textContent = 'Saved';
                    els.settingsMsg.className = 'settings-msg ok';
                } else {
                    els.settingsMsg.textContent = result.data.error || 'Save failed';
                    els.settingsMsg.className = 'settings-msg err';
                }
                setTimeout(function () { els.settingsMsg.textContent = ''; }, 3000);
            })
            .catch(function () {
                els.settingsMsg.textContent = 'Network error';
                els.settingsMsg.className = 'settings-msg err';
            });
    });

    // -- Init --

    // Initial status fetch
    pollStatus();

    // Load settings panel values
    loadSettings();

    // Try initial image preview
    refreshImagePreview();

    // Start WebSocket connection
    connectWebSocket();
})();
