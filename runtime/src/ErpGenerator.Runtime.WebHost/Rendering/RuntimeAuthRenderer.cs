using System.Net;
using ErpGenerator.Runtime.Shared.Tenants;
using static RuntimeHtml;
using static RuntimeLayoutRenderer;

internal static class RuntimeAuthRenderer
{
    public static string RenderLoginPage(ResolvedTenantEnvironment tenant, bool activated)
    {
        var message = activated
            ? """<p class="success">Activation complete. Sign in with your runtime credentials.</p>"""
            : string.Empty;
        var displayName = RuntimeUi.HumanizeTenant(tenant.TenantSlug);
        var environmentName = tenant.Environment.ToString();
        return Page($"Sign in · {displayName}", $$"""
          <main class="auth-page">
            <section class="auth-panel">
              <div class="auth-brand">{{BrandAssets.LogoSvg}}</div>
              <p class="auth-env"><span class="auth-env-dot" aria-hidden="true"></span>{{Html(environmentName)}} environment</p>
              <h1>Sign in to {{Html(displayName)}}</h1>
              <p class="auth-intro">First time here? Use the owner activation link from ErpGenerator before signing in.</p>
              {{message}}
              <form id="login-form" class="form-stack">
                <div class="field">
                  <label class="field-label" for="login-email">Email</label>
                  <input id="login-email" name="email" type="email" autocomplete="email" required />
                </div>
                <div class="field">
                  <label class="field-label" for="login-password">Password</label>
                  <input id="login-password" name="password" type="password" autocomplete="current-password" required />
                </div>
                <p id="form-error" class="error" role="alert" hidden></p>
                <button class="button button-block" type="submit">Sign in</button>
              </form>
            </section>
          </main>
          <script>
            (() => {
              const form = document.querySelector('#login-form');
              const button = form.querySelector('button[type="submit"]');
              const original = button.textContent;
              const error = document.querySelector('#form-error');
              const reset = () => { button.disabled = false; button.textContent = original; };
              form.addEventListener('submit', async (event) => {
                event.preventDefault();
                error.hidden = true;
                button.disabled = true;
                button.innerHTML = '<span class="spinner" aria-hidden="true"></span>Signing in…';
                let response;
                try {
                  response = await fetch('/api/auth/login', {
                    method: 'POST',
                    headers: { 'content-type': 'application/json' },
                    body: JSON.stringify(Object.fromEntries(new FormData(form).entries()))
                  });
                } catch (networkError) {
                  error.textContent = 'Something went wrong. Please try again.';
                  error.hidden = false; reset(); return;
                }
                if (!response.ok) {
                  error.textContent = 'Invalid runtime credentials.';
                  error.hidden = false; reset(); return;
                }
                const overlay = document.createElement('div');
                overlay.className = 'auth-overlay';
                overlay.innerHTML = '<div class="auth-success" role="status"><svg class="auth-check" viewBox="0 0 52 52" fill="none" aria-hidden="true"><circle cx="26" cy="26" r="24" stroke-width="3" opacity="0.25"></circle><path d="M15 27 l7 7 l15 -16" stroke-width="4" stroke-linecap="round" stroke-linejoin="round"></path></svg><p class="auth-success-text">Signed in</p></div>';
                document.body.appendChild(overlay);
                setTimeout(() => { window.location.href = '/'; }, 900);
              });
            })();
          </script>
          """);
    }

    public static string RenderActivationForm(string token, string? email = null, string? name = null)
    {
        var prefilled = !string.IsNullOrWhiteSpace(email) || !string.IsNullOrWhiteSpace(name);
        var intro = prefilled
            ? "You've been invited to this ERP. Confirm your details and choose a password."
            : "Set the owner credentials for this ERP. They live only inside your company's ERP, separate from your ErpGenerator account.";
        return Page("Activate your ERP", $$"""
          <main class="auth-page">
            <section class="auth-panel">
              <div class="auth-brand">{{BrandAssets.LogoSvg}}</div>
              <h1>{{(prefilled ? "Set up your account" : "Activate your ERP owner account")}}</h1>
              <p class="auth-intro">{{Html(intro)}}</p>
              <form id="activation-form" class="form-stack">
                <input type="hidden" name="token" value="{{WebUtility.HtmlEncode(token)}}" />
                <div class="field">
                  <label class="field-label" for="activation-email">Email</label>
                  <input id="activation-email" name="email" type="email" autocomplete="email" value="{{WebUtility.HtmlEncode(email ?? "")}}" required />
                </div>
                <div class="field">
                  <label class="field-label" for="activation-name">Name</label>
                  <input id="activation-name" name="displayName" autocomplete="name" value="{{WebUtility.HtmlEncode(name ?? "")}}" required />
                </div>
                <div class="field">
                  <label class="field-label" for="activation-password">Password</label>
                  <input id="activation-password" name="password" type="password" autocomplete="new-password" minlength="9" required />
                  <p class="field-hint">At least 9 characters.</p>
                </div>
                <p id="form-error" class="error" role="alert" hidden></p>
                <button class="button button-block" type="submit">Activate</button>
              </form>
            </section>
          </main>
          <script>
            (() => {
              const form = document.querySelector('#activation-form');
              const button = form.querySelector('button[type="submit"]');
              const original = button.textContent;
              const error = document.querySelector('#form-error');
              form.addEventListener('submit', async (event) => {
                event.preventDefault();
                error.hidden = true;
                button.disabled = true;
                button.innerHTML = '<span class="spinner" aria-hidden="true"></span>Activating…';
                let response;
                try {
                  response = await fetch('/api/activate', {
                    method: 'POST',
                    headers: { 'content-type': 'application/json' },
                    body: JSON.stringify(Object.fromEntries(new FormData(form).entries()))
                  });
                } catch (networkError) {
                  error.textContent = 'Something went wrong. Please try again.';
                  error.hidden = false; button.disabled = false; button.textContent = original; return;
                }
                if (!response.ok) {
                  error.textContent = 'Activation failed. The token may be expired or already used.';
                  error.hidden = false; button.disabled = false; button.textContent = original; return;
                }
                window.location.href = '/login?activated=1';
              });
            })();
          </script>
          """);
    }
}
