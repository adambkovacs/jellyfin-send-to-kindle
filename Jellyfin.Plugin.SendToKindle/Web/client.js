(() => {
    'use strict';

    const buttonId = 'sendToKindleButton';
    let timer;
    let dialog;
    let view;

    function bookId() {
        const hash = window.location.hash;
        return new URLSearchParams(hash.split('?')[1]).get('id')?.replace(/-/g, '').toLowerCase();
    }

    function request(path, body) {
        return window.ApiClient.ajax({
            type: body ? 'POST' : 'GET',
            url: window.ApiClient.getUrl('SendToKindle/' + path),
            dataType: 'json',
            ...(body && { contentType: 'application/json', data: JSON.stringify(body) })
        });
    }

    function element(tag, text) {
        const node = document.createElement(tag);
        node.textContent = text;
        return node;
    }

    function selectField(form, title, choices) {
        const label = element('label', title + ' ');
        const select = document.createElement('select');
        for (const choice of choices) select.add(new Option(choice.label, choice.id));
        label.append(select);
        form.append(label);
        return select;
    }

    const statuses = {
        queued: 'Queued',
        submitting: 'Submitting to mail provider',
        smtp_accepted: 'Accepted by mail provider',
        failed: 'Delivery failed',
        unknown: 'Delivery status unknown. Check with the administrator before sending again.'
    };

    async function choose(id, userId, button) {
        button.disabled = true;
        try {
            const options = await request('Books/' + id + '/Options');
            if (bookId() !== id || !button.isConnected) return;
            if (!options.devices?.length) throw new Error('No devices are available.');
            const box = document.createElement('dialog');
            dialog = box;
            box.style.cssText = 'background:#242424;color:white;padding:1.5rem;border:0;border-radius:.5rem;max-width:85vw';
            const form = document.createElement('form');
            form.style.cssText = 'display:grid;gap:1rem';
            form.append(element('h2', 'Send to Kindle'));
            const device = selectField(form, 'Device', options.devices);
            const formats = options.formats?.length ? options.formats : ['original'];
            const format = selectField(form, 'Format', formats.map(value => ({ id: value, label: value })));
            const message = element('div', '');
            message.setAttribute('role', 'status');
            const send = element('button', 'Send');
            const close = element('button', 'Close');
            close.type = 'button';
            close.onclick = () => box.close();
            box.onclose = () => { box.remove(); if (dialog === box) dialog = null; };
            form.append(message, send, close);
            box.append(form);
            document.body.append(box);
            box.showModal();

            const choiceKey = () => ['sendToKindle', window.ApiClient.getUrl('SendToKindle'), userId, id, device.value, format.value].join('/');
            const showAction = () => {
                send.textContent = sessionStorage.getItem(choiceKey()) ? 'Check previous request' : 'Send';
            };
            device.onchange = format.onchange = showAction;
            showAction();
            let receipt;
            form.onsubmit = async event => {
                event.preventDefault();
                if (send.disabled || bookId() !== id) return;
                const storageKey = choiceKey();
                const previousKey = sessionStorage.getItem(storageKey);
                send.disabled = device.disabled = format.disabled = true;
                try {
                    if (!receipt) {
                        let key = previousKey;
                        if (!key) {
                            const bytes = crypto.getRandomValues(new Uint8Array(16));
                            bytes[6] = (bytes[6] & 15) | 64;
                            bytes[8] = (bytes[8] & 63) | 128;
                            const hex = Array.from(bytes, b => b.toString(16).padStart(2, '0')).join('');
                            key = [hex.slice(0,8), hex.slice(8,12), hex.slice(12,16), hex.slice(16,20), hex.slice(20)].join('-');
                            sessionStorage.setItem(storageKey, key);
                        }
                        receipt = await request('Jobs', { bookId: id, deviceId: device.value, format: format.value, idempotencyKey: key });
                    }
                    while (box.open) {
                        message.textContent = statuses[receipt.status] || 'Unknown status';
                        if (!['queued', 'submitting'].includes(receipt.status)) {
                            sessionStorage.removeItem(storageKey);
                            break;
                        }
                        await new Promise(resolve => setTimeout(resolve, 2000));
                        if (box.open) receipt = await request('Jobs/' + encodeURIComponent(receipt.jobId));
                    }
                    send.hidden = true;
                } catch (error) {
                    const code = error?.status ?? error?.statusCode;
                    if (!receipt && [400, 401, 403, 404, 409, 422].includes(code)) {
                        if (!previousKey) sessionStorage.removeItem(storageKey);
                        message.textContent = {
                            400: 'The request was invalid. Close and reopen the chooser.',
                            401: 'Authorization failed. Check your session and the delivery service settings.',
                            403: 'Your account cannot send this book.',
                            404: 'This book is no longer available to send.',
                            409: 'The request conflicts with an earlier send. Check the earlier job before trying again.',
                            422: 'This device or format is no longer available. Close and reopen to refresh the choices.'
                        }[code];
                        send.hidden = true;
                        return;
                    }
                    message.textContent = receipt ? 'Could not check the job status.' : 'No receipt received. Retry to check this request.';
                    send.textContent = receipt ? 'Check status' : 'Retry';
                    send.disabled = false;
                }
            };
        } catch (error) {
            window.alert(error?.message || 'Could not load delivery options.');
        } finally {
            button.disabled = false;
        }
    }

    async function refresh() {
        const id = bookId();
        const page = document.querySelector('.itemDetailPage:not(.hide)');
        const actions = page?.querySelector('.mainDetailButtons, .detailButtons');
        const apiClient = window.ApiClient;
        const userId = apiClient?.getCurrentUserId?.();
        if (view && view.id === id && view.actions === actions && view.apiClient === apiClient && view.userId === userId) {
            if (view.button && !actions.contains(view.button)) actions.append(view.button);
            return;
        }
        view?.button?.remove();
        dialog?.close();
        view = null;
        if (!id || !actions || !apiClient) return;
        const current = { id, actions, apiClient, userId };
        view = current;
        const isCurrent = () => view === current && bookId() === id && actions.isConnected
            && window.ApiClient === apiClient && apiClient.getCurrentUserId?.() === userId;
        try {
            const user = await apiClient.getCurrentUser();
            if (!user?.Id || !isCurrent()) return;
            const item = await apiClient.getItem(user.Id, id);
            if (item?.Type !== 'Book' || !isCurrent()) return;
            if (!user.Policy?.IsAdministrator) {
                const options = await request('Books/' + id + '/Options');
                if (!options.devices?.length || !isCurrent()) return;
            }
            const button = element('button', 'Send to Kindle');
            button.id = buttonId;
            button.dataset.book = id;
            button.type = 'button';
            button.className = 'button-flat btnAction emby-button';
            button.onclick = () => choose(id, user.Id, button);
            current.button = button;
            actions.append(button);
        } catch { /* The session or item may no longer be accessible. */ }
    }

    function scheduleRefresh() {
        clearTimeout(timer);
        timer = setTimeout(refresh, 100);
    }
    window.addEventListener('hashchange', scheduleRefresh);
    new MutationObserver(scheduleRefresh).observe(document.body, { childList: true, subtree: true });
    scheduleRefresh();
})();
