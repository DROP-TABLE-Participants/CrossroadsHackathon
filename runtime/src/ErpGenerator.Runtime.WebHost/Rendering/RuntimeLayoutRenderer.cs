using System.Net;
using System.Text.Json;
using ErpGenerator.Runtime.Shared.Tenants;
using static RuntimeHtml;

internal static class RuntimeLayoutRenderer
{
    public static string ShellPage(
        string title,
        ResolvedTenantEnvironment tenant,
        RuntimeSession session,
        IReadOnlyList<RuntimeEntityDefinition> entities,
        string body)
    {
        var company = tenant.TenantSlug == "demo-" + tenant.TenantStableId ? "Dandelion Chocolate" : RuntimeUi.HumanizeTenant(tenant.TenantSlug);
        var guest = session.Email.EndsWith("@demo.invalid", StringComparison.Ordinal);
        var displayName = guest ? "Guest" : session.Email;
        var signOut = guest ? "" : "<a class=\"icon-button\" href=\"/logout\" aria-label=\"Sign out\">" + RuntimeUi.Icon("logout", "") + "</a>";
        var records = string.Join("", entities.Select(entity =>
            NavLink($"/entities/{WebUtility.UrlEncode(entity.Key)}", RuntimeUi.EntityIconName(entity.Key, entity.DisplayName), entity.DisplayName)));
        var recordsSection = entities.Count > 0
            ? $$"""
                <section class="nav-group" data-nav-group="records" data-expanded="true">
                  <button class="nav-group-toggle" type="button" aria-expanded="true" aria-controls="records-navigation">
                    <span class="nav-group-label">Records</span>
                    <span class="nav-group-count">{{entities.Count}}</span>
                    {{RuntimeUi.Icon("chevron-down", "nav-group-chevron")}}
                  </button>
                  <div class="nav-group-links" id="records-navigation">
                    {{records}}
                  </div>
                </section>
                """
            : "";

        // Command-palette destinations for the global search dropdown.
        var searchItems = new List<object>
        {
            SearchItem("Dashboard", "/", "Pages", "dashboard"),
            SearchItem("Reports", "/reports", "Pages", "reports"),
            SearchItem("Tasks", "/tasks", "Pages", "tasks"),
            SearchItem("Notifications", "/notifications", "Pages", "bell"),
            SearchItem("Users", "/settings/users", "Pages", "users"),
            SearchItem("Deployment", "/settings/deployment", "Pages", "settings"),
        };
        searchItems.AddRange(entities.Select(entity =>
            SearchItem(entity.DisplayName, $"/entities/{WebUtility.UrlEncode(entity.Key)}", "Records", RuntimeUi.EntityIconName(entity.Key, entity.DisplayName))));
        var searchJson = JsonSerializer.Serialize(searchItems);
        var tourSteps = new List<object>
        {
            TourStep("/", ".page-head", "Your dashboard", "This is the starting point for your ERP. It brings the most important operational information and tools into one place.", padding: 0),
            TourStep("/", ".erp-assistant", "Meet ErpGenerator AI", "Ask questions about your ERP in plain language. ErpGenerator AI reads only the business data you are allowed to access and gives you a grounded summary."),
            TourStep("/", ".analytics-grid .chart-panel:first-child", "Live business charts", "These charts update from your ERP records so you can see volume, distribution, and recent activity without building a report first.", placement: "right"),
            TourStep("/", ".sidebar", "Move through your ERP", "Use the navigation to open reports, tasks, notifications, administration, and every generated business page."),
            TourStep("/", ".topbar-search", "Find anything quickly", "Search pages and record types from anywhere in the ERP."),
        };
        if (entities.FirstOrDefault() is { } firstEntity)
        {
            tourSteps.Add(TourStep(
                $"/entities/{WebUtility.UrlEncode(firstEntity.Key)}",
                ".page-actions .button[href$='/new']",
                "Create business records",
                $"Create buttons add new {firstEntity.DisplayName.ToLowerInvariant()} and other business records. Forms, validation, and available actions are generated for this ERP."));
        }
        tourSteps.Add(TourStep("/notifications", ".page-head", "Stay on top of changes", "Notifications collect updates that need your attention, while Tasks contains approvals and workflow decisions assigned to you.", padding: 0));
        tourSteps.Add(TourStep(null, "[data-feedback-open]", "Shape your ERP", "Send feedback whenever something is missing or could work better. Your feedback is processed automatically by the ErpGenerator AI engine and used to propose improvements to this ERP."));
        var tourJson = JsonSerializer.Serialize(tourSteps);

        var shell = $$"""
          <a class="skip-link" href="#content">Skip to content</a>
          <div class="app-shell" data-sidebar="expanded" data-mobile-nav="closed">
            <div class="scrim" data-mobile-close></div>
            <aside class="sidebar" data-tour-nav>
              <div class="sidebar-head">
                <a class="brand" href="/" title="{{Html(company)}}">
                  {{BrandAssets.MarkSvg}}
                  <span>{{Html(company)}}</span>
                </a>
                <button class="sidebar-toggle" type="button" data-sidebar-toggle aria-label="Collapse sidebar" title="Toggle sidebar">
                  {{RuntimeUi.Icon("panel-left", "")}}
                </button>
              </div>
              <nav class="nav" aria-label="Main">
                <span class="nav-label">Workspace</span>
                {{NavLink("/", "dashboard", "Dashboard")}}
                {{NavLink("/reports", "reports", "Reports")}}
                {{NavLink("/tasks", "tasks", "Tasks")}}
                {{NavLink("/notifications", "bell", "Notifications")}}
                {{recordsSection}}
                <span class="nav-label">Administration</span>
                {{NavLink("/settings/users", "users", "Users")}}
                {{NavLink("/settings/deployment", "settings", "Settings")}}
              </nav>
              <div class="sidebar-foot">
                <span class="avatar" aria-hidden="true">{{Html(RuntimeUi.Initials(displayName))}}</span>
                <div class="user-meta"><strong>{{Html(displayName)}}</strong><span>{{Html(RuntimeUi.Humanize(session.Role))}}</span></div>
                {{signOut}}
              </div>
            </aside>
            <main class="content" id="content">
              <header class="topbar">
                <button class="icon-button mobile-only" type="button" data-mobile-open aria-label="Open navigation">{{RuntimeUi.Icon("menu", "")}}</button>
                <div class="topbar-search" data-search>
                  {{RuntimeUi.Icon("search", "search-icon")}}
                  <input type="search" placeholder="Search pages and records…" aria-label="Global search" autocomplete="off" data-global-search />
                  <kbd aria-hidden="true">/</kbd>
                  <div class="search-results" data-search-results role="listbox" aria-label="Search results" hidden></div>
                </div>
                <div class="topbar-actions">
                  <button class="icon-button" type="button" data-tour-start aria-label="Start product tour" title="Product tour">{{RuntimeUi.Icon("help", "")}}</button>
                  <button class="icon-button" type="button" data-theme-toggle aria-label="Toggle light or dark theme" title="Toggle theme">{{RuntimeUi.Icon("sun", "")}}</button>
                  <a class="icon-button" href="/notifications" aria-label="Notifications" data-tour-notifications>{{RuntimeUi.Icon("bell", "")}}</a>
                  <button class="icon-button feedback-trigger" type="button" data-feedback-open aria-label="Send feedback" title="Send feedback">{{RuntimeUi.Icon("message", "")}}</button>
                </div>
              </header>
              {{body}}
            </main>
          </div>
          <div class="tour-root" data-tour-root hidden>
            <div class="tour-spotlight" data-tour-spotlight aria-hidden="true"></div>
            <section class="tour-popover" data-tour-popover role="dialog" aria-modal="true" aria-labelledby="tour-title" aria-describedby="tour-description">
              <div class="tour-head">
                <span class="tour-brand">{{BrandAssets.MarkSvg}}</span>
                <span>ERP introduction</span>
                <button class="icon-button tour-close" type="button" data-tour-close aria-label="Close tour">{{RuntimeUi.Icon("x", "")}}</button>
              </div>
              <div class="tour-body">
                <span class="tour-count" data-tour-count></span>
                <h2 id="tour-title" data-tour-title></h2>
                <p id="tour-description" data-tour-description></p>
              </div>
              <div class="tour-actions">
                <button class="button button-secondary button-sm" type="button" data-tour-skip>Skip tour</button>
                <span class="tour-spacer"></span>
                <button class="button button-secondary button-sm" type="button" data-tour-back>Back</button>
                <button class="button button-sm" type="button" data-tour-next>Next{{RuntimeUi.Icon("chevron-right", "")}}</button>
              </div>
            </section>
          </div>
          {{ConfirmDialog()}}
          {{FeedbackDialog()}}
          <div class="toast-host" data-toast-host aria-live="polite" aria-atomic="false"></div>
          <script>
          (() => {
            const shell = document.querySelector('.app-shell');
            const root = document.documentElement;

            const THEME_KEY = 'erpgenerator-theme';
            document.querySelector('[data-theme-toggle]')?.addEventListener('click', () => {
              const dark = root.getAttribute('data-theme') === 'dark'
                || (!root.getAttribute('data-theme') && matchMedia('(prefers-color-scheme: dark)').matches);
              const next = dark ? 'light' : 'dark';
              root.setAttribute('data-theme', next);
              try { localStorage.setItem(THEME_KEY, next); } catch (e) {}
            });

            const SIDEBAR_KEY = 'erpgenerator-sidebar';
            try { if (localStorage.getItem(SIDEBAR_KEY) === 'collapsed') shell.setAttribute('data-sidebar', 'collapsed'); } catch (e) {}
            document.querySelector('[data-sidebar-toggle]')?.addEventListener('click', () => {
              const collapsed = shell.getAttribute('data-sidebar') === 'collapsed';
              shell.setAttribute('data-sidebar', collapsed ? 'expanded' : 'collapsed');
              try { localStorage.setItem(SIDEBAR_KEY, collapsed ? 'expanded' : 'collapsed'); } catch (e) {}
            });

            const RECORDS_GROUP_KEY = 'erpgenerator-nav-records';
            const recordsGroup = document.querySelector('[data-nav-group="records"]');
            const recordsToggle = recordsGroup?.querySelector('.nav-group-toggle');
            const setRecordsExpanded = (expanded) => {
              if (!recordsGroup || !recordsToggle) return;
              recordsGroup.setAttribute('data-expanded', expanded ? 'true' : 'false');
              recordsToggle.setAttribute('aria-expanded', expanded ? 'true' : 'false');
            };
            if (recordsGroup) {
              try { setRecordsExpanded(localStorage.getItem(RECORDS_GROUP_KEY) !== 'collapsed'); } catch (e) {}
              recordsToggle?.addEventListener('click', () => {
                const expanded = recordsGroup.getAttribute('data-expanded') !== 'true';
                setRecordsExpanded(expanded);
                try { localStorage.setItem(RECORDS_GROUP_KEY, expanded ? 'expanded' : 'collapsed'); } catch (e) {}
              });
            }
            document.querySelector('[data-mobile-open]')?.addEventListener('click', () => shell.setAttribute('data-mobile-nav', 'open'));
            document.querySelector('[data-mobile-close]')?.addEventListener('click', () => shell.setAttribute('data-mobile-nav', 'closed'));

            const path = window.location.pathname;
            document.querySelectorAll('nav a[data-path]').forEach((link) => {
              const target = link.getAttribute('data-path');
              const active = target === '/' ? path === '/' : path === target || path.startsWith(target + '/');
              if (active) link.setAttribute('aria-current', 'page');
            });

            const host = document.querySelector('[data-toast-host]');
            const ICONS = {
              success: '<path d="M20 6 9 17l-5-5"/>',
              danger: '<circle cx="12" cy="12" r="10"/><path d="m15 9-6 6"/><path d="m9 9 6 6"/>',
              info: '<circle cx="12" cy="12" r="10"/><path d="M12 16v-4"/><path d="M12 8h.01"/>',
              warning: '<path d="m21.73 18-8-14a2 2 0 0 0-3.48 0l-8 14A2 2 0 0 0 4 21h16a2 2 0 0 0 1.73-3Z"/><path d="M12 9v4"/><path d="M12 17h.01"/>'
            };
            window.erpgeneratorToast = (message, opts = {}) => {
              const tone = opts.tone || 'info';
              const el = document.createElement('div');
              el.className = `toast tone-${tone}`;
              el.setAttribute('role', tone === 'danger' ? 'alert' : 'status');
              el.innerHTML = `<svg class="toast-icon" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">${ICONS[tone] || ICONS.info}</svg>`
                + `<div class="toast-body">${opts.title ? '<span class="toast-title"></span>' : ''}<span class="toast-msg"></span></div>`
                + `<button class="toast-close" type="button" aria-label="Dismiss">✕</button>`;
              if (opts.title) el.querySelector('.toast-title').textContent = opts.title;
              el.querySelector('.toast-msg').textContent = message;
              const remove = () => { el.setAttribute('data-leaving', 'true'); setTimeout(() => el.remove(), 160); };
              el.querySelector('.toast-close').addEventListener('click', remove);
              host.appendChild(el);
              if (opts.duration !== 0) setTimeout(remove, opts.duration || 4200);
              return remove;
            };

            const dialog = document.querySelector('[data-confirm-dialog]');
            window.erpgeneratorConfirm = (opts = {}) => new Promise((resolve) => {
              dialog.querySelector('[data-confirm-title]').textContent = opts.title || 'Are you sure?';
              dialog.querySelector('[data-confirm-message]').textContent = opts.message || '';
              const accept = dialog.querySelector('[data-confirm-accept]');
              accept.textContent = opts.confirmLabel || 'Confirm';
              accept.className = 'button ' + (opts.tone === 'danger' ? 'button-danger' : '');
              dialog.querySelector('[data-confirm-icon]').className = 'dialog-icon ' + (opts.tone === 'danger' ? 'tone-danger' : 'tone-warning');
              const reasonWrap = dialog.querySelector('[data-confirm-reason-wrap]');
              const reason = dialog.querySelector('[data-confirm-reason]');
              reasonWrap.hidden = !opts.requireReason;
              reason.value = '';
              reason.removeAttribute('aria-invalid');
              const onClose = () => {
                dialog.removeEventListener('close', onClose);
                resolve({ confirmed: dialog.returnValue === 'confirm', reason: reason.value.trim() });
              };
              dialog.addEventListener('close', onClose);
              dialog.showModal();
              setTimeout(() => (opts.requireReason ? reason : accept).focus(), 30);
            });
            dialog.querySelector('form').addEventListener('submit', (e) => {
              const btn = e.submitter;
              const wrap = dialog.querySelector('[data-confirm-reason-wrap]');
              if (btn && btn.value === 'confirm' && !wrap.hidden) {
                const r = dialog.querySelector('[data-confirm-reason]');
                if (!r.value.trim()) { e.preventDefault(); r.setAttribute('aria-invalid', 'true'); r.focus(); }
              }
            });

            document.addEventListener('click', (e) => {
              const toggle = e.target.closest('[data-menu-toggle]');
              document.querySelectorAll('.menu[data-open="true"]').forEach((m) => { if (!toggle || m !== toggle.closest('.menu')) m.removeAttribute('data-open'); });
              if (toggle) { const menu = toggle.closest('.menu'); menu.setAttribute('data-open', menu.getAttribute('data-open') === 'true' ? 'false' : 'true'); }
            });
            document.addEventListener('keydown', (e) => {
              if (e.key === 'Escape') document.querySelectorAll('.menu[data-open="true"]').forEach((m) => m.removeAttribute('data-open'));
              if (e.key === '/' && !/^(INPUT|TEXTAREA|SELECT)$/.test(document.activeElement.tagName)) {
                e.preventDefault();
                document.querySelector('[data-global-search]')?.focus();
              }
            });
          })();
          </script>
          <script>window.__ERP_GENERATOR_NAV = {{searchJson}};</script>
          <script>window.__ERP_GENERATOR_TOUR = {{tourJson}};</script>
          {{SearchScript()}}
          {{FeedbackScript()}}
          {{TourScript()}}
          """;

        return Page(title, shell);
    }

    private static string NavLink(string href, string icon, string label) =>
        $"""<a href="{Html(href)}" data-path="{Html(href)}">{RuntimeUi.Icon(icon)}<span class="nav-text">{Html(label)}</span></a>""";

    private static object SearchItem(string label, string href, string group, string iconName) =>
        new { label, href, group, icon = RuntimeUi.Icon(iconName, "search-item-icon") };

    private static object TourStep(
        string? path,
        string selector,
        string title,
        string description,
        int padding = 7,
        string? placement = null,
        string? align = null) =>
        new { path, selector, title, description, padding, placement, align };

    // Global search command palette. Filters the injected nav destinations
    // (window.__ERP_GENERATOR_NAV), groups them, and supports full keyboard use.
    // Kept as a plain string so its braces don't collide with raw-string interpolation.
    private static string SearchScript() =>
        """
        <script>
        (() => {
          const input = document.querySelector('[data-global-search]');
          const results = document.querySelector('[data-search-results]');
          if (!input || !results) return;
          const all = window.__ERP_GENERATOR_NAV || [];
          let items = [];
          let active = -1;
          input.setAttribute('role', 'combobox');
          input.setAttribute('aria-autocomplete', 'list');
          input.setAttribute('aria-expanded', 'false');
          const close = () => { results.hidden = true; input.setAttribute('aria-expanded', 'false'); active = -1; };
          const open = () => { results.hidden = false; input.setAttribute('aria-expanded', 'true'); };
          const highlight = () => items.forEach((it, i) => {
            const on = i === active;
            it.classList.toggle('is-active', on);
            it.setAttribute('aria-selected', on ? 'true' : 'false');
            if (on) it.scrollIntoView({ block: 'nearest' });
          });
          const setActive = (i) => { if (i >= 0 && i < items.length) { active = i; highlight(); } };
          const render = () => {
            const q = input.value.trim().toLowerCase();
            const matched = q ? all.filter(d => d.label.toLowerCase().includes(q)) : all;
            results.replaceChildren();
            items = [];
            if (!matched.length) {
              const empty = document.createElement('div');
              empty.className = 'autocomplete-empty';
              empty.textContent = 'No matching pages or records.';
              results.append(empty);
              open();
              return;
            }
            let lastGroup = '';
            matched.forEach((d) => {
              if (d.group !== lastGroup) {
                lastGroup = d.group;
                const h = document.createElement('div');
                h.className = 'search-group';
                h.textContent = d.group;
                results.append(h);
              }
              const a = document.createElement('a');
              a.className = 'search-item';
              a.href = d.href;
              a.setAttribute('role', 'option');
              a.innerHTML = d.icon + '<span class="search-item-label"></span><span class="search-item-hint"></span>';
              a.querySelector('.search-item-label').textContent = d.label;
              a.querySelector('.search-item-hint').textContent = d.href;
              const index = items.length;
              a.addEventListener('mousemove', () => setActive(index));
              a.addEventListener('mousedown', (e) => { e.preventDefault(); window.location.href = d.href; });
              items.push(a);
              results.append(a);
            });
            active = 0;
            highlight();
            open();
          };
          input.addEventListener('input', render);
          input.addEventListener('focus', render);
          input.addEventListener('keydown', (e) => {
            if (e.key === 'ArrowDown') { e.preventDefault(); if (results.hidden) render(); else setActive(Math.min(active + 1, items.length - 1)); }
            else if (e.key === 'ArrowUp') { e.preventDefault(); setActive(Math.max(active - 1, 0)); }
            else if (e.key === 'Enter') { if (active >= 0 && items[active]) { e.preventDefault(); window.location.href = items[active].href; } }
            else if (e.key === 'Escape') { if (!results.hidden) { e.preventDefault(); close(); input.blur(); } }
          });
          input.addEventListener('blur', () => setTimeout(close, 150));
        })();
        </script>
        """;

    private static string TourScript() =>
        """
        <script>
        (() => {
          const steps = Array.isArray(window.__ERP_GENERATOR_TOUR) ? window.__ERP_GENERATOR_TOUR : [];
          const root = document.querySelector('[data-tour-root]');
          const spotlight = root?.querySelector('[data-tour-spotlight]');
          const popover = root?.querySelector('[data-tour-popover]');
          const startButton = document.querySelector('[data-tour-start]');
          if (!steps.length || !root || !spotlight || !popover || !startButton) return;

          const STATE_KEY = 'erpgenerator-product-tour-state-v1';
          const COMPLETE_KEY = 'erpgenerator-product-tour-complete-v1';
          const title = root.querySelector('[data-tour-title]');
          const description = root.querySelector('[data-tour-description]');
          const count = root.querySelector('[data-tour-count]');
          const back = root.querySelector('[data-tour-back]');
          const next = root.querySelector('[data-tour-next]');
          let index = 0;
          let activeTarget = null;

          const readState = () => {
            try { return JSON.parse(localStorage.getItem(STATE_KEY) || 'null'); }
            catch (error) { return null; }
          };
          const writeState = () => {
            try { localStorage.setItem(STATE_KEY, JSON.stringify({ active: true, index })); }
            catch (error) {}
          };
          const clearTarget = () => {
            activeTarget?.removeAttribute('data-tour-active');
            activeTarget = null;
            document.querySelector('.app-shell')?.setAttribute('data-mobile-nav', 'closed');
          };
          const finish = () => {
            clearTarget();
            root.hidden = true;
            try {
              localStorage.removeItem(STATE_KEY);
              localStorage.setItem(COMPLETE_KEY, 'true');
            } catch (error) {}
            startButton.focus();
          };
          const clamp = (value, min, max) => Math.max(min, Math.min(max, value));
          const position = () => {
            if (!activeTarget || root.hidden) return;
            const rect = activeTarget.getBoundingClientRect();
            const step = steps[index];
            const pad = Number.isFinite(step?.padding) ? step.padding : 7;
            spotlight.style.left = `${Math.max(4, rect.left - pad)}px`;
            spotlight.style.top = `${Math.max(4, rect.top - pad)}px`;
            spotlight.style.width = `${Math.min(window.innerWidth - 8, rect.width + pad * 2)}px`;
            spotlight.style.height = `${Math.min(window.innerHeight - 8, rect.height + pad * 2)}px`;

            const popoverRect = popover.getBoundingClientRect();
            const gap = 16;
            const candidates = {
              bottom: {
                fits: window.innerHeight - rect.bottom >= popoverRect.height + gap + 12,
                left: rect.left,
                top: rect.bottom + gap,
                space: window.innerHeight - rect.bottom
              },
              top: {
                fits: rect.top >= popoverRect.height + gap + 12,
                left: rect.left,
                top: rect.top - popoverRect.height - gap,
                space: rect.top
              },
              right: {
                fits: window.innerWidth - rect.right >= popoverRect.width + gap + 12,
                left: rect.right + gap,
                top: rect.top,
                space: window.innerWidth - rect.right
              },
              left: {
                fits: rect.left >= popoverRect.width + gap + 12,
                left: rect.left - popoverRect.width - gap,
                top: rect.top,
                space: rect.left
              }
            };
            const preferred = step?.placement || (activeTarget.matches('.sidebar') ? 'right' : activeTarget.matches('[data-feedback-open]') ? 'top' : 'bottom');
            const order = [preferred, 'bottom', 'top', 'right', 'left'].filter((side, itemIndex, all) => all.indexOf(side) === itemIndex);
            const selected = order.map(side => candidates[side]).find(candidate => candidate.fits)
              || order.map(side => candidates[side]).sort((a, b) => b.space - a.space)[0];
            let left = selected.left;
            let top = selected.top;
            popover.style.left = `${clamp(left, 12, Math.max(12, window.innerWidth - popoverRect.width - 12))}px`;
            popover.style.top = `${clamp(top, 12, Math.max(12, window.innerHeight - popoverRect.height - 12))}px`;
          };

          const show = () => {
            if (index < 0 || index >= steps.length) { finish(); return; }
            const step = steps[index];
            writeState();
            if (step.path && window.location.pathname !== step.path) {
              window.location.assign(step.path);
              return;
            }

            clearTarget();
            let target = document.querySelector(step.selector);
            if (!target) {
              index += 1;
              show();
              return;
            }
            if (window.innerWidth <= 860 && target.matches('.sidebar')) {
              target = document.querySelector('[data-mobile-open]') || target;
            }
            activeTarget = target;
            activeTarget.setAttribute('data-tour-active', 'true');
            if (activeTarget.matches('.sidebar')) {
              const shell = document.querySelector('.app-shell');
              shell?.setAttribute('data-sidebar', 'expanded');
              if (window.innerWidth <= 860) shell?.setAttribute('data-mobile-nav', 'open');
            }
            title.textContent = step.title;
            description.textContent = step.description;
            count.textContent = `Step ${index + 1} of ${steps.length}`;
            back.disabled = index === 0;
            next.textContent = index === steps.length - 1 ? 'Finish' : 'Next';
            root.hidden = false;
            activeTarget.scrollIntoView({ behavior: 'smooth', block: 'center', inline: 'nearest' });
            requestAnimationFrame(() => requestAnimationFrame(() => {
              position();
              next.focus({ preventScroll: true });
            }));
          };
          const move = (delta) => {
            index += delta;
            if (index >= steps.length) { finish(); return; }
            show();
          };
          const begin = () => {
            index = 0;
            try { localStorage.removeItem(COMPLETE_KEY); } catch (error) {}
            show();
          };

          startButton.addEventListener('click', begin);
          root.querySelector('[data-tour-close]').addEventListener('click', finish);
          root.querySelector('[data-tour-skip]').addEventListener('click', finish);
          back.addEventListener('click', () => move(-1));
          next.addEventListener('click', () => move(1));
          window.addEventListener('resize', position);
          window.addEventListener('scroll', position, true);
          document.addEventListener('keydown', (event) => {
            if (root.hidden) return;
            if (event.key === 'Escape') { event.preventDefault(); finish(); }
            if (event.key === 'ArrowRight') { event.preventDefault(); move(1); }
            if (event.key === 'ArrowLeft' && index > 0) { event.preventDefault(); move(-1); }
          });

          const state = readState();
          if (state?.active && Number.isInteger(state.index)) {
            index = clamp(state.index, 0, steps.length - 1);
            show();
          } else {
            let completed = false;
            try { completed = localStorage.getItem(COMPLETE_KEY) === 'true'; } catch (error) {}
            if (!completed && window.location.pathname === '/') setTimeout(begin, 650);
          }
        })();
        </script>
        """;

    public static string Page(string title, string body) =>
        $$"""
        <!doctype html>
        <html lang="en">
          <head>
            <meta charset="utf-8" />
            <meta name="viewport" content="width=device-width, initial-scale=1" />
            <title>{{WebUtility.HtmlEncode(title)}}</title>
            <link rel="icon" type="image/svg+xml" href="data:image/svg+xml;base64,PHN2ZyB4bWxucz0iaHR0cDovL3d3dy53My5vcmcvMjAwMC9zdmciIHZpZXdCb3g9IjAgMCA2NCA2NCI+CiAgPHJlY3Qgd2lkdGg9IjY0IiBoZWlnaHQ9IjY0IiByeD0iMTQiIGZpbGw9IiMyNTYzZWIiLz4KICA8cGF0aCBkPSJNMTcgMjBoMzBNMTcgMzJoMjJNMTcgNDRoMzAiIHN0cm9rZT0iI2ZmZiIgc3Ryb2tlLXdpZHRoPSI1IiBzdHJva2UtbGluZWNhcD0icm91bmQiLz4KPC9zdmc+Cg==" />
            <style>
        {{RuntimeStyles.Css}}
            </style>
            {{ThemeBootScript}}
          </head>
          <body>{{body}}</body>
        </html>
        """;

    // Applies the persisted theme before first paint to avoid a light/dark flash.
    // Kept as a plain string so its braces do not collide with raw-string interpolation.
    private const string ThemeBootScript =
        "<script>(function(){try{var t=localStorage.getItem('erpgenerator-theme');if(t==='dark'||t==='light')document.documentElement.setAttribute('data-theme',t);}catch(e){}})();</script>";

    private static string ConfirmDialog() =>
        $"""
        <dialog data-confirm-dialog>
          <form method="dialog" class="dialog-body">
            <div class="dialog-icon-row">
              <span class="dialog-icon tone-warning" data-confirm-icon aria-hidden="true">{RuntimeUi.Icon("alert", "")}</span>
              <div style="display:grid;gap:8px;flex:1 1 auto;min-width:0">
                <h2 data-confirm-title>Are you sure?</h2>
                <p data-confirm-message></p>
                <label class="confirm-form" data-confirm-reason-wrap hidden>
                  <span class="field-label">Reason <span class="req">*</span></span>
                  <input data-confirm-reason placeholder="Provide a reason" />
                </label>
              </div>
            </div>
            <div class="dialog-actions">
              <button class="button button-outline" value="cancel" type="submit">Cancel</button>
              <button class="button button-danger" value="confirm" type="submit" data-confirm-accept>Confirm</button>
            </div>
          </form>
        </dialog>
        """;

    private static string FeedbackDialog() =>
        """
        <dialog class="feedback-dialog" data-feedback-dialog aria-labelledby="feedback-dialog-title">
          <form class="dialog-body" method="dialog" data-feedback-form>
            <div style="display:flex;align-items:center;justify-content:space-between;gap:12px">
              <h2 id="feedback-dialog-title">Send feedback</h2>
            </div>
            <label class="field">
              <span class="field-label">Category</span>
              <select name="category">
                <option value="general">General</option>
                <option value="missing_field">Missing field</option>
                <option value="workflow">Workflow</option>
                <option value="reporting">Reporting</option>
                <option value="integration">Integration</option>
                <option value="ui">Interface</option>
              </select>
            </label>
            <label class="field">
              <span class="field-label">Message</span>
              <textarea name="message" rows="5" maxlength="2000" required placeholder="Describe the issue or idea"></textarea>
            </label>
            <p class="field-error" data-feedback-status aria-live="polite" hidden></p>
            <div class="dialog-actions">
              <button class="button button-outline" type="button" data-feedback-close>Cancel</button>
              <button class="button" type="submit">Submit feedback</button>
            </div>
          </form>
        </dialog>
        """;

    private static string FeedbackScript() =>
        """
        <script>
        (() => {
          const dialog = document.querySelector('[data-feedback-dialog]');
          const form = document.querySelector('[data-feedback-form]');
          const status = document.querySelector('[data-feedback-status]');
          const openButton = document.querySelector('[data-feedback-open]');
          if (!dialog || !form || !openButton) return;
          const context = () => {
            const match = window.location.pathname.match(/^\/entities\/([^/]+)(?:\/(\d+))?/);
            return {
              entityKey: match ? decodeURIComponent(match[1]) : '',
              recordId: match && match[2] ? match[2] : '',
              pagePath: window.location.pathname
            };
          };
          openButton.addEventListener('click', () => { status.hidden = true; status.textContent = ''; dialog.showModal(); });
          document.querySelector('[data-feedback-close]')?.addEventListener('click', () => dialog.close());
          form.addEventListener('submit', async (event) => {
            if (event.submitter && event.submitter.type !== 'submit') return;
            event.preventDefault();
            const data = new FormData(form);
            const submit = form.querySelector('button[type="submit"]');
            submit.disabled = true;
            status.hidden = true;
            try {
              const response = await fetch('/api/runtime/feedback', {
                method: 'POST',
                headers: { 'content-type': 'application/json' },
                body: JSON.stringify({
                  category: data.get('category'),
                  message: data.get('message'),
                  ...context(),
                  metadata: { userAgent: navigator.userAgent.slice(0, 180) }
                })
              });
              const body = await response.json().catch(() => ({}));
              if (!response.ok) throw new Error(body.message || 'Feedback was not sent.');
              form.reset();
              dialog.close();
              window.erpgeneratorToast('Thanks — your feedback was sent.', { tone: 'success' });
            } catch (error) {
              status.textContent = error instanceof Error ? error.message : 'Feedback was not sent.';
              status.hidden = false;
            } finally {
              submit.disabled = false;
            }
          });
        })();
        </script>
        """;
}
