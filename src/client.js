// DSH serves client exports as classic, lazily materialized module factories.
window.__ModuleLoader__.load({
  id: 'dsh-pet',
  factory(require) {
    const { createElement, useSyncExternalStore } = require('react');
    const slot = 'sidebar.footer.action';
    const locale = 'dsh-pet.visibility';
    const endpoint = '/api/dsh-pet/visibility';
    const copy = {
      zh: {
        show: '显示宠物',
        hide: '隐藏宠物',
        loading: '正在连接桌面宠物',
        retry: '连接宠物失败，点击重试',
      },
      en: {
        show: 'Show pet',
        hide: 'Hide pet',
        loading: 'Connecting to desktop pet',
        retry: 'Could not connect to the pet. Click to retry.',
      },
    };

    const styles = `
      .dsh-pet-visibility {
        display: flex;
        align-items: center;
        justify-content: space-between;
        gap: 8px;
        box-sizing: border-box;
        min-height: 32px;
        width: calc(100% - 16px);
        margin: 1px 8px;
        padding: 3px 8px;
        border: 0;
        border-radius: var(--dsw-radius-md, 10px);
        background: transparent;
        color: var(--dsw-alias-label-primary, inherit);
        font: inherit;
        text-align: left;
        cursor: pointer;
      }
      .dsh-pet-visibility[data-wide="false"] {
        justify-content: center;
        width: 32px;
        margin-inline: 0;
        padding-inline: 2px;
      }
      .dsh-pet-visibility:not(:disabled):hover {
        background: var(--dsw-alias-interactive-bg-hover, rgba(127, 127, 127, .14));
      }
      .dsh-pet-visibility:focus-visible {
        outline: var(--dsw-focus-ring-width, 2px) solid var(--dsw-focus-ring-color, #4d6bfe);
        outline-offset: -2px;
      }
      .dsh-pet-visibility:disabled { cursor: wait; opacity: .65; }
      .dsh-pet-visibility-switch {
        display: inline-flex;
        align-items: center;
        flex: none;
        box-sizing: border-box;
        width: 25px;
        height: 14px;
        border-radius: 9px;
        padding: 2px;
        background: var(--dsw-alias-interactive-bg-hover, rgba(127, 127, 127, .2));
        box-shadow: inset 0 0 0 1px rgba(127, 127, 127, .32);
      }
      .dsh-pet-visibility-switch::before {
        content: '';
        width: 10px;
        height: 10px;
        border-radius: 50%;
        background: var(--dsw-alias-label-secondary, #77808a);
      }
      .dsh-pet-visibility[aria-pressed="true"] .dsh-pet-visibility-switch {
        justify-content: flex-end;
        background: var(--dsw-alias-state-business-primary, #4d6bfe);
        box-shadow: none;
      }
      .dsh-pet-visibility[aria-pressed="true"] .dsh-pet-visibility-switch::before {
        background: white;
      }
    `;

    function createVisibility() {
      const listeners = new Set();
      let snapshot = { visible: null, pending: false, failed: false };
      let inFlight;
      let disposed = false;

      function publish(next) {
        snapshot = next;
        for (const listener of listeners) listener();
      }

      async function update(next) {
        if (disposed || inFlight) return;
        const operation = new AbortController();
        inFlight = operation;
        publish({ ...snapshot, pending: true });
        try {
          const writing = typeof next === 'boolean';
          const signal = AbortSignal.any([operation.signal, AbortSignal.timeout(5000)]);
          const response = await fetch(endpoint, {
            credentials: 'same-origin',
            cache: 'no-store',
            signal,
            ...(writing ? {
              method: 'PUT',
              headers: { 'Content-Type': 'application/json' },
              body: JSON.stringify({ visible: next }),
            } : {}),
          });
          if (!response.ok) throw new Error('Pet visibility unavailable');
          const result = await response.json();
          if (typeof result?.visible !== 'boolean') throw new Error('Invalid pet visibility response');
          if (!disposed) publish({ visible: result.visible, pending: false, failed: false });
        } catch {
          if (!disposed) publish({ ...snapshot, pending: false, failed: true });
        } finally {
          inFlight = undefined;
        }
      }

      return {
        getSnapshot: () => snapshot,
        subscribe(listener) {
          listeners.add(listener);
          return () => { listeners.delete(listener); };
        },
        refresh: () => { void update(); },
        toggle() {
          if (snapshot.visible === null || snapshot.failed) return update();
          return update(!snapshot.visible);
        },
        dispose() {
          disposed = true;
          inFlight?.abort();
          listeners.clear();
        },
      };
    }

    function PetVisibility({ wide, visibility, t }) {
      const { visible, pending, failed } = useSyncExternalStore(visibility.subscribe, visibility.getSnapshot);
      const action = pending ? t('loading') : failed ? t('retry') : t(visible ? 'hide' : 'show');
      return createElement('button', {
        type: 'button',
        className: 'dsh-pet-visibility',
        'aria-label': action,
        'aria-pressed': visible === null ? undefined : visible,
        'aria-busy': pending,
        'data-wide': wide,
        disabled: pending,
        title: action,
        onClick: () => { void visibility.toggle(); },
      },
      wide ? createElement('span', null, action) : null,
      createElement('span', { className: 'dsh-pet-visibility-switch', 'aria-hidden': true }));
    }

    function apply(ctx) {
      if (!('dshDesktop' in globalThis) || !ctx.connection.isLoopback) return;
      const visibility = createVisibility();
      ctx.effect(() => ctx.locale.register(locale, copy), 'dsh-pet: visibility copy');
      ctx.effect(() => {
        const style = document.createElement('style');
        style.textContent = styles;
        document.head.append(style);
        const disconnect = ctx.connection.generation.subscribe(visibility.refresh);
        window.addEventListener('focus', visibility.refresh);
        visibility.refresh();
        return () => {
          style.remove();
          disconnect();
          window.removeEventListener('focus', visibility.refresh);
          visibility.dispose();
        };
      }, 'dsh-pet: visibility lifecycle');
      ctx.slots.inject(slot, () => ctx.slots.register({
        name: slot,
        id: 'dsh-pet-visibility',
        locale,
        inject: () => ({ visibility }),
      }, PetVisibility));
    }

    return { apply, inject: ['slots', 'locale', 'connection'] };
  },
});
