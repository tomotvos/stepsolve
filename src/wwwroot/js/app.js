// StepSolve dashboard — polls /status and updates display
(function () {
    'use strict';

    const POLL_INTERVAL_MS = 3000;

    const els = {
        ra: document.getElementById('ra'),
        dec: document.getElementById('dec'),
        confidence: document.getElementById('confidence'),
        solver: document.getElementById('solver'),
        solveTime: document.getElementById('solve-time'),
        lastSolveTime: document.getElementById('last-solve-time'),
        state: document.getElementById('state'),
        modeBadge: document.getElementById('mode-badge'),
        themeToggle: document.getElementById('theme-toggle'),
    };

    function formatRa(deg) {
        if (deg == null) return '--';
        const hours = deg / 15.0;
        const h = Math.floor(hours);
        const m = Math.floor((hours - h) * 60);
        const s = Math.floor(((hours - h) * 60 - m) * 60);
        return `${h.toString().padStart(2, '0')}h ${m.toString().padStart(2, '0')}m ${s.toString().padStart(2, '0')}s`;
    }

    function formatDec(deg) {
        if (deg == null) return '--';
        const sign = deg >= 0 ? '+' : '-';
        const v = Math.abs(deg);
        const d = Math.floor(v);
        const m = Math.floor((v - d) * 60);
        const s = Math.floor(((v - d) * 60 - m) * 60);
        return `${sign}${d.toString().padStart(2, '0')}\u00B0 ${m.toString().padStart(2, '0')}' ${s.toString().padStart(2, '0')}"`;
    }

    function updateDisplay(data) {
        els.ra.textContent = formatRa(data.ra);
        els.dec.textContent = formatDec(data.dec);
        els.confidence.textContent = data.confidence != null ? data.confidence.toFixed(2) : '--';
        els.solver.textContent = data.solver || '--';
        els.solveTime.textContent = data.solveTimeMs != null ? `${(data.solveTimeMs / 1000).toFixed(1)}s` : '--';
        els.lastSolveTime.textContent = data.lastSolveTimestamp
            ? new Date(data.lastSolveTimestamp).toLocaleTimeString()
            : '--';
        els.state.textContent = data.state || '--';
        els.modeBadge.textContent = data.mode || '--';
    }

    async function poll() {
        try {
            const resp = await fetch('/status');
            if (resp.ok) {
                updateDisplay(await resp.json());
            }
        } catch {
            // Network error — will retry next interval
        }
    }

    // Night mode toggle
    els.themeToggle.addEventListener('click', () => {
        document.documentElement.classList.toggle('night');
    });

    // Start polling
    poll();
    setInterval(poll, POLL_INTERVAL_MS);
})();
