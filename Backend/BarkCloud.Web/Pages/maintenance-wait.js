(function () {
    const checkIntervalMs = 3000;
    const requiredSuccessCount = 3;

    function start({ initialDelayMs, pageServerStartedAt, target = '/settings#system' }) {
        let seconds = 0;
        let successCount = 0;
        let checkInProgress = false;
        let redirectStarted = false;
        const timer = document.getElementById('timer');

        setInterval(() => {
            seconds++;
            if (timer) timer.textContent = seconds;
        }, 1000);

        function goBack() {
            const hashIndex = target.indexOf('#');
            const path = hashIndex >= 0 ? target.slice(0, hashIndex) : target;
            const hash = hashIndex >= 0 ? target.slice(hashIndex) : '';
            const separator = path.includes('?') ? '&' : '?';
            window.location.replace(path + separator + '_=' + Date.now() + hash);
        }

        async function checkServer() {
            if (checkInProgress || redirectStarted) return;
            checkInProgress = true;

            try {
                const response = await fetch('/healthz', {
                    cache: 'no-store',
                    credentials: 'same-origin',
                    redirect: 'manual'
                });
                const serverStartedAt = response.headers.get('X-BarkCloud-Started-At');

                if (response.status === 200 && serverStartedAt && serverStartedAt !== pageServerStartedAt) {
                    successCount++;
                    if (successCount >= requiredSuccessCount) {
                        redirectStarted = true;
                        goBack();
                    }
                } else {
                    successCount = 0;
                }
            } catch {
                successCount = 0;
            } finally {
                checkInProgress = false;
                if (!redirectStarted) setTimeout(checkServer, checkIntervalMs);
            }
        }

        setTimeout(checkServer, initialDelayMs);
    }

    window.BarkCloudWait = { start };
})();
