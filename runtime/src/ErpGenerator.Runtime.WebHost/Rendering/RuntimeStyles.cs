/// <summary>
/// The single inline stylesheet served with every generated ERP page.
/// Token model and component anatomy follow shadcn/ui conventions; the sheet is
/// self-contained (the runtime has no static-asset pipeline, so no external
/// font/CSS/JS fetches). Ships light + dark themes: dark follows the operating
/// system unless the user overrides it from the top bar (which stamps
/// data-theme on the document element).
/// </summary>
internal static class RuntimeStyles
{
    public const string Css =
        """
        /* ============================================================================
           ErpGenerator Runtime design system — shared by every generated ERP.
           Token model & component anatomy follow shadcn/ui conventions. Self-contained
           (no external font/CSS/JS fetches: the runtime has no static-asset pipeline).
           Light + dark themes; dark follows the OS unless the user overrides via the
           topbar toggle (which stamps data-theme on <html>).
           ============================================================================ */

        /* ---- Tokens: light (default) --------------------------------------------- */
        :root {
          color-scheme: light;
          --font-sans: Inter, ui-sans-serif, system-ui, -apple-system, "Segoe UI", Roboto, sans-serif;
          --font-mono: ui-monospace, SFMono-Regular, "SF Mono", Menlo, Consolas, monospace;

          --background: #f6f7f9;
          --foreground: #10131a;
          --surface: #ffffff;
          --surface-2: #f2f3f6;
          --card: #ffffff;
          --card-foreground: #10131a;
          --sidebar: #ffffff;
          --border: #e6e8ec;
          --border-strong: #d5d8df;
          --input: #d5d8df;
          --ring: #6338fe;

          --primary: #6338fe;
          --primary-hover: #5730e6;
          --primary-foreground: #ffffff;
          --muted: #f2f3f6;
          --muted-foreground: #61697a;
          --accent: #efecfe;
          --accent-foreground: #4f2fd0;

          --destructive: #dc2626;
          --destructive-hover: #b91c1c;
          --destructive-foreground: #ffffff;
          --success: #16a34a;
          --warning: #b45309;

          --radius: 8px;
          --radius-sm: 6px;
          --radius-lg: 12px;
          --shadow-xs: 0 1px 2px 0 rgb(16 19 26 / 0.05);
          --shadow-sm: 0 1px 3px 0 rgb(16 19 26 / 0.08), 0 1px 2px -1px rgb(16 19 26 / 0.08);
          --shadow-md: 0 4px 14px -3px rgb(16 19 26 / 0.12);
          --shadow-lg: 0 12px 40px -8px rgb(16 19 26 / 0.24);

          /* Status vocabulary — one semantic colour + label per state, shared across
             every module. bg/fg/border triplets so pills read on any surface. */
          --status-neutral-bg: #eef0f3; --status-neutral-fg: #495260; --status-neutral-bd: #dcdfe5;
          --status-info-bg: #e8effe;    --status-info-fg: #1d4ed8;    --status-info-bd: #c7d8fb;
          --status-success-bg: #e6f6ec; --status-success-fg: #15803d; --status-success-bd: #bce6c9;
          --status-warning-bg: #fdf1dc; --status-warning-fg: #92500a; --status-warning-bd: #f2dcac;
          --status-danger-bg: #fdeaea;  --status-danger-fg: #b42222;  --status-danger-bd: #f4c2c2;
        }

        /* ---- Tokens: dark -------------------------------------------------------- */
        @media (prefers-color-scheme: dark) {
          :root:not([data-theme]) {
            color-scheme: dark;
            --background: #0b0d12;
            --foreground: #e8ecf3;
            --surface: #14171f;
            --surface-2: #1a1e28;
            --card: #14171f;
            --card-foreground: #e8ecf3;
            --sidebar: #0f1218;
            --border: #242a35;
            --border-strong: #323945;
            --input: #323945;
            --ring: #8b6dff;
            --primary: #7c5cff;
            --primary-hover: #8f74ff;
            --primary-foreground: #ffffff;
            --muted: #1a1e28;
            --muted-foreground: #949db0;
            --accent: #221c3f;
            --accent-foreground: #c3b5ff;
            --destructive: #f0555a;
            --destructive-hover: #f37176;
            --destructive-foreground: #ffffff;
            --success: #34d17f;
            --warning: #e0a53a;
            --shadow-xs: 0 1px 2px 0 rgb(0 0 0 / 0.4);
            --shadow-sm: 0 1px 3px 0 rgb(0 0 0 / 0.5);
            --shadow-md: 0 4px 14px -3px rgb(0 0 0 / 0.55);
            --shadow-lg: 0 12px 40px -8px rgb(0 0 0 / 0.7);
            --status-neutral-bg: #1c2230; --status-neutral-fg: #aab3c2; --status-neutral-bd: #2c3444;
            --status-info-bg: #14243f;    --status-info-fg: #8bb4ff;    --status-info-bd: #244066;
            --status-success-bg: #102a1c; --status-success-fg: #62d999; --status-success-bd: #1f4832;
            --status-warning-bg: #2e2410; --status-warning-fg: #e6b968; --status-warning-bd: #4a3a17;
            --status-danger-bg: #2f1618;  --status-danger-fg: #f39ba0;  --status-danger-bd: #55272b;
          }
        }
        :root[data-theme="dark"] {
          color-scheme: dark;
          --background: #0b0d12;
          --foreground: #e8ecf3;
          --surface: #14171f;
          --surface-2: #1a1e28;
          --card: #14171f;
          --card-foreground: #e8ecf3;
          --sidebar: #0f1218;
          --border: #242a35;
          --border-strong: #323945;
          --input: #323945;
          --ring: #8b6dff;
          --primary: #7c5cff;
          --primary-hover: #8f74ff;
          --primary-foreground: #ffffff;
          --muted: #1a1e28;
          --muted-foreground: #949db0;
          --accent: #221c3f;
          --accent-foreground: #c3b5ff;
          --destructive: #f0555a;
          --destructive-hover: #f37176;
          --destructive-foreground: #ffffff;
          --success: #34d17f;
          --warning: #e0a53a;
          --shadow-xs: 0 1px 2px 0 rgb(0 0 0 / 0.4);
          --shadow-sm: 0 1px 3px 0 rgb(0 0 0 / 0.5);
          --shadow-md: 0 4px 14px -3px rgb(0 0 0 / 0.55);
          --shadow-lg: 0 12px 40px -8px rgb(0 0 0 / 0.7);
          --status-neutral-bg: #1c2230; --status-neutral-fg: #aab3c2; --status-neutral-bd: #2c3444;
          --status-info-bg: #14243f;    --status-info-fg: #8bb4ff;    --status-info-bd: #244066;
          --status-success-bg: #102a1c; --status-success-fg: #62d999; --status-success-bd: #1f4832;
          --status-warning-bg: #2e2410; --status-warning-fg: #e6b968; --status-warning-bd: #4a3a17;
          --status-danger-bg: #2f1618;  --status-danger-fg: #f39ba0;  --status-danger-bd: #55272b;
        }

        /* ---- Base ---------------------------------------------------------------- */
        * { box-sizing: border-box; }
        [hidden] { display: none !important; }
        html { -webkit-text-size-adjust: 100%; }
        body {
          margin: 0; min-height: 100vh; background: var(--background); color: var(--foreground);
          font-family: var(--font-sans); font-size: 13px; line-height: 1.5;
          -webkit-font-smoothing: antialiased; text-rendering: optimizeLegibility;
        }
        a { color: inherit; text-decoration: none; }
        h1, h2, h3 { margin: 0; font-weight: 600; letter-spacing: -0.011em; }
        p { margin: 0; }
        :focus-visible { outline: 2px solid var(--ring); outline-offset: 2px; border-radius: 2px; }
        .num { font-variant-numeric: tabular-nums; text-align: right; }
        .mono { font-family: var(--font-mono); }
        .muted { color: var(--muted-foreground); }
        .visually-hidden { position: absolute; width: 1px; height: 1px; margin: -1px; padding: 0; overflow: hidden; clip: rect(0 0 0 0); white-space: nowrap; border: 0; }
        .skip-link { position: absolute; left: -9999px; top: 8px; z-index: 100; border-radius: var(--radius-sm); background: var(--card); color: var(--foreground); padding: 8px 14px; box-shadow: var(--shadow-md); }
        .skip-link:focus-visible { left: 8px; }
        kbd { display: inline-flex; align-items: center; min-width: 18px; height: 18px; justify-content: center; padding: 0 5px; border: 1px solid var(--border-strong); border-bottom-width: 2px; border-radius: 4px; background: var(--surface-2); color: var(--muted-foreground); font-family: var(--font-mono); font-size: 11px; }

        /* ---- App shell ----------------------------------------------------------- */
        .app-shell { display: grid; min-height: 100vh; grid-template-columns: 244px 1fr; transition: grid-template-columns 160ms ease; }
        .scrim { display: none; }
        .app-shell[data-sidebar="collapsed"] { grid-template-columns: 60px 1fr; }
        .sidebar { position: sticky; top: 0; align-self: start; height: 100vh; display: flex; flex-direction: column; gap: 4px; border-right: 1px solid var(--border); background: var(--sidebar); padding: 12px 10px; overflow: hidden; }
        .sidebar-head { display: flex; align-items: center; gap: 8px; padding: 6px 8px 10px; }
        .brand { display: flex; align-items: center; gap: 8px; font-size: 14px; font-weight: 500; letter-spacing: 0; white-space: nowrap; overflow: hidden; }
        .brand-mark { flex: 0 0 auto; width: 22px; height: 22px; }
        .sidebar-toggle { flex: 0 0 auto; display: inline-flex; align-items: center; justify-content: center; width: 30px; height: 30px; margin-left: auto; border: 1px solid transparent; border-radius: var(--radius-sm); background: transparent; color: var(--muted-foreground); cursor: pointer; }
        .sidebar-toggle:hover { background: var(--muted); color: var(--foreground); }
        .nav { flex: 1 1 auto; min-height: 0; display: flex; flex-direction: column; gap: 3px; overflow-y: auto; overscroll-behavior: contain; scrollbar-gutter: stable; padding: 0 3px 12px 0; }
        .nav-label { margin: 14px 8px 5px; color: var(--muted-foreground); font-size: 10.5px; font-weight: 600; letter-spacing: 0.06em; text-transform: uppercase; white-space: nowrap; }
        .nav a { display: flex; min-height: 34px; align-items: center; gap: 10px; border-radius: var(--radius-sm); padding: 8px 8px; color: var(--muted-foreground); font-weight: 500; white-space: nowrap; overflow: hidden; transition: background-color 120ms ease, color 120ms ease; }
        .nav a:hover { background: var(--muted); color: var(--foreground); }
        .nav a[aria-current="page"] { background: var(--accent); color: var(--accent-foreground); font-weight: 600; }
        .nav-icon { flex: 0 0 auto; width: 17px; height: 17px; color: currentColor; }
        .nav-text { overflow: hidden; text-overflow: ellipsis; }
        .nav-group { display: grid; gap: 3px; margin-top: 7px; }
        .nav-group-toggle { display: flex; min-height: 30px; align-items: center; gap: 6px; margin: 4px 0 1px; padding: 6px 8px; border: 0; border-radius: var(--radius-sm); background: transparent; color: var(--muted-foreground); cursor: pointer; font: inherit; text-align: left; }
        .nav-group-toggle:hover { background: var(--muted); color: var(--foreground); }
        .nav-group-label { font-size: 10.5px; font-weight: 600; letter-spacing: 0.06em; text-transform: uppercase; white-space: nowrap; }
        .nav-group-count { display: inline-flex; min-width: 20px; height: 18px; align-items: center; justify-content: center; margin-left: auto; padding: 0 6px; border-radius: 999px; background: var(--muted); color: var(--muted-foreground); font-size: 10px; font-variant-numeric: tabular-nums; }
        .nav-group-chevron { width: 14px; height: 14px; transition: transform 140ms ease; }
        .nav-group[data-expanded="false"] .nav-group-chevron { transform: rotate(-90deg); }
        .nav-group-links { display: grid; gap: 3px; }
        .nav-group[data-expanded="false"] .nav-group-links { display: none; }
        .app-shell[data-sidebar="collapsed"] .nav-label { height: 1px; margin: 8px 14px; padding: 0; background: var(--border); font-size: 0; overflow: hidden; }
        .app-shell[data-sidebar="collapsed"] .nav > .nav-label:first-child { display: none; }
        .app-shell[data-sidebar="collapsed"] .nav a { justify-content: center; padding: 8px 0; }
        .app-shell[data-sidebar="collapsed"] .nav-group { margin-top: 8px; padding-top: 8px; border-top: 1px solid var(--border); }
        .app-shell[data-sidebar="collapsed"] .nav-group-toggle { display: none; }
        .app-shell[data-sidebar="collapsed"] .nav-group-links { display: grid; }
        .app-shell[data-sidebar="collapsed"] .nav-text, .app-shell[data-sidebar="collapsed"] .brand span { display: none; }
        .app-shell[data-sidebar="collapsed"] .sidebar-head { flex-direction: column; justify-content: center; gap: 8px; }
        .app-shell[data-sidebar="collapsed"] .sidebar-toggle { margin-left: 0; }

        /* Profile pinned to the bottom of the sidebar */
        .sidebar-foot { margin-top: auto; display: flex; align-items: center; gap: 8px; padding: 8px 6px 2px; border-top: 1px solid var(--border); }
        .sidebar-foot .avatar { flex: 0 0 auto; }
        .sidebar-foot .user-meta { flex: 1 1 auto; min-width: 0; max-width: none; }
        .sidebar-foot .user-meta strong { font-size: 12px; font-weight: 600; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
        .sidebar-foot .user-meta span { font-size: 10.5px; color: var(--muted-foreground); text-transform: capitalize; }
        .sidebar-foot .icon-button { flex: 0 0 auto; width: 30px; height: 30px; }
        .app-shell[data-sidebar="collapsed"] .sidebar-foot { flex-direction: column; gap: 8px; padding: 8px 0 2px; }
        .app-shell[data-sidebar="collapsed"] .sidebar-foot .user-meta { display: none; }

        .content { min-width: 0; display: flex; flex-direction: column; padding-bottom: 76px; }
        .topbar { position: sticky; top: 0; z-index: 20; display: flex; min-height: 52px; align-items: center; gap: 14px; border-bottom: 1px solid var(--border); background: color-mix(in srgb, var(--surface) 88%, transparent); backdrop-filter: saturate(180%) blur(8px); -webkit-backdrop-filter: saturate(180%) blur(8px); padding: 8px 20px; }
        .topbar-search { flex: 1 1 auto; max-width: 460px; position: relative; }
        .topbar-search input { height: 34px; padding-left: 32px; background: var(--surface-2); border-color: transparent; }
        .topbar-search input:focus-visible { background: var(--surface); }
        .topbar-search .search-icon { position: absolute; left: 9px; top: 50%; transform: translateY(-50%); width: 15px; height: 15px; color: var(--muted-foreground); pointer-events: none; }
        .topbar-search kbd { position: absolute; right: 8px; top: 50%; transform: translateY(-50%); }
        .search-results { position: absolute; left: 0; right: 0; top: calc(100% + 6px); z-index: 60; max-height: min(64vh, 440px); overflow-y: auto; padding: 5px; border: 1px solid var(--border); border-radius: var(--radius); background: var(--surface); box-shadow: var(--shadow-md); }
        .search-group { padding: 9px 8px 4px; color: var(--muted-foreground); font-size: 10.5px; font-weight: 600; letter-spacing: 0.05em; text-transform: uppercase; }
        .search-item { display: flex; align-items: center; gap: 10px; padding: 7px 8px; border-radius: var(--radius-sm); color: var(--foreground); }
        .search-item .search-item-icon { width: 16px; height: 16px; flex: 0 0 auto; color: var(--muted-foreground); }
        .search-item-label { font-size: 13px; font-weight: 500; white-space: nowrap; }
        .search-item-hint { margin-left: auto; padding-left: 14px; color: var(--muted-foreground); font-size: 11.5px; font-family: var(--font-mono); overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
        .search-item.is-active, .search-item:hover { background: var(--accent); color: var(--accent-foreground); }
        .search-item.is-active .search-item-icon, .search-item:hover .search-item-icon { color: var(--accent-foreground); }
        .topbar-actions { display: flex; align-items: center; gap: 6px; margin-left: auto; }
        .icon-button { position: relative; display: inline-flex; align-items: center; justify-content: center; width: 34px; height: 34px; border: 1px solid transparent; border-radius: var(--radius-sm); background: transparent; color: var(--muted-foreground); cursor: pointer; transition: background-color 120ms ease, color 120ms ease; }
        .icon-button:hover { background: var(--muted); color: var(--foreground); }
        .icon-button svg { width: 17px; height: 17px; }
        .notif-dot { position: absolute; top: 6px; right: 7px; min-width: 15px; height: 15px; padding: 0 3px; border-radius: 999px; background: var(--destructive); color: var(--destructive-foreground); font-size: 9px; font-weight: 700; line-height: 15px; text-align: center; }
        .user-chip { display: inline-flex; align-items: center; gap: 8px; padding: 4px 8px 4px 5px; border: 1px solid var(--border); border-radius: 999px; background: var(--surface); cursor: pointer; }
        .user-chip:hover { background: var(--muted); }
        .avatar { display: inline-flex; align-items: center; justify-content: center; width: 26px; height: 26px; border-radius: 50%; background: var(--accent); color: var(--accent-foreground); font-size: 11px; font-weight: 700; text-transform: uppercase; }
        .user-meta { display: flex; flex-direction: column; line-height: 1.2; max-width: 150px; }
        .user-meta strong { font-size: 12px; font-weight: 600; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
        .user-meta span { font-size: 10.5px; color: var(--muted-foreground); text-transform: capitalize; }

        /* Breadcrumbs + page header */
        .breadcrumbs { display: flex; flex-wrap: wrap; align-items: center; gap: 6px; padding: 14px 24px 0; color: var(--muted-foreground); font-size: 12px; }
        .breadcrumbs a:hover { color: var(--foreground); text-decoration: underline; text-underline-offset: 2px; }
        .breadcrumbs .sep { opacity: 0.5; }
        .breadcrumbs [aria-current="page"] { color: var(--foreground); font-weight: 500; }
        .page-head { display: flex; align-items: flex-start; justify-content: space-between; gap: 16px; padding: 16px 24px 14px; }
        .page-head-main { min-width: 0; display: flex; flex-direction: column; gap: 4px; }
        .page-title-row { display: flex; align-items: center; gap: 10px; flex-wrap: wrap; }
        .page-head h1 { font-size: 20px; line-height: 1.25; }
        .record-id { font-family: var(--font-mono); font-size: 12px; color: var(--muted-foreground); background: var(--surface-2); border: 1px solid var(--border); border-radius: var(--radius-sm); padding: 1px 7px; }
        .eyebrow { color: var(--muted-foreground); font-size: 11px; font-weight: 600; letter-spacing: 0.04em; text-transform: uppercase; }
        .page-subtitle { color: var(--muted-foreground); font-size: 12.5px; }
        .page-actions { display: flex; align-items: center; gap: 8px; flex: 0 0 auto; }

        /* ---- Buttons ------------------------------------------------------------- */
        .button { display: inline-flex; align-items: center; justify-content: center; gap: 6px; min-height: 34px; border: 1px solid transparent; border-radius: var(--radius-sm); background: var(--primary); color: var(--primary-foreground); padding: 6px 13px; box-shadow: var(--shadow-xs); font-family: inherit; font-size: 13px; font-weight: 600; white-space: nowrap; cursor: pointer; transition: transform 100ms ease, background-color 120ms ease, border-color 120ms ease, color 120ms ease, box-shadow 120ms ease, opacity 120ms ease; }
        .button svg { width: 15px; height: 15px; flex: 0 0 auto; }
        .button:hover { background: var(--primary-hover); box-shadow: var(--shadow-sm); }
        .button:active { transform: translateY(1px); box-shadow: var(--shadow-xs); }
        .button:focus-visible { outline: 2px solid var(--ring); outline-offset: 2px; }
        .button:disabled, .button[aria-disabled="true"] { pointer-events: none; opacity: 0.5; }
        .button-outline { border-color: var(--border-strong); background: var(--surface); color: var(--foreground); }
        .button-outline:hover { background: var(--muted); }
        .button-secondary { border-color: var(--border-strong); background: var(--surface); color: var(--foreground); }
        .button-secondary:hover { background: var(--muted); }
        .button-ghost { background: transparent; color: var(--foreground); }
        .button-ghost:hover { background: var(--muted); }
        .button-ghost-danger { background: transparent; color: var(--destructive); }
        .button-ghost-danger:hover { background: var(--status-danger-bg); color: var(--status-danger-fg); }
        .button-danger { background: var(--destructive); color: var(--destructive-foreground); }
        .button-danger:hover { background: var(--destructive-hover); }
        .button-danger-soft { border-color: var(--status-danger-bd); background: var(--status-danger-bg); color: var(--status-danger-fg); box-shadow: none; }
        .button-danger-soft:hover { border-color: color-mix(in srgb, var(--destructive) 50%, var(--status-danger-bd)); background: color-mix(in srgb, var(--status-danger-bg) 82%, var(--destructive)); color: var(--status-danger-fg); box-shadow: var(--shadow-xs); }
        .button-sm { min-height: 28px; padding: 3px 9px; font-size: 12px; border-radius: var(--radius-sm); }
        .button-block { width: 100%; }
        .button-icon { padding: 6px; min-width: 34px; }
        .button .spinner { margin-right: 2px; }

        /* Overflow menu (row / secondary actions) */
        .menu { position: relative; display: inline-flex; }
        .menu-panel { position: absolute; right: 0; top: calc(100% + 4px); z-index: 30; min-width: 176px; display: none; flex-direction: column; gap: 1px; padding: 5px; border: 1px solid var(--border); border-radius: var(--radius); background: var(--surface); box-shadow: var(--shadow-md); }
        .menu[data-open="true"] .menu-panel { display: flex; }
        .menu-panel button, .menu-panel a { display: flex; align-items: center; gap: 8px; width: 100%; border: 0; border-radius: var(--radius-sm); background: transparent; color: var(--foreground); padding: 7px 9px; font: inherit; font-size: 12.5px; text-align: left; cursor: pointer; }
        .menu-panel button:hover, .menu-panel a:hover { background: var(--muted); }
        .menu-panel .danger { color: var(--destructive); }
        .menu-panel .danger:hover { background: var(--status-danger-bg); color: var(--status-danger-fg); }
        .menu-sep { height: 1px; margin: 4px 2px; background: var(--border); }

        /* ---- Cards / panels ------------------------------------------------------ */
        .panel, .metric-card, .table-panel, .form-panel, .auth-panel, .summary-card { border: 1px solid var(--border); border-radius: var(--radius); background: var(--card); }
        .table-panel, .form-panel { margin: 0 24px 16px; box-shadow: var(--shadow-xs); }
        .stat-grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(210px, 1fr)); gap: 12px; padding: 0 24px 16px; }
        .metric-card { display: flex; flex-direction: column; gap: 6px; padding: 14px 15px; box-shadow: var(--shadow-xs); }
        .metric-card .metric-label { display: flex; align-items: center; gap: 6px; color: var(--muted-foreground); font-size: 12px; font-weight: 500; }
        .metric-card strong { font-size: 25px; font-weight: 600; font-variant-numeric: tabular-nums; letter-spacing: -0.02em; }
        .metric-card small { color: var(--muted-foreground); font-size: 11.5px; }
        .metric-card .metric-label svg { width: 15px; height: 15px; }
        a.metric-card { transition: border-color 120ms ease, box-shadow 120ms ease; }
        a.metric-card:hover { border-color: var(--border-strong); box-shadow: var(--shadow-sm); }
        a.metric-card .metric-value-row { display: flex; align-items: center; justify-content: space-between; }
        a.metric-card .metric-value-row svg.chev { width: 16px; height: 16px; color: var(--muted-foreground); }
        .metric-card.text strong { font-size: 15px; line-height: 1.4; word-break: break-word; }

        /* Grounded ERP assistant */
        .erp-assistant { margin: 0 24px 16px; border: 1px solid color-mix(in srgb, var(--primary) 28%, var(--border)); border-radius: var(--radius); background: var(--card); box-shadow: var(--shadow-sm); overflow: hidden; }
        .erp-assistant-head { display: flex; align-items: center; gap: 11px; padding: 15px 16px 10px; }
        .erp-assistant-head h2 { font-size: 16px; }
        .assistant-mark { display: inline-flex; align-items: center; justify-content: center; width: 34px; height: 34px; flex: 0 0 auto; border: 1px solid var(--border); border-radius: var(--radius-sm); background: var(--surface-2); box-shadow: var(--shadow-xs); }
        .assistant-mark .brand-mark { width: 23px; height: 23px; }
        .assistant-mark-sm { width: 25px; height: 25px; }
        .assistant-mark-sm .brand-mark { width: 17px; height: 17px; }
        .assistant-live { display: inline-flex; align-items: center; gap: 6px; margin-left: auto; color: var(--muted-foreground); font-size: 11.5px; font-weight: 600; }
        .assistant-live > span { width: 7px; height: 7px; border-radius: 50%; background: var(--success); box-shadow: 0 0 0 0 color-mix(in srgb, var(--success) 35%, transparent); animation: assistant-pulse 2.2s ease-out infinite; }
        .assistant-composer { margin: 0 12px 12px; border: 1px solid var(--input); border-radius: var(--radius); background: var(--surface); box-shadow: inset 0 1px 1px rgb(16 19 26 / 0.03); transition: border-color 120ms ease, box-shadow 120ms ease; }
        .assistant-composer:focus-within { border-color: var(--ring); box-shadow: 0 0 0 3px color-mix(in srgb, var(--ring) 17%, transparent); }
        .assistant-composer textarea { min-height: 92px; border: 0; border-radius: var(--radius); background: transparent; padding: 14px 15px 8px; box-shadow: none; resize: vertical; font-size: 15px; line-height: 1.55; }
        .assistant-composer textarea:focus-visible { border: 0; box-shadow: none; }
        .assistant-composer-foot { display: flex; align-items: center; gap: 10px; padding: 8px; border-top: 1px solid var(--border); }
        .assistant-prompts { display: flex; flex-wrap: wrap; align-items: center; gap: 6px; min-width: 0; }
        .assistant-prompt { min-height: 28px; border: 1px solid var(--border); border-radius: var(--radius-sm); background: var(--surface-2); color: var(--muted-foreground); padding: 3px 9px; font: inherit; font-size: 11.5px; font-weight: 500; cursor: pointer; }
        .assistant-prompt:hover { border-color: var(--border-strong); color: var(--foreground); background: var(--muted); }
        .assistant-submit { margin-left: auto; flex: 0 0 auto; }
        .assistant-submit .brand-mark { width: 16px; height: 16px; filter: brightness(0) invert(1); }
        .assistant-answer { margin: 0 12px 12px; border-top: 1px solid var(--border); padding: 14px 4px 4px; animation: assistant-answer-in 180ms ease-out; }
        .assistant-answer-head { display: flex; align-items: center; gap: 8px; margin-bottom: 9px; }
        .assistant-answer-head strong { font-size: 13px; }
        .assistant-answer > p { white-space: pre-wrap; font-size: 13.5px; line-height: 1.65; }
        .assistant-highlights { display: grid; gap: 6px; margin: 12px 0 0; padding: 0; list-style: none; }
        .assistant-highlights li { position: relative; padding-left: 17px; color: var(--foreground); }
        .assistant-highlights li::before { content: ""; position: absolute; left: 2px; top: 8px; width: 6px; height: 6px; border-radius: 50%; background: var(--primary); }
        .assistant-sources { display: flex; flex-wrap: wrap; align-items: center; gap: 6px; margin-top: 13px; padding-top: 10px; border-top: 1px solid var(--border); color: var(--muted-foreground); font-size: 11px; }
        .assistant-sources strong { margin-right: 2px; color: var(--foreground); }
        .assistant-sources a { border: 1px solid var(--border); border-radius: var(--radius-sm); background: var(--surface-2); color: var(--foreground); padding: 2px 6px; text-decoration: none; }
        .assistant-sources a:hover { border-color: var(--border-strong); background: var(--muted); }
        .assistant-error { margin: 0 16px 13px; }

        /* Live ERP charts */
        .analytics-grid { display: grid; grid-template-columns: minmax(230px, 0.8fr) minmax(420px, 1.8fr); gap: 12px; margin: 0 24px 16px; }
        .chart-panel { min-width: 0; border: 1px solid var(--border); border-radius: var(--radius); background: var(--card); box-shadow: var(--shadow-xs); overflow: hidden; }
        .chart-total { font-size: 12.5px; }
        .donut-wrap { display: grid; grid-template-columns: 132px 1fr; align-items: center; gap: 16px; min-height: 190px; padding: 18px; }
        .donut-chart { position: relative; width: 126px; aspect-ratio: 1; border-radius: 50%; background: var(--donut-fill); animation: donut-enter 500ms cubic-bezier(0.2, 0.7, 0.2, 1); }
        .donut-chart::after { content: ""; position: absolute; inset: 23px; border-radius: 50%; background: var(--card); }
        .donut-chart > span { position: absolute; inset: 0; z-index: 1; display: flex; flex-direction: column; align-items: center; justify-content: center; line-height: 1.15; }
        .donut-chart strong { font-size: 20px; font-variant-numeric: tabular-nums; }
        .donut-chart small { margin-top: 3px; color: var(--muted-foreground); font-size: 10.5px; }
        .donut-legend { display: grid; gap: 8px; margin: 0; padding: 0; list-style: none; }
        .donut-legend li { display: grid; grid-template-columns: 8px minmax(0, 1fr) auto; align-items: center; gap: 7px; }
        .donut-legend li > span { width: 8px; height: 8px; border-radius: 2px; }
        .donut-legend b { overflow: hidden; color: var(--muted-foreground); font-size: 11.5px; font-weight: 500; text-overflow: ellipsis; white-space: nowrap; }
        .donut-legend small { color: var(--foreground); font-size: 11px; font-variant-numeric: tabular-nums; }
        .bar-chart { display: grid; gap: 11px; padding: 16px; }
        .bar-row { display: grid; grid-template-columns: minmax(110px, 170px) 1fr auto; align-items: center; gap: 12px; }
        .bar-label { font-size: 13px; font-weight: 500; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
        .bar-label a { color: var(--foreground); }
        .bar-label a:hover { color: var(--primary); text-decoration: underline; text-underline-offset: 2px; }
        .bar-track { height: 22px; border-radius: var(--radius-sm); background: var(--surface-2); overflow: hidden; }
        .bar-fill { width: var(--bar-width); height: 100%; border-radius: var(--radius-sm); background: linear-gradient(90deg, color-mix(in srgb, var(--primary) 62%, #168b75), var(--primary)); transform-origin: left center; animation: bar-grow 520ms cubic-bezier(0.2, 0.7, 0.2, 1) var(--bar-delay) both; }
        .bar-value { min-width: 56px; text-align: right; font-size: 13px; font-weight: 600; font-variant-numeric: tabular-nums; }

        .trend-panel { margin: 0 24px 16px; border: 1px solid var(--border); border-radius: var(--radius); background: var(--card); box-shadow: var(--shadow-xs); overflow: hidden; }
        .trend-head { min-height: 64px; }
        .trend-summary { display: flex; align-items: baseline; gap: 7px; }
        .trend-summary strong { font-size: 23px; font-variant-numeric: tabular-nums; }
        .trend-summary span { color: var(--muted-foreground); font-size: 11.5px; }
        .trend-chart-wrap { overflow-x: auto; padding: 12px 15px 8px; }
        .trend-chart { display: block; min-width: 720px; width: 100%; height: auto; }
        .trend-grid line { stroke: var(--border); stroke-width: 1; }
        .trend-grid text, .trend-labels text { fill: var(--muted-foreground); font-family: var(--font-sans); font-size: 11px; }
        .trend-area { fill: color-mix(in srgb, var(--primary) 13%, transparent); animation: trend-area-in 560ms ease both; }
        .trend-line { fill: none; stroke: var(--primary); stroke-width: 3; stroke-linecap: round; stroke-linejoin: round; stroke-dasharray: 1; stroke-dashoffset: 1; animation: trend-line-draw 850ms cubic-bezier(0.2, 0.7, 0.2, 1) 80ms forwards; }
        .trend-markers circle { fill: var(--card); stroke: var(--primary); stroke-width: 3; animation: trend-point-in 220ms ease 700ms both; }

        .panel-head { display: flex; align-items: center; justify-content: space-between; gap: 12px; padding: 12px 15px; border-bottom: 1px solid var(--border); }
        .panel-head h2 { font-size: 14px; font-weight: 600; }
        .panel-head .eyebrow { display: block; margin-bottom: 2px; }
        .panel-body { padding: 15px; }

        /* ---- Data tables --------------------------------------------------------- */
        .table-scroll { overflow: auto; border-radius: var(--radius); }
        table { width: 100%; border-collapse: separate; border-spacing: 0; font-size: 13px; }
        thead th { position: sticky; top: 0; z-index: 2; background: var(--surface-2); color: var(--muted-foreground); font-size: 11.5px; font-weight: 600; letter-spacing: 0.01em; text-align: left; white-space: nowrap; padding: 9px 12px; border-bottom: 1px solid var(--border); }
        tbody td { padding: 9px 12px; border-bottom: 1px solid var(--border); vertical-align: middle; }
        tbody tr:last-child td { border-bottom: 0; }
        tbody tr { transition: background-color 100ms ease; }
        tbody tr:hover { background: color-mix(in srgb, var(--muted) 55%, transparent); }
        tbody tr[data-selected="true"] { background: color-mix(in srgb, var(--accent) 55%, transparent); }
        th.num, td.num { text-align: right; font-variant-numeric: tabular-nums; white-space: nowrap; }
        td.nowrap, th.nowrap { white-space: nowrap; }
        th.sticky-col, td.sticky-col { position: sticky; left: 0; z-index: 1; background: var(--card); }
        thead th.sticky-col { z-index: 3; background: var(--surface-2); }
        tbody tr:hover td.sticky-col { background: color-mix(in srgb, var(--muted) 92%, var(--card)); }
        .col-select { width: 36px; text-align: center; padding-left: 12px; padding-right: 0; }
        .col-actions { width: 1%; white-space: nowrap; text-align: right; }
        td a.record-link { color: var(--primary); font-weight: 500; font-variant-numeric: tabular-nums; }
        td a.record-link:hover { text-decoration: underline; text-underline-offset: 2px; }
        .table-compact thead th { padding: 6px 12px; }
        .table-compact tbody td { padding: 5px 12px; }
        .row-actions { display: inline-flex; align-items: center; gap: 4px; justify-content: flex-end; }
        .cell-strong { font-weight: 500; color: var(--foreground); }
        .cell-muted { color: var(--muted-foreground); }
        input[type="checkbox"].row-check { width: 15px; height: 15px; min-height: 0; accent-color: var(--primary); cursor: pointer; }
        /* Inline user management cell */
        .user-manage { display: grid; gap: 6px; width: max-content; }
        .user-manage-row { display: flex; align-items: center; gap: 6px; }
        .user-manage-row select { width: 210px; max-width: 100%; }
        .user-manage-row .button { flex: 0 0 auto; white-space: nowrap; }
        .user-manage .activation-inline input { margin-top: 2px; }

        /* Toolbar + filters */
        .toolbar { display: flex; flex-wrap: wrap; align-items: center; gap: 8px; margin: 0 24px 12px; }
        .toolbar-search { position: relative; flex: 1 1 260px; max-width: 360px; }
        .toolbar-search input { padding-left: 32px; }
        .toolbar-search .search-icon { position: absolute; left: 9px; top: 50%; transform: translateY(-50%); width: 15px; height: 15px; color: var(--muted-foreground); pointer-events: none; }
        .toolbar .field-inline { display: inline-flex; align-items: center; gap: 6px; }
        .toolbar .field-inline > span { color: var(--muted-foreground); font-size: 12px; font-weight: 500; white-space: nowrap; }
        .toolbar select, .toolbar .field-inline input { min-height: 34px; }
        .toolbar .spacer { flex: 1 1 auto; }
        .result-bar { display: flex; flex-wrap: wrap; align-items: center; gap: 8px 12px; margin: 0 24px 10px; color: var(--muted-foreground); font-size: 12.5px; }
        .result-bar strong { color: var(--foreground); font-weight: 600; }
        .result-bar svg { width: 14px; height: 14px; flex: 0 0 auto; vertical-align: -2px; color: var(--muted-foreground); }
        .filter-chips { display: inline-flex; flex-wrap: wrap; align-items: center; gap: 6px; }
        .filter-chip { display: inline-flex; align-items: center; gap: 6px; border: 1px solid var(--border-strong); border-radius: 999px; background: var(--surface); padding: 2px 6px 2px 10px; font-size: 12px; }
        .filter-chip .chip-key { color: var(--muted-foreground); }
        .filter-chip button { display: inline-flex; align-items: center; justify-content: center; width: 17px; height: 17px; border: 0; border-radius: 50%; background: var(--muted); color: var(--muted-foreground); cursor: pointer; padding: 0; }
        .filter-chip button:hover { background: var(--destructive); color: var(--destructive-foreground); }
        .clear-filters { color: var(--primary); font-size: 12px; font-weight: 500; cursor: pointer; background: none; border: 0; padding: 2px 4px; }
        .clear-filters:hover { text-decoration: underline; }

        .segmented { display: inline-flex; align-items: center; gap: 2px; border: 1px solid var(--border); border-radius: var(--radius-sm); background: var(--surface-2); padding: 2px; }
        .segmented a, .segmented button { display: inline-flex; align-items: center; gap: 5px; border: 0; border-radius: 5px; background: transparent; color: var(--muted-foreground); padding: 5px 10px; font: inherit; font-size: 12.5px; font-weight: 500; cursor: pointer; }
        .segmented a:hover, .segmented button:hover { color: var(--foreground); }
        .segmented a.active, .segmented button.active { background: var(--surface); color: var(--foreground); font-weight: 600; box-shadow: var(--shadow-xs); }
        .segmented svg { width: 15px; height: 15px; flex: 0 0 auto; }
        :root[data-theme="dark"] .segmented a.active, :root[data-theme="dark"] .segmented button.active { background: var(--border-strong); }

        .sort-link { display: inline-flex; align-items: center; gap: 3px; color: inherit; }
        .sort-link:hover { color: var(--foreground); }
        .sort-link svg { width: 13px; height: 13px; opacity: 0.55; }
        th[aria-sort] .sort-link svg { opacity: 1; color: var(--primary); }
        .table-sort { display: inline-flex; align-items: center; gap: 3px; border: 0; background: transparent; color: inherit; font: inherit; font-size: 11.5px; font-weight: 600; letter-spacing: 0.01em; text-transform: none; cursor: pointer; padding: 0; }
        .table-sort:hover { color: var(--foreground); }

        .pager { display: flex; align-items: center; justify-content: space-between; gap: 10px; margin: 0 24px 20px; color: var(--muted-foreground); font-size: 12.5px; }
        .pager .pager-buttons { display: flex; gap: 6px; }

        /* Bulk selection bar */
        .bulk-bar { position: sticky; bottom: 14px; z-index: 15; display: none; align-items: center; gap: 12px; margin: 0 24px 16px; border: 1px solid var(--border-strong); border-radius: var(--radius); background: var(--surface); padding: 8px 10px 8px 14px; box-shadow: var(--shadow-md); }
        .bulk-bar[data-visible="true"] { display: flex; }
        .bulk-bar strong { font-size: 13px; }
        .bulk-bar .spacer { flex: 1 1 auto; }

        /* ---- Status pills / badges ---------------------------------------------- */
        .status-pill { display: inline-flex; align-items: center; gap: 5px; border: 1px solid var(--status-neutral-bd); border-radius: 999px; background: var(--status-neutral-bg); color: var(--status-neutral-fg); padding: 2px 9px 2px 7px; font-size: 11.5px; font-weight: 500; white-space: nowrap; line-height: 1.4; }
        .status-pill::before { content: ""; width: 6px; height: 6px; border-radius: 50%; background: currentColor; flex: 0 0 auto; }
        .status-pill.tone-success { background: var(--status-success-bg); color: var(--status-success-fg); border-color: var(--status-success-bd); }
        .status-pill.tone-warning { background: var(--status-warning-bg); color: var(--status-warning-fg); border-color: var(--status-warning-bd); }
        .status-pill.tone-danger { background: var(--status-danger-bg); color: var(--status-danger-fg); border-color: var(--status-danger-bd); }
        .status-pill.tone-info { background: var(--status-info-bg); color: var(--status-info-fg); border-color: var(--status-info-bd); }
        .status-pill.tone-neutral { background: var(--status-neutral-bg); color: var(--status-neutral-fg); border-color: var(--status-neutral-bd); }
        .badge { display: inline-flex; align-items: center; gap: 4px; border: 1px solid var(--border); border-radius: var(--radius-sm); background: var(--surface-2); color: var(--muted-foreground); padding: 1px 7px; font-size: 11px; font-weight: 500; }

        /* ---- Record summary (detail header) ------------------------------------- */
        .summary-card { margin: 0 24px 16px; box-shadow: var(--shadow-xs); }
        .summary-grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(160px, 1fr)); gap: 1px; background: var(--border); border-radius: var(--radius); overflow: hidden; }
        .summary-item { display: flex; flex-direction: column; gap: 3px; padding: 12px 15px; background: var(--card); }
        .summary-item dt { color: var(--muted-foreground); font-size: 11px; font-weight: 500; }
        .summary-item dd { margin: 0; font-size: 13.5px; font-weight: 500; color: var(--foreground); word-break: break-word; }
        .summary-item dd a { color: var(--primary); }
        .summary-item dd a:hover { text-decoration: underline; }

        /* Tabs */
        .tabs { display: flex; gap: 2px; margin: 0 24px; border-bottom: 1px solid var(--border); }
        .tabs a, .tabs button { position: relative; border: 0; background: transparent; color: var(--muted-foreground); padding: 10px 12px; font: inherit; font-size: 13px; font-weight: 500; cursor: pointer; }
        .tabs a:hover, .tabs button:hover { color: var(--foreground); }
        .tabs a[aria-selected="true"], .tabs button[aria-selected="true"] { color: var(--foreground); font-weight: 600; }
        .tabs a[aria-selected="true"]::after, .tabs button[aria-selected="true"]::after { content: ""; position: absolute; left: 8px; right: 8px; bottom: -1px; height: 2px; border-radius: 2px 2px 0 0; background: var(--primary); }

        /* ---- Forms --------------------------------------------------------------- */
        .form-section { padding: 18px 20px; border-bottom: 1px solid var(--border); }
        .form-section:last-of-type { border-bottom: 0; }
        .form-section-head { display: flex; align-items: baseline; justify-content: space-between; gap: 12px; margin-bottom: 14px; }
        .form-section-head h2 { font-size: 13.5px; font-weight: 600; }
        .form-section-head p { color: var(--muted-foreground); font-size: 12px; }
        .form-grid { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); align-items: start; gap: 16px; }
        .form-stack { display: grid; gap: 14px; }
        .field { display: grid; gap: 6px; align-content: start; }
        .field.col-span-2 { grid-column: 1 / -1; }
        .field-label { display: flex; align-items: center; gap: 6px; color: var(--foreground); font-size: 12.5px; font-weight: 500; }
        .field-label .req { color: var(--destructive); }
        .field-label .opt { color: var(--muted-foreground); font-weight: 400; font-size: 11px; }
        .field-hint { color: var(--muted-foreground); font-size: 11.5px; }
        .field-error { margin: 0; color: var(--status-danger-fg); font-size: 12px; font-weight: 500; display: flex; align-items: center; gap: 5px; }
        .field-error svg { width: 13px; height: 13px; flex: 0 0 auto; }
        label { color: var(--foreground); font-size: 12.5px; font-weight: 500; }
        input, textarea, select { width: 100%; min-height: 34px; border: 1px solid var(--input); border-radius: var(--radius-sm); background: var(--surface); color: var(--foreground); padding: 6px 10px; font: inherit; font-size: 13px; transition: border-color 120ms ease, box-shadow 120ms ease; }
        select { cursor: pointer; appearance: none; background-image: url("data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' width='16' height='16' viewBox='0 0 24 24' fill='none' stroke='%2361697a' stroke-width='2.2' stroke-linecap='round' stroke-linejoin='round'%3E%3Cpath d='m6 9 6 6 6-6'/%3E%3C/svg%3E"); background-repeat: no-repeat; background-position: right 8px center; padding-right: 30px; }
        input::placeholder, textarea::placeholder { color: var(--muted-foreground); }
        input:focus-visible, textarea:focus-visible, select:focus-visible { outline: none; border-color: var(--ring); box-shadow: 0 0 0 3px color-mix(in srgb, var(--ring) 20%, transparent); }
        input[aria-invalid="true"], textarea[aria-invalid="true"], select[aria-invalid="true"] { border-color: var(--destructive); }
        input[aria-invalid="true"]:focus-visible, textarea[aria-invalid="true"]:focus-visible { box-shadow: 0 0 0 3px color-mix(in srgb, var(--destructive) 22%, transparent); }
        input:disabled, textarea:disabled, select:disabled { background: var(--surface-2); color: var(--muted-foreground); cursor: not-allowed; opacity: 1; }
        input[readonly] { background: var(--surface-2); color: var(--muted-foreground); }
        textarea { min-height: 88px; resize: vertical; }
        .input-affix { position: relative; display: flex; align-items: stretch; }
        .input-affix .affix { display: inline-flex; align-items: center; padding: 0 10px; border: 1px solid var(--input); background: var(--surface-2); color: var(--muted-foreground); font-size: 12.5px; font-variant-numeric: tabular-nums; }
        .input-affix .affix.prefix { border-right: 0; border-radius: var(--radius-sm) 0 0 var(--radius-sm); }
        .input-affix .affix.suffix { border-left: 0; border-radius: 0 var(--radius-sm) var(--radius-sm) 0; }
        .input-affix input { border-radius: 0; }
        .input-affix .affix.prefix + input { border-radius: 0 var(--radius-sm) var(--radius-sm) 0; }
        .input-affix input:has(+ .affix.suffix), .input-affix input.has-suffix { border-radius: var(--radius-sm) 0 0 var(--radius-sm); }
        .checkbox { display: inline-flex; align-items: center; gap: 8px; font-size: 13px; }
        .checkbox input { width: 16px; height: 16px; min-height: 0; accent-color: var(--primary); }
        .inline-check { display: inline-flex; align-items: center; gap: 7px; white-space: nowrap; font-size: 13px; }
        .inline-check input { width: 16px; height: 16px; min-height: 0; accent-color: var(--primary); }
        .form-actions { position: sticky; bottom: 0; grid-column: 1 / -1; display: flex; align-items: center; justify-content: flex-end; gap: 8px; margin-top: 4px; padding: 14px 20px; border-top: 1px solid var(--border); background: var(--card); border-radius: 0 0 var(--radius) var(--radius); }
        .form-actions .spacer { flex: 1 1 auto; }
        .legend { display: inline-flex; align-items: center; gap: 5px; color: var(--muted-foreground); font-size: 11.5px; }
        .legend .req { color: var(--destructive); }

        /* Autocomplete / combobox */
        .combobox { position: relative; }
        .autocomplete-results { position: absolute; left: 0; right: 0; top: calc(100% + 4px); z-index: 40; display: grid; max-height: 240px; overflow: auto; border: 1px solid var(--border); border-radius: var(--radius-sm); background: var(--surface); box-shadow: var(--shadow-md); }
        .autocomplete-option, .autocomplete-empty { width: 100%; min-height: 34px; border: 0; border-radius: 0; background: transparent; color: var(--foreground); padding: 8px 10px; text-align: left; font: inherit; font-size: 13px; }
        .autocomplete-option { cursor: pointer; }
        .autocomplete-option:hover, .autocomplete-option.is-active { background: var(--accent); color: var(--accent-foreground); outline: none; }
        .autocomplete-empty { color: var(--muted-foreground); }

        /* ---- Notices / alerts ---------------------------------------------------- */
        .notice { display: flex; align-items: flex-start; gap: 10px; margin: 0 24px 16px; border: 1px solid var(--border); border-left-width: 3px; border-radius: var(--radius); background: var(--card); padding: 12px 15px; }
        .notice svg.notice-icon { width: 17px; height: 17px; flex: 0 0 auto; margin-top: 1px; }
        .notice-body { flex: 1 1 auto; display: flex; flex-direction: column; gap: 2px; }
        .notice-body strong { font-size: 13px; font-weight: 600; }
        .notice-body span { color: var(--muted-foreground); font-size: 12.5px; }
        .notice-actions { flex: 0 0 auto; display: flex; gap: 8px; align-items: center; }
        .notice.tone-warning { border-left-color: var(--warning); }
        .notice.tone-warning svg.notice-icon { color: var(--warning); }
        .notice.tone-danger { border-left-color: var(--destructive); }
        .notice.tone-danger svg.notice-icon { color: var(--destructive); }
        .notice.tone-info { border-left-color: var(--primary); }
        .notice.tone-info svg.notice-icon { color: var(--primary); }
        .notice.tone-success { border-left-color: var(--success); }
        .notice.tone-success svg.notice-icon { color: var(--success); }
        .error, .error-list { border: 1px solid var(--status-danger-bd); border-radius: var(--radius-sm); background: var(--status-danger-bg); color: var(--status-danger-fg); padding: 10px 12px; font-size: 12.5px; }
        .error-list { grid-column: 1 / -1; display: grid; gap: 4px; }
        .error-list p { margin: 0; }
        .error-list-title { font-weight: 600; display: flex; align-items: center; gap: 6px; }
        .success { border: 1px solid var(--status-success-bd); border-radius: var(--radius-sm); background: var(--status-success-bg); color: var(--status-success-fg); padding: 10px 12px; font-size: 12.5px; }

        /* Empty states */
        .empty-state { display: flex; flex-direction: column; align-items: center; gap: 6px; padding: 40px 20px; text-align: center; color: var(--muted-foreground); }
        .empty-state .empty-icon { display: inline-flex; align-items: center; justify-content: center; width: 44px; height: 44px; border-radius: 50%; background: var(--surface-2); color: var(--muted-foreground); margin-bottom: 4px; }
        .empty-state .empty-icon svg { width: 21px; height: 21px; }
        .empty-state strong { color: var(--foreground); font-size: 14px; font-weight: 600; }
        .empty-state span { max-width: 42ch; font-size: 12.5px; }
        .empty-state .empty-actions { margin-top: 8px; display: flex; gap: 8px; }
        td .empty-state { padding: 32px 16px; }
        .empty { color: var(--muted-foreground); text-align: center; padding: 20px; }

        /* ---- Toasts -------------------------------------------------------------- */
        .toast-host { position: fixed; bottom: 16px; right: 16px; z-index: 80; display: flex; flex-direction: column; gap: 8px; max-width: min(92vw, 380px); }
        .toast { display: flex; align-items: flex-start; gap: 10px; border: 1px solid var(--border); border-left-width: 3px; border-radius: var(--radius); background: var(--surface); padding: 11px 12px; box-shadow: var(--shadow-lg); animation: toast-in 200ms cubic-bezier(0.2, 0.7, 0.2, 1) both; }
        .toast svg.toast-icon { width: 17px; height: 17px; flex: 0 0 auto; margin-top: 1px; }
        .toast .toast-body { flex: 1 1 auto; display: flex; flex-direction: column; gap: 2px; min-width: 0; }
        .toast .toast-title { font-size: 12.5px; font-weight: 600; }
        .toast .toast-msg { color: var(--muted-foreground); font-size: 12px; }
        .toast .toast-close { flex: 0 0 auto; border: 0; background: transparent; color: var(--muted-foreground); cursor: pointer; padding: 2px; border-radius: 4px; }
        .toast .toast-close:hover { background: var(--muted); color: var(--foreground); }
        .toast.tone-success { border-left-color: var(--success); } .toast.tone-success .toast-icon { color: var(--success); }
        .toast.tone-danger { border-left-color: var(--destructive); } .toast.tone-danger .toast-icon { color: var(--destructive); }
        .toast.tone-info { border-left-color: var(--primary); } .toast.tone-info .toast-icon { color: var(--primary); }
        .toast.tone-warning { border-left-color: var(--warning); } .toast.tone-warning .toast-icon { color: var(--warning); }
        .toast[data-leaving="true"] { animation: toast-out 160ms ease both; }
        @keyframes toast-in { from { opacity: 0; transform: translateY(8px) scale(0.98); } to { opacity: 1; transform: none; } }
        @keyframes toast-out { to { opacity: 0; transform: translateX(12px); } }

        /* ---- Dialogs (native <dialog>) ------------------------------------------ */
        dialog { border: 1px solid var(--border); border-radius: var(--radius-lg); background: var(--surface); color: var(--foreground); padding: 0; box-shadow: var(--shadow-lg); max-width: min(94vw, 460px); }
        dialog::backdrop { background: rgb(10 12 16 / 0.5); backdrop-filter: blur(2px); }
        .dialog-body { display: grid; gap: 14px; padding: 20px; }
        .dialog-body h2 { font-size: 15px; font-weight: 600; }
        .dialog-body p { color: var(--muted-foreground); font-size: 13px; }
        .dialog-actions { display: flex; justify-content: flex-end; gap: 8px; margin: 0; padding: 0; }
        .dialog-icon-row { display: flex; align-items: flex-start; gap: 12px; }
        .dialog-icon { display: inline-flex; align-items: center; justify-content: center; width: 36px; height: 36px; border-radius: 50%; flex: 0 0 auto; }
        .dialog-icon.tone-danger { background: var(--status-danger-bg); color: var(--status-danger-fg); }
        .dialog-icon.tone-warning { background: var(--status-warning-bg); color: var(--status-warning-fg); }
        .dialog-icon svg { width: 18px; height: 18px; }
        .confirm-form { display: grid; gap: 6px; }

        /* Feedback launcher — floating bottom-right widget */
        .feedback-trigger { position: fixed; right: 20px; bottom: 20px; z-index: 40; display: inline-flex; align-items: center; justify-content: center; width: 46px; height: 46px; padding: 0; border: 0; border-radius: 50%; background: var(--primary); color: var(--primary-foreground); cursor: pointer; box-shadow: var(--shadow-lg); transition: transform 120ms ease, background-color 120ms ease; }
        .feedback-trigger:hover { background: var(--primary-hover); transform: translateY(-1px); }
        .feedback-trigger:focus-visible { outline: 2px solid var(--ring); outline-offset: 2px; }
        .feedback-trigger svg { width: 20px; height: 20px; }

        /* First-run ERP introduction */
        .tour-root { position: fixed; inset: 0; z-index: 100; pointer-events: auto; }
        .tour-spotlight { position: fixed; z-index: 100; border: 2px solid color-mix(in srgb, var(--primary) 78%, white); border-radius: var(--radius); background: transparent; box-shadow: 0 0 0 9999px rgb(7 9 14 / 0.58), 0 0 0 5px color-mix(in srgb, var(--primary) 18%, transparent), var(--shadow-lg); pointer-events: none; transition: left 180ms ease, top 180ms ease, width 180ms ease, height 180ms ease; }
        .tour-popover { position: fixed; z-index: 101; display: flex; flex-direction: column; width: min(380px, calc(100vw - 24px)); max-height: calc(100vh - 24px); border: 1px solid var(--border-strong); border-radius: var(--radius); background: var(--surface); color: var(--foreground); box-shadow: var(--shadow-lg); overflow: hidden; pointer-events: auto; animation: tour-popover-in 160ms ease both; }
        .tour-head { display: flex; align-items: center; gap: 8px; min-height: 44px; padding: 7px 9px 7px 12px; border-bottom: 1px solid var(--border); color: var(--muted-foreground); font-size: 11.5px; font-weight: 600; }
        .tour-brand { display: inline-flex; align-items: center; justify-content: center; width: 23px; height: 23px; }
        .tour-brand .brand-mark { width: 21px; height: 21px; }
        .tour-close { width: 30px; height: 30px; margin-left: auto; }
        .tour-body { display: grid; gap: 7px; padding: 17px 18px 15px; overflow-y: auto; }
        .tour-count { color: var(--primary); font-size: 10.5px; font-weight: 700; letter-spacing: 0.04em; text-transform: uppercase; }
        .tour-body h2 { font-size: 18px; line-height: 1.3; }
        .tour-body p { color: var(--muted-foreground); font-size: 13px; line-height: 1.6; }
        .tour-actions { display: flex; align-items: center; gap: 7px; padding: 10px 12px; border-top: 1px solid var(--border); background: var(--surface-2); }
        .tour-spacer { flex: 1 1 auto; }
        .tour-actions .button svg { width: 14px; height: 14px; }
        @keyframes tour-popover-in { from { opacity: 0; transform: translateY(5px) scale(0.985); } to { opacity: 1; transform: none; } }

        /* Spinner */
        .spinner { display: inline-block; width: 14px; height: 14px; border: 2px solid color-mix(in srgb, currentColor 28%, transparent); border-top-color: currentColor; border-radius: 50%; animation: spin 0.6s linear infinite; }
        .skeleton { border-radius: var(--radius-sm); background: linear-gradient(90deg, var(--surface-2) 25%, var(--muted) 37%, var(--surface-2) 63%); background-size: 400% 100%; animation: shimmer 1.4s ease infinite; }
        @keyframes spin { to { transform: rotate(360deg); } }
        @keyframes shimmer { from { background-position: 100% 0; } to { background-position: 0 0; } }
        @keyframes bar-grow { from { transform: scaleX(0); opacity: 0.45; } to { transform: scaleX(1); opacity: 1; } }
        @keyframes donut-enter { from { transform: rotate(-28deg) scale(0.82); opacity: 0; } to { transform: none; opacity: 1; } }
        @keyframes trend-line-draw { to { stroke-dashoffset: 0; } }
        @keyframes trend-area-in { from { opacity: 0; } to { opacity: 1; } }
        @keyframes trend-point-in { from { opacity: 0; transform: scale(0.4); transform-origin: center; } to { opacity: 1; transform: none; } }
        @keyframes assistant-pulse { 70%, 100% { box-shadow: 0 0 0 7px transparent; } }
        @keyframes assistant-answer-in { from { opacity: 0; transform: translateY(5px); } to { opacity: 1; transform: none; } }

        /* ---- Auth ---------------------------------------------------------------- */
        .auth-page, .center { display: grid; min-height: 100vh; place-items: center; padding: 24px; }
        .auth-page { background: radial-gradient(1100px 520px at 50% -10%, color-mix(in srgb, var(--primary) 10%, transparent), transparent 70%), var(--background); }
        .auth-panel { width: min(100%, 400px); padding: 28px 26px; box-shadow: var(--shadow-lg); animation: auth-rise 460ms cubic-bezier(0.2, 0.7, 0.2, 1) both; }
        .auth-panel h1 { font-size: 19px; margin-bottom: 6px; }
        .auth-intro { margin-bottom: 18px; color: var(--muted-foreground); font-size: 13px; }
        .auth-brand { margin-bottom: 20px; }
        .auth-logo { display: block; height: 24px; width: auto; color: var(--foreground); }
        .auth-env { display: inline-flex; align-items: center; gap: 6px; margin-bottom: 12px; border: 1px solid var(--status-info-bd); border-radius: 999px; background: var(--status-info-bg); color: var(--status-info-fg); padding: 2px 9px; font-size: 10.5px; font-weight: 600; letter-spacing: 0.04em; text-transform: uppercase; }
        .auth-env-dot { width: 6px; height: 6px; border-radius: 50%; background: currentColor; }
        .auth-panel .form-stack label { display: grid; gap: 6px; }
        .error-card { width: min(100%, 440px); display: grid; justify-items: center; gap: 10px; padding: 32px 26px; text-align: center; box-shadow: var(--shadow-sm); }
        .error-card .error-glyph { display: inline-flex; align-items: center; justify-content: center; width: 46px; height: 46px; border-radius: 50%; background: var(--surface-2); color: var(--muted-foreground); margin-bottom: 4px; }
        .error-card .error-glyph svg { width: 22px; height: 22px; }
        .error-card h1 { font-size: 19px; }
        .error-card p { color: var(--muted-foreground); }
        .auth-overlay { position: fixed; inset: 0; z-index: 60; display: grid; place-items: center; padding: 24px; background: color-mix(in srgb, var(--background) 66%, transparent); backdrop-filter: blur(6px); -webkit-backdrop-filter: blur(6px); animation: auth-fade 200ms ease both; }
        .auth-success { display: grid; justify-items: center; gap: 12px; text-align: center; animation: auth-pop 420ms cubic-bezier(0.2, 0.7, 0.2, 1) both; }
        .auth-check { width: 62px; height: 62px; }
        .auth-check circle { stroke: var(--success); }
        .auth-check path { stroke: var(--success); stroke-dasharray: 42; stroke-dashoffset: 0; animation: auth-draw 480ms ease 120ms both; }
        .auth-success-text { font-size: 15px; font-weight: 600; }
        @keyframes auth-rise { from { opacity: 0; transform: translateY(10px); } to { opacity: 1; transform: none; } }
        @keyframes auth-fade { from { opacity: 0; } to { opacity: 1; } }
        @keyframes auth-pop { from { opacity: 0; transform: scale(0.94); } to { opacity: 1; transform: none; } }
        @keyframes auth-draw { from { stroke-dashoffset: 42; } to { stroke-dashoffset: 0; } }

        /* ---- Responsive ---------------------------------------------------------- */
        @media (max-width: 860px) {
          .app-shell { grid-template-columns: 1fr; }
          .app-shell[data-sidebar="collapsed"] { grid-template-columns: 1fr; }
          .sidebar { position: fixed; z-index: 50; left: 0; top: 0; width: 240px; transform: translateX(-100%); transition: transform 180ms ease; box-shadow: var(--shadow-lg); }
          .app-shell[data-mobile-nav="open"] .sidebar { transform: none; }
          .app-shell[data-sidebar="collapsed"] .nav-text, .app-shell[data-sidebar="collapsed"] .brand span, .app-shell[data-sidebar="collapsed"] .sidebar-toggle { display: initial; }
          .app-shell[data-sidebar="collapsed"] .nav a { justify-content: flex-start; padding: 7px 8px; }
          .scrim { position: fixed; inset: 0; z-index: 45; background: rgb(10 12 16 / 0.45); display: none; }
          .app-shell[data-mobile-nav="open"] .scrim { display: block; }
          .mobile-only { display: inline-flex !important; }
          .topbar-search { max-width: none; }
          .page-head { flex-direction: column; align-items: stretch; }
          .page-actions { flex-wrap: wrap; }
          .form-grid { grid-template-columns: 1fr; }
          .summary-grid { grid-template-columns: 1fr 1fr; }
          .analytics-grid { grid-template-columns: 1fr; }
          .user-meta { display: none; }
        }
        .mobile-only { display: none; }
        @media (max-width: 520px) {
          .summary-grid { grid-template-columns: 1fr; }
          .table-panel, .form-panel, .toolbar, .pager, .stat-grid, .result-bar, .notice, .summary-card, .tabs, .breadcrumbs, .page-head { margin-left: 12px; margin-right: 12px; padding-left: 12px; padding-right: 12px; }
          .erp-assistant, .analytics-grid, .trend-panel { margin-left: 12px; margin-right: 12px; }
          .assistant-composer-foot { align-items: stretch; flex-direction: column; }
          .assistant-submit { width: 100%; margin-left: 0; }
          .assistant-prompt { flex: 1 1 auto; }
          .donut-wrap { grid-template-columns: 112px 1fr; padding: 14px; }
          .donut-chart { width: 108px; }
          .bar-row { grid-template-columns: minmax(80px, 115px) 1fr auto; gap: 8px; }
        }

        @media (prefers-reduced-motion: reduce) {
          *, *::before, *::after { animation-duration: 0.001ms !important; animation-iteration-count: 1 !important; transition-duration: 0.001ms !important; }
        }
        @media print {
          .sidebar, .topbar, .toolbar, .pager, .bulk-bar, .feedback-trigger, .toast-host, .page-actions, .row-actions { display: none !important; }
          .app-shell { grid-template-columns: 1fr; }
        }

        """;
}
