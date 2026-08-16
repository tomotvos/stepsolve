// StepSolve dashboard — WebSocket real-time updates with polling fallback
(function () {
    'use strict';

    var POLL_INTERVAL_MS = 5000;
    var MAX_LOG_ENTRIES = 500;
    var STALE_THRESHOLD_MS = 5000;
    var CALIBRATION_POLL_INTERVAL_MS = 2000;
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
        shutdownBtn: document.getElementById('shutdown-btn'),
        powerDialog: document.getElementById('power-dialog'),
        powerCancel: document.getElementById('power-cancel'),
        powerRestart: document.getElementById('power-restart'),
        powerShutdown: document.getElementById('power-shutdown'),
        solveNowBtn: document.getElementById('solve-now-btn'),
        onstepStatus: document.getElementById('onstep-status'),
        onstepLastSync: document.getElementById('onstep-last-sync'),
        onstepResult: document.getElementById('onstep-result'),
        onstepSection: document.getElementById('onstep-display'),
        calibrationSection: document.getElementById('onstep-calibration'),
        calibrationState: document.getElementById('calibration-state'),
        calibrationConnection: document.getElementById('calibration-connection'),
        calibrationTarget: document.getElementById('calibration-target'),
        calibrationPoint: document.getElementById('calibration-point'),
        calibrationCandidate: document.getElementById('calibration-candidate'),
        calibrationReply: document.getElementById('calibration-reply'),
        calibrationMessage: document.getElementById('calibration-message'),
        calibrationStart: document.getElementById('calibration-start'),
        calibrationAccept: document.getElementById('calibration-accept'),
        calibrationAbort: document.getElementById('calibration-abort'),
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
        setFovEstimate: document.getElementById('set-fov-estimate'),
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
        setTetra3Index: document.getElementById('set-tetra3-index'),
        setAstrometryIndex: document.getElementById('set-astrometry-index'),
        settingsSave: document.getElementById('settings-save'),
        settingsMsg: document.getElementById('settings-msg'),
        // Update section
        updateSection: document.getElementById('update-section'),
        updateBadge: document.getElementById('update-badge'),
        updateCurrent: document.getElementById('update-current'),
        updateLatest: document.getElementById('update-latest'),
        updateBtn: document.getElementById('update-btn'),
        updateMsg: document.getElementById('update-msg'),
    };

    var state = {
        ws: null,
        wsRetries: 0,
        pollTimer: null,
        logPaused: false,
        logEntryCount: 0,
        lastSolveTimestamp: null,
        staleTimer: null,
        calibrationPollTimer: null,
        mode: null,
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

        // Only refresh image preview when the server tells us one is available
        if (data.imageUrl) refreshImagePreview();
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
        state.mode = lower;
        els.modeSelect.value = lower;
        var inSolveLoop = lower === 'solve';
        els.solveNowBtn.disabled = inSolveLoop;
        els.solveNowBtn.title = inSolveLoop ? 'Solve loop is already running' : '';
        updateCalibrationVisibility();
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
        if (onstep.calibration) updateCalibration(onstep.calibration);
    }

    function updateCalibrationVisibility() {
        var isCalibrateMode = state.mode === 'calibrate';
        els.calibrationSection.hidden = !isCalibrateMode;

        if (isCalibrateMode && !state.calibrationPollTimer) {
            loadCalibrationStatus();
            state.calibrationPollTimer = setInterval(loadCalibrationStatus, CALIBRATION_POLL_INTERVAL_MS);
        } else if (!isCalibrateMode && state.calibrationPollTimer) {
            clearInterval(state.calibrationPollTimer);
            state.calibrationPollTimer = null;
        }
    }

    function updateCalibration(calibration) {
        if (!calibration) return;

        els.calibrationState.textContent = calibration.state || '--';
        els.calibrationConnection.textContent = calibration.isConnected ? (calibration.isSafe ? 'Connected / safe' : 'Connected / unsafe') : 'Not connected';
        els.calibrationTarget.textContent = calibration.requestedAzimuthDeg != null && calibration.requestedAltitudeDeg != null
            ? 'Az ' + calibration.requestedAzimuthDeg.toFixed(1) + '\u00B0, Alt ' + calibration.requestedAltitudeDeg.toFixed(1) + '\u00B0'
            : '--';
        els.calibrationPoint.textContent = calibration.currentPoint != null && calibration.currentPoint > 0
            ? calibration.currentPoint + ' / 3' + (calibration.attempt ? ', attempt ' + calibration.attempt : '')
            : '--';
        els.calibrationCandidate.textContent = calibration.candidateRaDeg != null && calibration.candidateDecDeg != null
            ? formatRa(calibration.candidateRaDeg) + ', ' + formatDec(calibration.candidateDecDeg)
            : '--';
        els.calibrationReply.textContent = calibration.lastReply || '--';
        els.calibrationMessage.textContent = calibration.message || '';

        // These are presentation hints only. The server repeats every safety and
        // operating-mode check before issuing an OnStep command.
        var isCalibrateMode = state.mode === 'calibrate';
        els.calibrationStart.disabled = !isCalibrateMode || !calibration.isConnected || !calibration.isSafe;
        els.calibrationAccept.disabled = !isCalibrateMode || calibration.candidateRaDeg == null || calibration.candidateDecDeg == null;
        els.calibrationAbort.disabled = !isCalibrateMode || !calibration.state || calibration.state.toLowerCase() === 'idle';
    }

    function loadCalibrationStatus() {
        if (state.mode !== 'calibrate') return;
        fetch('/onstep/calibration')
            .then(function (r) { return r.ok ? r.json() : null; })
            .then(function (data) { if (data) updateCalibration(data); })
            .catch(function () { /* status polling will retry while Calibrate mode is active */ });
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
            loadUpdateStatus();
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
                    case 'image':
                        refreshImagePreview();
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

    els.shutdownBtn.addEventListener('click', function () {
        els.powerDialog.showModal();
    });

    els.powerCancel.addEventListener('click', function () {
        els.powerDialog.close();
    });

    els.powerShutdown.addEventListener('click', function () {
        els.powerDialog.close();
        els.shutdownBtn.disabled = true;
        fetch('/system/shutdown', { method: 'POST' })
            .catch(function () { /* connection drop is expected */ });
    });

    els.powerRestart.addEventListener('click', function () {
        els.powerDialog.close();
        els.shutdownBtn.disabled = true;
        fetch('/system/restart', { method: 'POST' })
            .catch(function () { /* connection drop is expected */ });
    });

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

    function requestCalibrationAction(path, body, successMessage) {
        fetch(path, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: body ? JSON.stringify(body) : null
        })
            .then(function (r) {
                return r.json().catch(function () { return {}; }).then(function (data) {
                    return { ok: r.ok, data: data };
                });
            })
            .then(function (result) {
                if (result.data.calibration) updateCalibration(result.data.calibration);
                if (result.ok) {
                    appendLog('INFO', successMessage);
                } else {
                    appendLog('WARNING', result.data.error || 'OnStep calibration action was rejected');
                }
                loadCalibrationStatus();
            })
            .catch(function () {
                appendLog('ERROR', 'OnStep calibration: network error');
            });
    }

    els.calibrationStart.addEventListener('click', function () {
        if (!window.confirm('Start the OnStep three-point alignment sequence? The mount will move to its first configured target.')) return;
        requestCalibrationAction('/onstep/alignment/start', { confirmed: true }, 'OnStep alignment started');
    });

    els.calibrationAccept.addEventListener('click', function () {
        if (!window.confirm('Approve this plate-solved point and submit it to OnStep?')) return;
        requestCalibrationAction('/onstep/alignment/accept', null, 'OnStep calibration point approved');
    });

    els.calibrationAbort.addEventListener('click', function () {
        if (!window.confirm('Abort the OnStep alignment sequence?')) return;
        requestCalibrationAction('/onstep/alignment/abort', null, 'OnStep alignment aborted');
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
        fetch('/solve', { method: 'POST' })
            .then(function (r) { return r.json().then(function (data) { return { ok: r.ok, data: data }; }); })
            .then(function (result) {
                if (result.ok && result.data && result.data.ra != null) {
                    updateSolveDisplay(result.data);
                } else if (!result.ok) {
                    appendLog('WARNING', result.data && result.data.error ? result.data.error : 'Solve Now failed');
                }
            })
            .catch(function () { appendLog('ERROR', 'Solve Now: network error'); })
            .finally(function () {
                els.solveNowBtn.disabled = els.modeSelect.value === 'solve';
            });
    });

    // -- Settings panel --

    function loadSettings() {
        fetch('/settings')
            .then(function (r) { return r.ok ? r.json() : null; })
            .then(function (data) {
                if (!data) return;
                if (data.solver) {
                    els.setBackend.value = data.solver.backend || 'astrometry';
                    els.setFovEstimate.value = data.solver.fovEstimateDeg != null ? data.solver.fovEstimateDeg : 34.3;
                    els.setHintTimeout.value = data.solver.hintTimeout || 10;
                    els.setSolveRadius.value = data.solver.solveRadius || 20;
                    els.setTetra3Index.value = (data.solver.tetra3 && data.solver.tetra3.indexPath) || '';
                    els.setAstrometryIndex.value = (data.solver.astrometry && data.solver.astrometry.indexPath) || '';
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
                FovEstimateDeg: els.setFovEstimate.value,
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

        if (els.setTetra3Index.value)
            payload['Solver:Tetra3'] = { IndexPath: els.setTetra3Index.value };
        if (els.setAstrometryIndex.value)
            payload['Solver:Astrometry'] = { IndexPath: els.setAstrometryIndex.value };

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

    // -- Software update --

    function loadUpdateStatus() {
        fetch('/system/update/check')
            .then(function (r) { return r.ok ? r.json() : null; })
            .then(function (data) {
                if (!data) return;
                els.updateCurrent.textContent = data.currentVersion || '--';
                if (data.hasUpdate) {
                    els.updateLatest.textContent = data.latestVersion + ' ✓ available';
                    els.updateLatest.className = 'update-available';
                    els.updateBtn.style.display = '';
                    els.updateBadge.style.display = '';
                } else {
                    els.updateLatest.textContent = data.latestVersion
                        ? data.latestVersion + ' (up to date)'
                        : (data.currentVersion === 'dev' ? 'dev build' : 'up to date');
                    els.updateLatest.className = '';
                    els.updateBtn.style.display = 'none';
                    els.updateBadge.style.display = 'none';
                }
            })
            .catch(function () {
                els.updateLatest.textContent = 'check failed (no internet?)';
            });
    }

    els.updateBtn.addEventListener('click', function () {
        els.updateBtn.disabled = true;
        els.updateMsg.textContent = 'Downloading update…';
        els.updateMsg.className = 'settings-msg';

        fetch('/system/update', { method: 'POST' })
            .then(function (r) { return r.json(); })
            .then(function () {
                els.updateMsg.textContent = 'Installing… dashboard will reconnect shortly.';
                // The service will restart; the existing WS reconnect logic handles it automatically.
            })
            .catch(function () {
                els.updateMsg.textContent = 'Update failed.';
                els.updateMsg.className = 'settings-msg err';
                els.updateBtn.disabled = false;
            });
    });

    // Header badge: open the section then scroll to it
    els.updateBadge.addEventListener('click', function (e) {
        e.preventDefault();
        els.updateSection.open = true;
        els.updateSection.scrollIntoView({ behavior: 'smooth' });
    });

    // Reload update status each time the section is opened
    els.updateSection.addEventListener('toggle', function () {
        if (els.updateSection.open) loadUpdateStatus();
    });

    // -- Init --

    // Initial status fetch
    pollStatus();

    // Load settings panel values
    loadSettings();

    // Start WebSocket connection
    connectWebSocket();
})();
