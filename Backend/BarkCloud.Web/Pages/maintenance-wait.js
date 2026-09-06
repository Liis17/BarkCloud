(function () {
    const checkIntervalMs = 3000;
    const requiredSuccessCount = 3;
    const maxWaitSeconds = 90;

    function start({ initialDelayMs, pageServerStartedAt, operationId = '', target = '/settings#system' }) {
        let seconds = 0;
        let successCount = 0;
        let checkInProgress = false;
        let redirectStarted = false;
        let timeoutShown = false;
        let operationFailed = false;
        const timer = document.getElementById('timer');
        const waitError = document.getElementById('wait-error');
        const waitErrorMessage = document.getElementById('wait-error-message');

        setInterval(() => {
            seconds++;
            if (timer) timer.textContent = seconds;
            if (seconds >= maxWaitSeconds && !timeoutShown) {
                timeoutShown = true;
                if (waitError) waitError.hidden = false;
            }
        }, 1000);

        function goBack() {
            const hashIndex = target.indexOf('#');
            const path = hashIndex >= 0 ? target.slice(0, hashIndex) : target;
            const hash = hashIndex >= 0 ? target.slice(hashIndex) : '';
            const separator = path.includes('?') ? '&' : '?';
            window.location.replace(path + separator + '_=' + Date.now() + hash);
        }

        function showOperationError(message) {
            operationFailed = true;
            if (waitErrorMessage && message) waitErrorMessage.textContent = message;
            if (waitError) waitError.hidden = false;
        }

        async function readOperationState() {
            if (!operationId) return null;

            try {
                const response = await fetch('/maintenance-status?operationId=' + encodeURIComponent(operationId), {
                    cache: 'no-store',
                    credentials: 'same-origin',
                    redirect: 'manual'
                });
                if (response.status === 204 || !response.ok) return null;
                const status = await response.json();
                return status && typeof status.state === 'string' ? status : null;
            } catch {
                return null;
            }
        }

        async function checkServer() {
            if (checkInProgress || redirectStarted || operationFailed) return;
            checkInProgress = true;

            try {
                const operation = await readOperationState();
                if (operation && operation.state.toLowerCase() === 'failed') {
                    showOperationError(operation.message || 'Операция обслуживания завершилась с ошибкой.');
                    return;
                }

                const response = await fetch('/healthz', {
                    cache: 'no-store',
                    credentials: 'same-origin',
                    redirect: 'manual'
                });
                const serverStartedAt = response.headers.get('X-BarkCloud-Started-At');
                const operationCompleted = operation && operation.state.toLowerCase() === 'completed';
                const healthyRestart = operationId
                    ? operationCompleted
                    : serverStartedAt !== pageServerStartedAt;

                if (response.status === 200 && serverStartedAt && healthyRestart) {
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
                if (!redirectStarted && !operationFailed) setTimeout(checkServer, checkIntervalMs);
            }
        }

        setTimeout(checkServer, initialDelayMs);
    }

    window.BarkCloudWait = { start };
})();
