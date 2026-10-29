# Email and form setup

Everything here can be checked from inside the CRM: sign in as the admin and open **Form integrations → Email delivery**.

## 1. Email

### What the CRM emails

| Email | When |
|---|---|
| Password reset link | Someone uses **Forgot password** |
| Reminder summary | Follow-ups, tasks and calendar entries that are due (one email per person per check) |
| Event notifications | A lead is assigned to you, a form lead arrives, a deal is won, a task is assigned to you, a calendar entry is shared with you |
| Marketing campaigns | You press **Send** on a campaign |

Reminders can be turned off with `Reminders:EmailEnabled=false`, event notifications with `Notifications:EmailEnabled=false`.
Event notifications are sent in the background, so a slow mail server never slows the page down; each message is retried up to three times.

### Easiest on a host that blocks SMTP (Render free plan): Brevo over HTTPS

Brevo can send through its web API on port 443, which no host blocks. Brevo has a free plan (check its current daily limit).

1. Create a Brevo account. Under **Senders, Domains & Dedicated IPs → Senders** add the address you want to send from and verify it (Brevo emails you a link).
2. Under **SMTP & API → API keys** create an API key and copy it.
3. Set these (Render: **Environment** tab, then redeploy):

   | Setting | Value |
   |---|---|
   | `Email__Provider` | `Brevo` |
   | `Email__ApiKey` | the API key |
   | `Email__FromAddress` | the verified sender address, exactly |
   | `Email__FromName` | the name people see, for example `Uncovering Greatness` |
   | `App__PublicBaseUrl` | your public https address |

   On your own computer use the same names with colons:

   ```powershell
   dotnet user-secrets set "Email:Provider" "Brevo"
   dotnet user-secrets set "Email:ApiKey" "your-brevo-api-key"
   dotnet user-secrets set "Email:FromAddress" "you@yourdomain.co.za"
   ```

4. Restart, then use **Form integrations → Email delivery → Send test email**. Brevo refusing the key or the sender is shown with a fix.

Leave `Email__Host` empty when using Brevo. If both a host and an API key are set, the SMTP host is used unless `Email__Provider` is `Brevo`.

### Or use any SMTP server: set it up on your own computer (Windows PowerShell)

1. Get SMTP details from your mail provider: server, port **587**, username, password.
   Gmail and Microsoft 365 need an **app password**, not your normal password.
2. Open PowerShell in the project folder (the one containing `UncoveringGreatnessCRM.csproj`) and run:

   ```powershell
   dotnet user-secrets set "Email:Host" "smtp.yourprovider.com"
   dotnet user-secrets set "Email:Port" "587"
   dotnet user-secrets set "Email:Username" "you@yourdomain.co.za"
   dotnet user-secrets set "Email:Password" "your-smtp-password"
   dotnet user-secrets set "Email:FromAddress" "you@yourdomain.co.za"
   ```

3. Stop the app and start it again (`dotnet run`). Mail settings are read at start-up.
   The console should say `Email is configured: sending via ...`.
4. Sign in as admin, open **Form integrations → Email delivery** and press **Send test email**.
   A success message means the mail server accepted it. A failure shows the server's own answer plus a plain-English suggestion.

### Typical provider settings

| Provider | Server | Port | Username |
|---|---|---|---|
| Gmail / Google Workspace | `smtp.gmail.com` | 587 | full email address, with an app password |
| Microsoft 365 | `smtp.office365.com` | 587 | full email address |
| Brevo | `smtp-relay.brevo.com` | 587 | your Brevo login, SMTP key as password |
| SendGrid | `smtp.sendgrid.net` | 587 | `apikey`, API key as password |

Confirm these with your provider. Keep `Email:UseSsl` = `true`. Port 465 is not supported by the built-in mail client.
Set `Email:FromAddress` to an address your provider allows you to send from, otherwise the server refuses the message or it lands in spam.

### On a hosted site (for example Render)

Set these as environment variables: `Email__Host`, `Email__Port`, `Email__Username`, `Email__Password`, `Email__FromAddress`
and `App__PublicBaseUrl` (your public https address, for example `https://your-app.onrender.com`).
Render's **free** plan blocks the SMTP ports (25, 465 and 587). Either use the Brevo option above (works on free) or a paid instance with an SMTP provider.

### If the test email fails

| What you see | What to change |
|---|---|
| "Username and Password not accepted" / 535 | Wrong password. Use an app password; the username is normally the full address. |
| "must issue a STARTTLS command" / "Authentication Required" | `Email:UseSsl` must be `true`, and username and password must be set. |
| "timed out" / "connection refused" | Wrong host or port, or the network blocks outgoing port 587. |
| "No such host" | Typo in `Email:Host`. |
| "Sender address rejected" / "relay" | Set `Email:FromAddress` to the same address as the username. |
| Port 465 | Use 587 instead. |
| "Brevo refused ... 401" | The API key is wrong or was deleted. Create a new one. |
| "Brevo refused ... sender" | `Email:FromAddress` must be a sender you verified in Brevo. |

## 2. Forms (Tally, Google Forms, anything that can send a webhook)

Each connected form gets a private webhook address. Every submission that arrives becomes a lead, assigned to your team in turn
(or to a person you choose), and the assigned person gets an in-app notification and an email.

### The address must be reachable from the internet

Tally and Google cannot reach `http://localhost:5080`. Pick one:

* **Deploy the CRM** on a public https address and set `App:PublicBaseUrl` to it. This is the real set-up.
* **While testing on your own computer**, use a tunnel. With [ngrok](https://ngrok.com) installed:

  ```powershell
  ngrok http 5080
  ```

  Copy the `https://....ngrok-free.app` address it prints, then:

  ```powershell
  dotnet user-secrets set "App:PublicBaseUrl" "https://YOUR-ADDRESS.ngrok-free.app"
  ```

  Restart the app. The webhook address shown in the CRM now starts with your public address. (Free ngrok addresses change each time you restart ngrok.)

### Connect a Tally form

1. In the CRM: **Form integrations → Connect a form**, choose **Tally**, give it a name, save.
2. Copy the **Webhook URL** shown on the next page.
3. In Tally: open the form → **Integrations → Webhooks → Connect**, paste the URL, save.
4. Recommended: switch on Tally's **Signing secret**, choose a secret, then enter the same secret under **Edit** in the CRM. Unsigned requests are then rejected.
5. Make sure the form has an **Email** question (a question whose label contains "email" or the email input type).
6. Submit the form once. The lead appears under **Leads** and the submission under **Recent submissions** on the connection page.

### Connect a Google Form

1. In the CRM choose **Google Forms** when connecting. The page shows a short Apps Script.
2. In the Google Form: three-dot menu → **Script editor**, paste the script, save.
3. Run `setup()` once from the editor and approve the permissions.
4. Turn on **Collect email addresses** in the form settings (or add an Email question).

### Anything else (Zapier, Make, a website form, curl)

Send a `POST` with JSON to the webhook address, for example:

```json
{ "submissionId": "abc123", "Email": "jane@acme.co.za", "Name": "Jane Dlamini", "Company": "Acme" }
```

### How answers are matched to lead fields

Questions are matched by their label: email, phone/mobile/cell, first name, surname/last name, name, job title/position, company/organisation,
industry, city, province, LinkedIn. Anything else is kept in the lead's activity. If a label is unusual, add a line under **Edit → Field mapping**, for example `Company = Your business`.
Use **Test how a submission is read** on the connection page to see exactly what a sample submission would do before going live.

Repeated deliveries of the same response are ignored, and a second submission from an existing email is added to that lead's activity instead of creating a duplicate.

## 3. Quick checklist

1. Email delivery page shows **Mail server set**, and the test email arrives.
2. A form is connected and a test submission creates a lead.
3. The person the lead was assigned to sees a notification (bell icon) and receives an email.
4. Forgot password sends a reset link to a real address.
5. `App:PublicBaseUrl` is your public https address.
