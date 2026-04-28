// Please see documentation at https://learn.microsoft.com/aspnet/core/client-side/bundling-and-minification
// for details on configuring this project to bundle and minify static web assets.

// Write your JavaScript code.
(() => {
    const source = document.querySelector('[data-chat-unread-source]');
    if (!source) {
        return;
    }

    const formatCount = (count) => count > 99 ? '99+' : String(count);

    const readCount = (value) => {
        const parsed = Number.parseInt(value ?? '0', 10);
        return Number.isFinite(parsed) && parsed > 0 ? parsed : 0;
    };

    const isChatPath = (link, path) => {
        try {
            return new URL(link.href, window.location.origin).pathname.toLowerCase() === path;
        } catch {
            return false;
        }
    };

    const setLinkBadge = (link, count) => {
        link.classList.add('chat-unread-link');
        let badge = link.querySelector('[data-chat-unread-badge]');
        if (count <= 0) {
            badge?.remove();
            return;
        }

        if (!badge) {
            badge = document.createElement('span');
            badge.className = 'chat-unread-badge';
            badge.dataset.chatUnreadBadge = '';
            link.appendChild(badge);
        }

        badge.textContent = formatCount(count);
        badge.setAttribute('aria-label', `${count} unread chats`);
    };

    const applyCounts = (userUnreadChats, adminUnreadChats) => {
        source.dataset.userUnread = String(userUnreadChats);
        source.dataset.adminUnread = String(adminUnreadChats);

        document.querySelectorAll('a[href]').forEach((link) => {
            if (isChatPath(link, '/chats')) {
                setLinkBadge(link, userUnreadChats);
                return;
            }

            if (isChatPath(link, '/admin/chats')) {
                setLinkBadge(link, adminUnreadChats);
            }
        });
    };

    const poll = async () => {
        try {
            const response = await fetch('/api/chats/unread-counts', {
                method: 'GET',
                credentials: 'same-origin',
                headers: {
                    'X-Requested-With': 'XMLHttpRequest'
                }
            });
            if (!response.ok) {
                return;
            }

            const payload = await response.json();
            if (payload?.success !== true) {
                return;
            }

            applyCounts(readCount(payload.userUnreadChats), readCount(payload.adminUnreadChats));
        } catch {
        } finally {
            window.setTimeout(poll, 5000);
        }
    };

    applyCounts(readCount(source.dataset.userUnread), readCount(source.dataset.adminUnread));
    window.setTimeout(poll, 1500);
})();

(() => {
    const panel = document.querySelector('[data-global-sale-action-panel]');
    if (!panel) {
        return;
    }

    const panelStatus = panel.querySelector('[data-global-sale-action-panel-status]');
    const panelMessage = panel.querySelector('[data-global-sale-action-panel-message]');
    const accountOffersLink = panel.querySelector('[data-global-sale-account-offers-link]');
    const offerLink = panel.querySelector('[data-global-sale-action-offer-link]');
    const cancelForm = panel.querySelector('[data-global-sale-cancel-form]');
    const cancelOperationId = panel.querySelector('[data-global-sale-cancel-operation-id]');
    const cancelButton = panel.querySelector('[data-global-sale-cancel-button]');
    const panelTitle = panel.querySelector('[data-global-sale-action-panel-title]');
    const blockableForms = Array.from(document.querySelectorAll('[data-trade-blockable-form]'));
    let hasActiveTrade = false;
    let hadActiveTrade = false;

    const escapeHtml = (value) => String(value ?? '')
        .replace(/&/g, '&amp;')
        .replace(/</g, '&lt;')
        .replace(/>/g, '&gt;')
        .replace(/"/g, '&quot;')
        .replace(/'/g, '&#39;');

    const setPanelMessage = (message, state) => {
        if (!panelMessage) {
            return;
        }

        panelMessage.hidden = false;
        panelMessage.dataset.state = state;
        panelMessage.textContent = message;
    };

    const clearPanelMessage = () => {
        if (!panelMessage) {
            return;
        }

        panelMessage.hidden = true;
        panelMessage.textContent = '';
        delete panelMessage.dataset.state;
    };

    const setBlockableForms = (blocked) => {
        hasActiveTrade = blocked;
        for (const form of blockableForms) {
            const button = form.querySelector('button[type="submit"]');
            if (!button) {
                continue;
            }

            if (!button.dataset.originalText) {
                button.dataset.originalText = button.textContent || 'Submit';
            }

            button.disabled = blocked;
            button.textContent = blocked ? 'Trade in progress' : button.dataset.originalText;
            button.title = blocked
                ? (form.dataset.tradeBlockMessage || 'Finish or cancel the active trade offer first.')
                : '';
        }
    };

    const setLinkEnabled = (link, enabled) => {
        if (!link) {
            return;
        }

        link.dataset.disabled = enabled ? 'false' : 'true';
        link.setAttribute('aria-disabled', enabled ? 'false' : 'true');
        link.tabIndex = enabled ? 0 : -1;
    };

    const updatePanel = (operations) => {
        const activeTrade = operations[0];
        setBlockableForms(Boolean(activeTrade));
        if (!activeTrade) {
            panel.hidden = true;
            return;
        }

        panel.hidden = false;
        const hasOffer = Boolean(activeTrade.tradeOfferId);
        if (offerLink) {
            offerLink.href = hasOffer ? (activeTrade.steamOfferUrl || '#') : '#';
            setLinkEnabled(offerLink, hasOffer);
        }

        if (accountOffersLink && activeTrade.accountTradeOffersUrl) {
            accountOffersLink.href = activeTrade.accountTradeOffersUrl;
        }

        const isDelivery = activeTrade.flow === 'delivery';
        const isActionRequired = activeTrade.status === 'AwaitingUserAction' || activeTrade.status === 'AwaitingBuyerAction';
        if (panelTitle) {
            panelTitle.textContent = isActionRequired
                ? 'Steam offer needs action'
                : 'Active trade in progress';
        }

        if (panelStatus) {
            const flowText = isDelivery ? 'Purchase' : 'Sale';
            const actionText = isActionRequired
                ? (isDelivery ? 'accept the delivery offer' : 'accept the intake offer')
                : 'waiting for the next Steam step';
            panelStatus.textContent = `${flowText}: ${activeTrade.itemName || 'Steam offer'} - ${activeTrade.statusText || activeTrade.status}. ${actionText}.`;
        }

        if (cancelOperationId) {
            cancelOperationId.value = activeTrade.id || '';
        }

        if (cancelButton) {
            cancelButton.disabled = activeTrade.canCancel !== true;
            cancelButton.title = activeTrade.canCancel === true
                ? ''
                : 'Cancel is available only for sale intake offers.';
        }
    };

    const getLatestOperationsByAsset = (operations) => {
        const latest = new Map();
        for (const operation of operations) {
            if (!operation.assetId) {
                continue;
            }

            const current = latest.get(operation.assetId);
            const currentTime = current?.updatedAtUtc ? Date.parse(current.updatedAtUtc) : 0;
            const nextTime = operation.updatedAtUtc ? Date.parse(operation.updatedAtUtc) : 0;
            if (!current || nextTime >= currentTime) {
                latest.set(operation.assetId, operation);
            }
        }

        return latest;
    };

    const isCurrentInventoryStatus = (status) => {
        return status !== 'Credited' && status !== 'Failed';
    };

    const hasVisibleInventoryCompletion = (operations) => {
        const completedAssetIds = new Set(
            operations
                .filter((operation) =>
                    operation.flow === 'intake' &&
                    (operation.status === 'Credited' || operation.status === 'Failed') &&
                    operation.assetId)
                .map((operation) => operation.assetId));
        if (completedAssetIds.size === 0) {
            return false;
        }

        return Array.from(document.querySelectorAll('[data-inventory-sale-row]')).some((row) => {
            const assetIds = String(row.dataset.assetIds || '').split('|').filter(Boolean);
            return assetIds.some((assetId) => completedAssetIds.has(assetId));
        });
    };

    const updateVisibleTradeRows = (operations) => {
        const operationList = Array.isArray(operations) ? operations : [];
        const latestByAsset = getLatestOperationsByAsset(operationList);
        const byFlowAndId = new Map(operationList.map((operation) => [
            `${operation.flow}:${String(operation.id).toLowerCase()}`,
            operation
        ]));

        document.querySelectorAll('[data-trade-flow-row]').forEach((row) => {
            const key = `${row.dataset.tradeFlow}:${String(row.dataset.operationId || '').toLowerCase()}`;
            const operation = byFlowAndId.get(key);
            if (!operation) {
                return;
            }

            const status = row.querySelector('[data-trade-flow-status]');
            if (status) {
                status.dataset.status = operation.status || '';
                status.textContent = operation.statusText || operation.status || '';
            }
        });

        document.querySelectorAll('[data-inventory-sale-row]').forEach((row) => {
            const assetIds = String(row.dataset.assetIds || '').split('|').filter(Boolean);
            const rowOperations = assetIds
                .map((assetId) => latestByAsset.get(assetId))
                .filter((operation) =>
                    operation?.flow === 'intake' &&
                    isCurrentInventoryStatus(operation.status));
            if (rowOperations.length === 0) {
                return;
            }

            const statusCell = row.querySelector('[data-trade-flow-status-cell]');
            if (!statusCell) {
                return;
            }

            const badges = rowOperations.map((operation) =>
                `<span class="status-badge" data-status="${escapeHtml(operation.status)}">${escapeHtml(operation.statusText || operation.status)} x1</span>`);
            statusCell.innerHTML = `<div class="inventory-status inventory-status-summary">${badges.join('')}</div>`;
        });
    };

    const poll = async () => {
        try {
            const response = await fetch('/api/sales/status', {
                method: 'GET',
                credentials: 'same-origin',
                headers: {
                    'X-Requested-With': 'XMLHttpRequest'
                }
            });

            if (response.status === 401 || response.status === 404) {
                panel.hidden = true;
                return;
            }

            if (!response.ok) {
                throw new Error(`HTTP ${response.status}`);
            }

            const payload = await response.json();
            if (payload?.success !== true) {
                return;
            }

            const activeOperations = Array.isArray(payload.operations) ? payload.operations : [];
            const recentOperations = Array.isArray(payload.recentOperations) ? payload.recentOperations : activeOperations;
            const shouldRefreshInventory = hadActiveTrade &&
                activeOperations.length === 0 &&
                hasVisibleInventoryCompletion(recentOperations);
            updateVisibleTradeRows(recentOperations);
            updatePanel(activeOperations);
            hadActiveTrade = activeOperations.length > 0;
            if (shouldRefreshInventory) {
                window.setTimeout(() => window.location.reload(), 350);
            }
        } catch {
        } finally {
            window.setTimeout(poll, 2000);
        }
    };

    cancelForm?.addEventListener('submit', async (event) => {
        event.preventDefault();
        if (!cancelOperationId?.value) {
            setPanelMessage('Sale operation is not ready to cancel yet.', 'failed');
            return;
        }

        if (!window.confirm('Cancel this Steam offer and release the sale operation?')) {
            return;
        }

        clearPanelMessage();
        if (cancelButton) {
            cancelButton.disabled = true;
            cancelButton.textContent = 'Canceling...';
        }

        try {
            const response = await fetch(cancelForm.action, {
                method: 'POST',
                body: new FormData(cancelForm),
                credentials: 'same-origin',
                headers: {
                    'X-Requested-With': 'XMLHttpRequest'
                }
            });
            const payload = await response.json().catch(() => null);
            if (!response.ok || payload?.success !== true) {
                throw new Error(payload?.message || 'Could not cancel trade offer.');
            }

            setPanelMessage(payload.message || 'Trade offer was canceled.', 'success');
            window.setTimeout(() => window.location.reload(), 700);
        } catch (error) {
            const message = error instanceof Error ? error.message : 'Could not cancel trade offer.';
            setPanelMessage(message, 'failed');
        } finally {
            if (cancelButton) {
                cancelButton.disabled = false;
                cancelButton.textContent = 'Cancel offer';
            }
        }
    });

    blockableForms.forEach((form) => {
        form.addEventListener('submit', (event) => {
            if (!hasActiveTrade) {
                return;
            }

            event.preventDefault();
            setPanelMessage(form.dataset.tradeBlockMessage || 'Finish or cancel the active trade offer first.', 'failed');
        });
    });

    document.addEventListener('click', (event) => {
        const disabledLink = event.target.closest?.('a[data-disabled="true"]');
        if (!disabledLink) {
            return;
        }

        event.preventDefault();
        setPanelMessage('Steam offer is not created yet. The status will update automatically.', 'failed');
    });

    window.setTimeout(poll, 500);
})();
