# Uncovering Greatness CRM

ASP.NET Core 9 MVC application with a JSON API, EF Core persistence and role-based security.
It turns the original screens (dashboard, pipeline, leads, companies, events, tasks, calendar, campaigns, users,
notifications, login) into a working system while keeping the existing design.

## Run it

```bash
dotnet run          # http://localhost:5080 (see Properties/launchSettings.json)
```

* Database: SQLite file at `App_Data/crm.db`, created automatically on first start (no setup).
* **Seeded users** (from `Seed:Users` in `appsettings.json`, created when the database is empty). All use password `P@ssword4321`:

  | Email | Role |
  |---|---|
  | `admin@uncoveringgreatness.co.za` | Admin |
  | `michaela@uncoveringgreatness.co.za` | Sales rep |
  | `sule@uncoveringgreatness.co.za` | Sales rep |
  | `barry@uncoveringgreatness.co.za` | Sales rep |

  Names for Michaela, Sule and Barry are first names only; edit them under **Users**. Set `Seed:MustChangePassword` to `true` to force everyone to choose a new password at first sign-in.
* **Sample data** (`Seed:SampleData`): 16 leads, 6 companies, 4 events, tasks, calendar entries and campaigns, with the leads shared evenly across the four users. Set it to `false` for a clean system.
* If `Seed:Users` is empty, one admin is created with a random one-time password written to the log.
* Seeding only happens when the database has no users. If you ran an earlier build, delete `App_Data/crm.db` and restart.
* **Change the seeded passwords (or remove them from `appsettings.json`) before deploying anywhere public.**

### Switching to SQL Server
```json
"Database": { "Provider": "SqlServer" },
"ConnectionStrings": { "Default": "Server=...;Database=UG_CRM;Trusted_Connection=True;TrustServerCertificate=True" }
```
For real schema migrations: `dotnet ef migrations add Initial`, then set `Database:UseMigrations` to `true`.

### Email
Without `Email:Host` emails are only logged (password resets, campaigns) so nothing leaves your machine.
Set the `Email` section (SMTP host, port, credentials) to send for real, and set `App:PublicBaseUrl` to your public
HTTPS address so links inside emails (reset, unsubscribe, open tracking) are correct.

Full walkthrough (email, notifications and connecting Tally / Google Forms): [docs/EMAIL_AND_FORMS_SETUP.md](docs/EMAIL_AND_FORMS_SETUP.md).
Admins can check delivery at any time under **Form integrations → Email delivery** (it sends a real test email and explains any failure).

## Roles

| | Admin | Sales rep | Staff |
|---|---|---|---|
| See leads (list, pipeline, search, dashboard, exports, company/event pages) | **all leads** | **only leads assigned to them** | only leads assigned to them (none, since staff cannot own leads) |
| View companies, events, own calendar | ✔ | ✔ | ✔ |
| Create leads/companies, tasks, log calls, notes | ✔ | ✔ | – |
| Edit / move / reassign a lead | any | their own leads | – |
| Delete leads, companies | ✔ | – | – |
| Manage events, campaigns, users, audit log | ✔ | – | – |

## Security features

* **Authentication**: cookie sessions (HttpOnly, SameSite=Lax, `__Host-` prefix + Secure in production, sliding 60 min) for the web UI;
  short-lived tamper-proof **bearer tokens** (Data Protection, 8 h) for the API.
* **Passwords**: PBKDF2 (ASP.NET Core Identity hasher, auto-upgrades), policy of 10+ chars with upper/lower/number/symbol,
  no name/email inside the password, common passwords blocked. Timing-safe handling of unknown accounts.
* **Brute-force protection**: 5 failed sign-ins locks the account for 15 min (admins can unlock) plus per-IP rate limiting on
  sign-in/reset endpoints and a global per-user/IP limiter.
* **Session invalidation**: every user has a security stamp. Password change/reset, role change or deactivation ends all
  existing sessions and API tokens immediately.
* **Forced first-login password change** for accounts created or reset by an admin.
* **Password reset** by expiring (1 h), single-use, signed link; identical response whether or not the email exists.
* **Authorisation**: deny-by-default (fallback policy), role policies, and record-level rules enforced in the service layer so the
  web UI and API can never disagree. Last active admin cannot be removed; admins cannot lock themselves out.
* **CSRF**: antiforgery on all state-changing web requests; the API accepts bearer tokens only, so it is not CSRF-exposed.
* **Headers**: strict CSP (no inline scripts), HSTS, X-Frame-Options DENY, nosniff, Referrer-Policy, Permissions-Policy, no-store on pages.
* **Input handling**: server-side validation everywhere, parameterised queries via EF Core, HTML-encoded output, URL scheme checks,
  CSV export formula-injection guard, 5 MB CSV import cap.
* **Audit log**: sign-ins (success/failure/lockout), password events, user/role changes, deletes, exports and imports.
* **Marketing compliance**: one-click unsubscribe (POST-confirmed), opted-out leads are always excluded from campaigns.

## API (`/api/v1`, JSON)

```bash
# 1. get a token
curl -X POST http://localhost:5080/api/v1/auth/token -H "Content-Type: application/json" \
     -d '{"email":"admin@uncoveringgreatness.co.za","password":"P@ssword4321"}'
# 2. use it
curl http://localhost:5080/api/v1/leads?stage=Qualified -H "Authorization: Bearer <accessToken>"
```

| Area | Endpoints |
|---|---|
| Auth | `POST auth/token` (sign in), `POST auth/refresh`, `GET auth/me`, `POST auth/change-password`, `POST auth/revoke` (sign out everywhere), `POST auth/forgot-password`, `POST auth/reset-password` |
| Leads | `GET/POST leads`, `GET/PUT/DELETE leads/{id}`, `PATCH leads/{id}/stage`, `PATCH leads/{id}/assignee`, `POST leads/{id}/notes`, `POST leads/{id}/calls`, `POST leads/{id}/star`, `GET leads/pipeline`, `GET leads/export` |
| Companies | `GET/POST companies`, `GET/PUT/DELETE companies/{id}` |
| Events | `GET/POST events`, `GET/PUT/DELETE events/{id}` |
| Tasks | `GET/POST tasks`, `PUT/DELETE tasks/{id}`, `PATCH tasks/{id}/complete` |
| Calendar | `GET calendar?year=&month=`, `POST calendar`, `GET/PUT/DELETE calendar/{id}` |
| Campaigns (admin) | `GET/POST campaigns`, `GET/PUT/DELETE campaigns/{id}`, `POST campaigns/{id}/send`, `GET campaigns/audience` |
| Users (admin) | `GET/POST users`, `GET/PUT users/{id}`, `DELETE users/{id}?transferToId=`, `POST users/{id}/reset-password`, `POST users/{id}/unlock`, `GET audit` |
| Form integrations (admin) | `GET/POST integrations/forms`, `GET/PUT/DELETE integrations/forms/{id}`, `POST integrations/forms/{id}/rotate-token`, `POST integrations/forms/{id}/preview` |
| Other | `GET dashboard`, `GET search?q=`, `GET team`, `GET notifications`, `POST notifications/{id}/read`, `POST notifications/read-all` |

List endpoints take `page`, `pageSize` and filters (leads: `q, stage, eventId, assignedToId, companyId, view=mine|followup|starred, sort`)
and return `{ items, page, pageSize, totalCount, totalPages }`. Errors are `{ "error": "...", "errors": { "Field": ["..."] } }`
with 400 / 401 / 403 / 404 / 409 / 423 / 429.

## Linking Tally / Google Forms (Form integrations)

Admins: **Form integrations → Connect a form**. Each connection gets a private webhook URL
(`/integrations/forms/<token>`). When someone submits the form, the tool POSTs it to that URL and a lead appears in the CRM within a
second, assigned to your team and with the owner notified. This is push, not polling: nothing has to be checked on a schedule.

* **Tally**: form → Integrations → Webhooks → paste the URL. Optionally enable a signing secret in Tally and enter the same secret on the connection;
  deliveries without a valid `Tally-Signature` are then rejected.
* **Google Forms**: Google has no webhooks, so the connection page generates a short Apps Script to paste into the form (Script editor, run `setup()` once).
* **Anything else** (Zapier, Make, your own site): POST JSON such as `{"fields":[{"label":"Email","value":"a@b.co"}]}` or a flat `{"Email":"a@b.co","Name":"Ann Lee"}`.
* **Field mapping is automatic**: questions labelled Email, Name / First name / Surname, Phone, Company, Job title, Industry, City, Province,
  LinkedIn, Message are recognised. Anything else is kept in the lead's activity. Override with lines like `Email = Work email` if your labels differ.
  Use **Test how a submission is read** on the connection page to check a sample payload without saving anything.
* **Assignment**: take turns across admins and reps (default), whoever has the fewest open leads, or one named person. Optionally link every lead to an event and add tags.
* **Safe by design**: unguessable URL (rotatable), optional HMAC signature, 256 KB body cap, per-connection rate limit, retried deliveries are ignored
  (de-duplicated by the form's response id), an existing email never creates a second lead (the submission is added to its activity instead), a submission
  with no valid email is rejected and logged, all text is length-limited and HTML-encoded on display. Only a name/email summary of each submission is kept, not the raw answers.
* **Requirement**: the URL must be reachable from the internet. On `localhost` Tally and Google cannot reach it: deploy over HTTPS and set `App:PublicBaseUrl`
  (or use a tunnel such as ngrok while testing).

## User management (admins)

* **Add**: name, email, job title, phone, role and a generated temporary password (the user must change it at first sign-in).
* **Edit everything**: name, email (the sign-in name, must stay unique), job title, phone, role, active/inactive, plus reset password and unlock.
  Role/status changes, or changing someone else's email, sign that user out everywhere.
* **Delete**: permanent. You choose who takes over the user's leads and assigned tasks. Notes, campaigns and activity they wrote are kept and
  shown as written by a deleted user; their personal calendar events, notifications and stars are removed. All in one transaction and audit-logged.
* Safeguards: you can't delete or deactivate yourself, and the last active admin can't be deleted, demoted or deactivated.
  To stop someone signing in without losing anything, untick "Account is active" instead of deleting.
* Upgrading from an earlier build? The user table gained columns, so delete `App_Data/crm.db` and restart to recreate it.

## Behaviour worth knowing

* **Lead visibility is enforced in one place** (`Security/LeadScope.cs`): admins see every lead, everyone else only the leads assigned to them. The same rule
  covers the web UI, the API, search, dashboard figures, company/event pages, pipeline and CSV export. A lead that is not yours returns *not found*.
* New leads created by an admin with no owner are auto-assigned to the sales rep with the fewest open leads. A rep who creates a lead keeps it (assigned to them),
  and if a rep hands a lead to a colleague they are taken back to their list because they can no longer open it.
* **Reminders and notifications** (`Services/ReminderServices.cs`): a background worker runs every few minutes and creates in-app notifications for
  lead follow-ups, tasks (due today, overdue, and a heads-up the day before) and calendar entries starting within the next hour (for the owner and everyone
  the entry is shared with). Each reminder has a dedupe key, so it is created once. Everyone then receives **one summary email** of their new reminders.
  The bell and badge refresh every minute without a page reload.
  * Needs the `Email__*` settings for real emails; without them the emails are only written to the log.
  * Settings (all optional, environment variable form `Reminders__Name`): `Enabled` (true), `EmailEnabled` (true), `IntervalMinutes` (5),
    `ActiveFromHour` (6) and `ActiveToHour` (21) in South African time so nobody is emailed at night, `EventLeadMinutes` (60).
  * Email reminders go to every active user's sign-in email address.
* Lead follow-up dates appear on the assignee's calendar automatically; calendar events are private, shared or team-wide.
* Campaigns send in the background, personalise `{{FirstName}}`, `{{FullName}}`, `{{Company}}`, track opens, and resume after a restart.
* Dates entered by users are treated as South African time (SAST); system timestamps are stored in UTC.

## Testing

```bash
dotnet test Tests/UncoveringGreatnessCRM.Tests.csproj
```

* **Unit tests**: password policy, CSV export/import safety, result/paging types, the lead-visibility rule, form payload parsing.
* **Integration tests** boot the real application (middleware, EF Core, authentication, authorisation) on a throw-away SQLite database and call the JSON API:
  health check, security headers, sign-in requirements, per-user lead visibility (list, detail, dashboard, export), role restrictions, creating and permanently
  deleting a user with lead transfer, and the audit trail.
* The `Tests/` folder is excluded from the web project in `UncoveringGreatnessCRM.csproj`, so it never ships with the app.

## Pipelines (GitHub Actions)

* `.github/workflows/ci.yml`: restore, build, run all tests with coverage, upload results, build the Docker image. Runs on pushes to `develop`, `feature/**`,
  `fix/**`, `hotfix/**` and on every pull request into `develop` or `main`.
* `.github/workflows/cd.yml`: on every merge to `main`: re-run CI, call the Render deploy hook, wait for `/healthz`, smoke-test the login page.
* Branching model, protection rules and release flow: see [`docs/BRANCHING.md`](docs/BRANCHING.md).
* Putting the repo on GitHub, how the Docker image works, running it locally with `docker compose up --build` and deploying to Render step by step: see [`docs/DEPLOYMENT.md`](docs/DEPLOYMENT.md).
* Needed once in GitHub: secret `RENDER_DEPLOY_HOOK_URL`, repository variable `APP_URL`, and a `production` environment (optional approvals).

## Accessibility and front-end notes

* Skip-to-content link, labelled landmarks, `aria-current` on the active menu item, `aria-expanded` on menus, Esc closes menus and the mobile drawer.
* The account menu, notification bell and clickable table rows are reachable and usable with the keyboard only; focus outlines are always visible.
* Text and button colours meet WCAG AA contrast (4.5:1); respects reduced-motion and increased-contrast preferences.
* Responsive layout for desktop, tablet and phone; tables scroll inside their own container instead of stretching the page.
* Feedback: success/error banners (announced to screen readers), spinner on submit buttons, double-submit protection, friendly 404/error pages.
* Performance: the sign-in background image is 440 KB (was 16 MB), static files are fingerprinted (`asp-append-version`), no scripts block rendering.

## Project layout

```
Domain/        entities + enums          Data/       DbContext + seeder
Services/      all business rules        Security/   auth, tokens, policies, headers
Controllers/   MVC controllers           Controllers/Api/   JSON API
Views/         Razor views (original design)   wwwroot/   css, js, images
Tests/         xUnit unit + integration tests  .github/    CI/CD workflows       docs/   branching guide
```

## Email setup (step by step)

1. Get SMTP details from your mail provider (Microsoft 365, Google Workspace, your hosting company, SendGrid, Brevo, etc.): host, port **587**, username, password.
2. Put them in `appsettings.json` (or, better, keep the password out of the file):
   ```bash
   dotnet user-secrets set "Email:Host" "smtp.yourprovider.com"
   dotnet user-secrets set "Email:Username" "you@yourdomain.co.za"
   dotnet user-secrets set "Email:Password" "your-smtp-password"
   dotnet user-secrets set "Email:FromAddress" "no-reply@yourdomain.co.za"
   ```
   On a server use environment variables instead: `Email__Host`, `Email__Port`, `Email__Username`, `Email__Password`, `Email__FromAddress`.
3. Keep `Email:Port` = `587` and `Email:UseSsl` = `true`. Port 465 is not supported by the built-in mail client.
4. The sender address must belong to a domain your provider lets you send from (set up SPF/DKIM on that domain or mail lands in spam).
5. Set `App:PublicBaseUrl` to your public HTTPS address (unsubscribe links, open tracking and the form webhook URL depend on it).
6. Restart. The startup log says "Email is configured" when it is active. Test with **Forgot password** to a real address, then send a small campaign to yourself.

## Deploying to Render (Docker)

1. Push this folder to a private GitHub repo (do not commit `App_Data/`).
2. Render → New → Blueprint (uses `render.yaml`) or New → Web Service → Docker. Auto-deploy is off on purpose: the GitHub Actions **Deploy** workflow
   triggers Render only after the tests pass (create a Deploy Hook in Render → Settings and store it as the `RENDER_DEPLOY_HOOK_URL` secret).
3. Use a **paid** instance (Starter or above): the free tier has an ephemeral disk, sleeps when idle, and blocks outbound SMTP ports.
4. Attach a persistent disk mounted at `/app/App_Data` (holds `crm.db` and the Data Protection keys).
5. Environment variables: `Security__TrustForwardedHeaders=true`, `App__PublicBaseUrl=https://<your public address>`, `Email__*`, `Seed__SampleData=false`.
6. **Change the seeded passwords** before the first start (edit `Seed:Users` in `appsettings.json`, or set `Seed__Users__0__Password` etc.). Seeding only runs on an empty database.
7. Run a single instance only (SQLite).
