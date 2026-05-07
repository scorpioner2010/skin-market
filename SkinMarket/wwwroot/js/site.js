// Please see documentation at https://learn.microsoft.com/aspnet/core/client-side/bundling-and-minification
// for details on configuring this project to bundle and minify static web assets.

// Write your JavaScript code.
const appUiText = (() => {
    const element = document.getElementById('app-ui-text');
    if (!element) {
        return {};
    }

    try {
        return JSON.parse(element.textContent || '{}') || {};
    } catch {
        return {};
    }
})();

const appText = (key, fallback, ...args) => {
    const template = appUiText[key] || fallback;
    return args.reduce(
        (text, value, index) => text.replaceAll(`{${index}}`, String(value)),
        template);
};

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
        badge.setAttribute('aria-label', appText('unreadChats', '{0} unread chats', count));
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
                button.dataset.originalText = button.textContent || appText('submit', 'Submit');
            }

            button.disabled = blocked;
            button.textContent = blocked ? appText('tradeInProgress', 'Trade in progress') : button.dataset.originalText;
            button.title = blocked
                ? (form.dataset.tradeBlockMessage || appText('finishActiveTradeFirst', 'Finish or cancel the active trade offer first.'))
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
                ? appText('steamOfferNeedsAction', 'Steam offer needs action')
                : appText('activeTradeInProgress', 'Active trade in progress');
        }

        if (panelStatus) {
            const flowText = isDelivery ? appText('purchase', 'Purchase') : appText('sale', 'Sale');
            const detailText = activeTrade.detailText || (
                isActionRequired
                    ? (isDelivery ? appText('acceptDeliveryOffer', 'accept the delivery offer') : appText('acceptIntakeOffer', 'accept the intake offer'))
                    : appText('waitingNextSteamStep', 'waiting for the next Steam step'));
            panelStatus.textContent = `${flowText}: ${activeTrade.itemName || appText('steamOffer', 'Steam offer')} - ${activeTrade.statusText || activeTrade.status}. ${detailText}.`;
        }

        if (cancelOperationId) {
            cancelOperationId.value = activeTrade.id || '';
        }

        if (cancelButton) {
            cancelButton.disabled = activeTrade.canCancel !== true;
            cancelButton.title = activeTrade.canCancel === true
                ? ''
                : appText('cancelOnlySaleIntake', 'Cancel is available only for sale intake offers.');
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
            setPanelMessage(appText('saleOperationNotReadyCancel', 'Sale operation is not ready to cancel yet.'), 'failed');
            return;
        }

        if (!window.confirm(appText('cancelConfirm', 'Cancel this Steam offer and release the sale operation?'))) {
            return;
        }

        clearPanelMessage();
        if (cancelButton) {
            cancelButton.disabled = true;
            cancelButton.textContent = appText('canceling', 'Canceling...');
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
                throw new Error(payload?.message || appText('couldNotCancelTrade', 'Could not cancel trade offer.'));
            }

            setPanelMessage(payload.message || appText('tradeOfferCanceled', 'Trade offer was canceled.'), 'success');
            window.setTimeout(() => window.location.reload(), 700);
        } catch (error) {
            const message = error instanceof Error ? error.message : appText('couldNotCancelTrade', 'Could not cancel trade offer.');
            setPanelMessage(message, 'failed');
        } finally {
            if (cancelButton) {
                cancelButton.disabled = false;
                cancelButton.textContent = appText('cancelOffer', 'Cancel offer');
            }
        }
    });

    blockableForms.forEach((form) => {
        form.addEventListener('submit', (event) => {
            if (!hasActiveTrade) {
                return;
            }

            event.preventDefault();
            setPanelMessage(form.dataset.tradeBlockMessage || appText('finishActiveTradeFirst', 'Finish or cancel the active trade offer first.'), 'failed');
        });
    });

    document.addEventListener('click', (event) => {
        const disabledLink = event.target.closest?.('a[data-disabled="true"]');
        if (!disabledLink) {
            return;
        }

        event.preventDefault();
        setPanelMessage(appText('steamOfferNotCreated', 'Steam offer is not created yet. The status will update automatically.'), 'failed');
    });

window.setTimeout(poll, 500);
})();

(() => {
    const deferredRoots = Array.from(document.querySelectorAll('[data-deferred-page]'));
    if (deferredRoots.length === 0) {
        return;
    }

    const currentUrl = new URL(window.location.href);
    const hasDeferredParam = currentUrl.searchParams.get('deferred') === '1';
    const pendingRoot = deferredRoots.find((root) => root.dataset.deferredLoaded === 'false');

    if (pendingRoot) {
        const nextUrl = new URL(window.location.href);
        nextUrl.searchParams.set('deferred', '1');
        window.setTimeout(() => {
            window.location.replace(nextUrl.toString());
        }, 90);
        return;
    }

    if (!hasDeferredParam) {
        return;
    }

    currentUrl.searchParams.delete('deferred');
    window.history.replaceState(
        window.history.state,
        document.title,
        `${currentUrl.pathname}${currentUrl.search}${currentUrl.hash}`);

    const animatedItems = deferredRoots.flatMap((root) => Array.from(root.querySelectorAll([
        '.market-item-card',
        '.market-table-row:not(.market-table-header)',
        '.inventory-row:not(.inventory-row-header)'
    ].join(','))));

    animatedItems.forEach((item, index) => {
        item.classList.add('deferred-stagger-item');
        item.style.setProperty('--stagger-index', String(Math.min(index, 18)));
        item.addEventListener('animationend', () => {
            item.classList.remove('deferred-stagger-item', 'is-visible');
            item.style.removeProperty('--stagger-index');
        }, { once: true });
    });

    window.requestAnimationFrame(() => {
        animatedItems.forEach((item) => item.classList.add('is-visible'));
    });
})();

(() => {
    const menus = Array.from(document.querySelectorAll('[data-price-source-menu]'));
    if (menus.length === 0) {
        return;
    }

    const isTouchLike = () => window.matchMedia?.('(hover: none), (pointer: coarse)').matches === true;

    const setOpen = (menu, isOpen) => {
        menu.classList.toggle('is-open', isOpen);
        menu.closest('.market-table-row, .inventory-row')?.classList.toggle('has-open-price-menu', isOpen);
        const trigger = menu.querySelector('.price-source-trigger');
        const list = menu.querySelector('.price-source-list');
        trigger?.setAttribute('aria-expanded', isOpen ? 'true' : 'false');
        list?.setAttribute('aria-hidden', isOpen ? 'false' : 'true');
    };

    const closeOthers = (activeMenu = null) => {
        menus.forEach((menu) => {
            if (menu !== activeMenu) {
                setOpen(menu, false);
            }
        });
    };

    menus.forEach((menu) => {
        const trigger = menu.querySelector('.price-source-trigger');
        if (!trigger) {
            return;
        }

        trigger.addEventListener('click', (event) => {
            if (!isTouchLike()) {
                return;
            }

            event.preventDefault();
            event.stopPropagation();
            const shouldOpen = !menu.classList.contains('is-open');
            closeOthers(menu);
            setOpen(menu, shouldOpen);
        });
    });

    document.addEventListener('click', (event) => {
        if (event.target.closest?.('[data-price-source-menu]')) {
            return;
        }

        closeOthers();
    });

    document.addEventListener('keydown', (event) => {
        if (event.key === 'Escape') {
            closeOthers();
        }
    });
})();

(() => {
    const tableRoots = Array.from(document.querySelectorAll('.market-table-list, .inventory-list, .history-table'));
    if (tableRoots.length === 0) {
        return;
    }

    const headerClasses = ['market-table-header', 'inventory-row-header', 'history-table-header'];

    const findHeader = (root) => Array.from(root.children)
        .find((child) => headerClasses.some((className) => child.classList.contains(className)));

    const getRows = (root, header) => Array.from(root.children)
        .filter((child) => child !== header && (
            child.classList.contains('market-table-row') ||
            child.classList.contains('inventory-row') ||
            child.classList.contains('history-table-row')));

    const getCells = (row) => {
        if (row.classList.contains('history-table-row')) {
            return Array.from(row.children).filter((child) => !child.classList.contains('history-row-note'));
        }

        return Array.from(row.children)
            .filter((child) =>
                child.classList.contains('market-table-cell') ||
                child.classList.contains('inventory-cell'));
    };

    const normalizeLabel = (value) => String(value || '').trim().toLowerCase();

    const parseNumber = (value) => {
        const cleaned = String(value || '')
            .replace(/\s+/g, '')
            .replace(/[$€£,%]/g, '')
            .replace(/[^0-9.,+\-]/g, '');
        if (!cleaned || !/[0-9]/.test(cleaned)) {
            return null;
        }

        const decimalNormalized = cleaned.includes(',') && !cleaned.includes('.')
            ? cleaned.replace(',', '.')
            : cleaned.replace(/,/g, '');
        const parsed = Number.parseFloat(decimalNormalized);
        return Number.isFinite(parsed) ? parsed : null;
    };

    const parseDateValue = (value) => {
        const parsed = Date.parse(String(value || '').trim());
        return Number.isFinite(parsed) ? parsed : null;
    };

    const readComparable = (row, columnIndex, label) => {
        const cell = getCells(row)[columnIndex];
        const text = (cell?.textContent || '').trim();
        const normalizedLabel = normalizeLabel(label);
        const numericLabel = /(price|ціна|цена|balance|баланс|quantity|qty|кільк|колич|credit|кредит|amount|сума|сумма|records|step|confidence|http|duration|asset id)/i.test(normalizedLabel);
        const dateLabel = /(time|updated|created|date|attempt|час|дата|онов|обнов|создан|створ)/i.test(normalizedLabel);

        if (dateLabel) {
            const dateValue = parseDateValue(text);
            if (dateValue !== null) {
                return { type: 'number', value: dateValue, empty: false };
            }
        }

        if (numericLabel || /^[$€£]?\s*[-+]?\d/.test(text)) {
            const numberValue = parseNumber(text);
            if (numberValue !== null) {
                return { type: 'number', value: numberValue, empty: false };
            }
        }

        return { type: 'text', value: text.toLocaleLowerCase(), empty: text.length === 0 || text === '-' };
    };

    const compareValues = (left, right, direction) => {
        if (left.empty !== right.empty) {
            return left.empty ? 1 : -1;
        }

        let comparison;
        if (left.type === 'number' && right.type === 'number') {
            comparison = left.value - right.value;
        } else {
            comparison = String(left.value).localeCompare(String(right.value), undefined, {
                numeric: true,
                sensitivity: 'base'
            });
        }

        return direction === 'desc' ? -comparison : comparison;
    };

    const clearSortState = (buttons) => {
        buttons.forEach((button) => {
            button.classList.remove('is-asc', 'is-desc');
            button.setAttribute('aria-pressed', 'false');
        });
    };

    const animateSortedRows = (rows) => {
        rows.forEach((row) => {
            row.classList.remove('table-sort-settle-item');
            row.style.removeProperty('--sort-index');
        });

        window.requestAnimationFrame(() => {
            rows.forEach((row, index) => {
                row.style.setProperty('--sort-index', String(Math.min(index, 18)));
                row.classList.add('table-sort-settle-item');
                row.addEventListener('animationend', () => {
                    row.classList.remove('table-sort-settle-item');
                    row.style.removeProperty('--sort-index');
                }, { once: true });
            });
        });
    };

    tableRoots.forEach((root) => {
        const header = findHeader(root);
        if (!header) {
            return;
        }

        const headerCells = getCells(header);
        if (headerCells.length === 0) {
            return;
        }

        const buttons = [];
        headerCells.forEach((cell, index) => {
            const label = (cell.textContent || '').trim();
            if (!label || cell.classList.contains('market-table-actions-cell') || cell.classList.contains('inventory-actions-cell')) {
                return;
            }

            cell.textContent = '';
            const button = document.createElement('button');
            button.type = 'button';
            button.className = 'table-sort-button';
            button.textContent = label;
            button.dataset.sortIndex = String(index);
            button.dataset.sortLabel = label;
            button.setAttribute('aria-pressed', 'false');
            cell.appendChild(button);
            buttons.push(button);

            button.addEventListener('click', () => {
                const previousIndex = root.dataset.sortIndex;
                const previousDirection = root.dataset.sortDirection || 'asc';
                const direction = previousIndex === String(index) && previousDirection === 'asc' ? 'desc' : 'asc';
                const rows = getRows(root, header);

                const sortedRows = rows
                    .map((row, originalIndex) => ({
                        row,
                        originalIndex,
                        comparable: readComparable(row, index, label)
                    }))
                    .sort((left, right) => {
                        const comparison = compareValues(left.comparable, right.comparable, direction);
                        return comparison === 0 ? left.originalIndex - right.originalIndex : comparison;
                    });

                sortedRows.forEach((item) => root.appendChild(item.row));
                animateSortedRows(sortedRows.map((item) => item.row));

                root.dataset.sortIndex = String(index);
                root.dataset.sortDirection = direction;
                clearSortState(buttons);
                button.classList.add(direction === 'asc' ? 'is-asc' : 'is-desc');
                button.setAttribute('aria-pressed', 'true');
            });
        });
    });
})();
