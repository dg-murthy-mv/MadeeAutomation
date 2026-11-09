# Round 001: LINE done properly, the Admin screens it needs, and Playwright E2E

## What changed

**LINE pipeline**
- There is now one webhook URL per client: `/api/webhooks/line/{clientCode}`. The old per-workflow URL still works.
- Every call's `X-Line-Signature` is checked. Calls are refused (401) until the client's channel secret is saved in Admin.
- The webhook stores every event and answers 200 at once. A background worker then runs the workflow and replies.
- A re-delivered event is processed only once, so there are no duplicate orders.
- Replies use the LINE reply token, which is free. If the token has expired, a push message is sent instead.
- Replies always go back on the channel the message came from. The FAQ bot no longer answers on WhatsApp.
- The new **customer-message** handler takes orders from the client's **menu**, answers from the client's **knowledge base**, and hands everything else to staff. It replies in the customer's language. "hello" no longer creates an order.
- The three duplicate order parsers are now one handler, `order-parser`. Existing workflows are moved to it by the SQL script.

**Reliability**
- If a reply never reached the customer, the run is now marked **Failed**. Every action's result is saved.
- Paused or cancelled clients are **Skipped**.
- Claude calls now have timeouts and retry on errors such as 429 or 529.

**Security**
- Client credentials are stored **encrypted** with AES-GCM. Saved secrets are never shown again; Admin only shows a masked hint like ••••1234.
- The Messenger token is no longer in the URL, so it no longer ends up in log files.
- Meta verification answers on the same URL that receives messages, and a wrong verify token returns 403 instead of crashing with a 500.

**Admin**
- New Edit client page: credentials, knowledge base, menu and status.
- The client page shows each channel's webhook URL with a Copy button and whether its credentials are set.
- Workflows can be edited and turned on or off, and each has a channel. Only one enabled message workflow is allowed per channel.
- New execution-log detail page showing actions, payload, channel and customer.
- Controllers no longer use the DbContext directly; they go through repositories.

**Tests**
- New `tests/MadeeAutomation.E2E` project: Playwright with NUnit, using its own database and a fake LINE, Claude and POSFirst server. It has **28 tests**.

## Step 1: Copy the files

1. Close Visual Studio.
2. Unzip over `C:\Users\dgmur\source\repos\MadeeAutomation\MadeeAutomation\` and overwrite when asked.
3. **Delete these files.** They were replaced, and the build fails if they stay:
   ```
   MadeeAutomation.Application\VerticalPacks\FnB\WhatsAppOrderParserHandler.cs
   MadeeAutomation.Application\VerticalPacks\FnB\LineOrderParserHandler.cs
   MadeeAutomation.Application\VerticalPacks\FnB\MessengerOrderParserHandler.cs
   MadeeAutomation.Application\VerticalPacks\Support\FaqResponderHandler.cs
   MadeeAutomation.Infrastructure\Persistence\Repositories.cs
   MadeeAutomation.Admin.Web\Views\Clients\Create.cshtml
   MadeeAutomation.Admin.Web\Views\Clients\CreateWorkflow.cshtml
   ```
4. Open the solution. The new **tests → MadeeAutomation.E2E** project appears in Solution Explorer.

## Step 2: Database (SSMS, on the `MadeeAutomation` database)

1. Open `database\001_Baseline_and_Round001.sql` and run it as it is (`@Commit = 0`).
   - The last message should read `=== DRY RUN - rolled back ===`.
   - Read the messages. You should see the new columns, the `InboundEvents` table, and how many old order-parser workflows were moved.
2. Change `@Commit` to `1` and run it again. The last message should read `=== COMMITTED ===`.

The script only creates what is missing, so it is safe on your existing database.

## Step 3: Secrets

In PowerShell, generate one key:
```powershell
$b = New-Object byte[] 32; [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($b); [Convert]::ToBase64String($b)
```

Then set the **same** key in both apps, and the Claude key in WebApi only:
```powershell
cd MadeeAutomation.WebApi
dotnet user-secrets set "Security:SettingsEncryptionKey" "<the key>"
dotnet user-secrets set "Anthropic:ApiKey" "sk-ant-..."        # skip if already set
cd ..\MadeeAutomation.Admin.Web
dotnet user-secrets set "Security:SettingsEncryptionKey" "<the key>"
```

Keep the key safe. If you lose it, saved credentials can't be read, and you'll have to type them in again.

## Step 4: Build

Build the solution with **Ctrl+Shift+B**. You should get **0 errors**. The first build restores the new test packages (NUnit, Playwright, SqlClient), so it needs internet.

## Step 5: Set up MadeeVision on LINE

1. Run **WebApi** and **Admin.Web**, and start ngrok pointing at WebApi as before.
2. In Admin, set the ngrok address as the public URL, so the copy buttons show the right link. Either add `"WebApi": { "PublicBaseUrl": "https://<your-ngrok>.ngrok-free.dev" }` to Admin.Web user-secrets, or just copy the path.
3. Admin → your client → **Edit client & credentials**:
   - **LINE channel secret:** LINE Official Account Manager → Settings → Messaging API → *Channel secret*.
   - **LINE channel access token:** LINE Developers Console → your channel → *Messaging API* tab → *Channel access token (long-lived)*. Click **Issue** if there isn't one yet.
   - **Knowledge base:** opening hours, delivery area, payment methods and so on.
   - **Menu:** one item per line, with prices.
   - Save. The client page should now show **Channel secret saved** and **Access token saved**.
4. Check the client's workflow. Your old LINE workflow now shows `order-parser` on **LINE**. Click **Edit** and change the handler to **Customer messages (customer-message)** so it also answers questions.
5. LINE Developers Console → Messaging API → **Webhook URL:** `https://<your-ngrok>.ngrok-free.dev/api/webhooks/line/<clientCode>`. Click **Verify**; it should show **Success**.
6. On your phone, send the account three messages: `hello`, `what time do you open?`, and an order such as `2 croissants`.
   - Each should get a reply in a second or two.
   - Admin → **View Execution Logs** should show three Succeeded rows. Click one to see the actions.
   - Until POSFirst is connected, orders get `MOCK-...` references.

## Step 6: Run the E2E tests

**One-time setup:**
```powershell
cd tests\MadeeAutomation.E2E
pwsh bin\Debug\net8.0\playwright.ps1 install chromium
```
If you don't have `pwsh`, run `powershell -File bin\Debug\net8.0\playwright.ps1 install chromium` instead.

Check `tests\MadeeAutomation.E2E\e2e.settings.json`:
- `SqlServer` must point at your SQL Server. It is `DG_MURTHY` now.
- Ports **58801–58803** must be free.

The test run **creates and drops** a separate database called `MadeeAutomation_E2E`. Your real database is never touched, and the apps run with `ASPNETCORE_ENVIRONMENT=E2E`, so your user-secrets are not loaded.

**Each run:**
1. Build the solution first. The tests start WebApi and Admin.Web from their `bin\Debug` folders.
2. Then run:
   ```powershell
   dotnet test tests\MadeeAutomation.E2E
   ```
   You can also use Test Explorer → Run All.

**Expected:** `Passed: 28, Failed: 0`. The run takes about 2 minutes.

| Fixture | Tests |
|---|---|
| LineWebhookTests | T01–T20: signature, duplicates, batching, order/FAQ/greeting/hand-over, reply-token fallback, failed send, paused client, missing workflow, old URL, slow AI, bad AI answer, old plain-JSON credentials |
| AdminTests | T21–T28: create client, duplicate code, encrypted credentials (keep/remove), UI-configured client answers on LINE, one workflow per channel, JSON validation, log detail, turning a workflow off stops replies |

If anything fails, send me:
- the test output (the name and message of each failing test)
- `tests\MadeeAutomation.E2E\TestResults\webapi.out.log`
- `tests\MadeeAutomation.E2E\TestResults\adminweb.out.log`

## Changes in behaviour to know about

- **LINE calls get 401 until the channel secret is saved** for that client. This is on purpose.
  - For a quick local test only, you can set `"Webhooks": { "RequireLineSignature": false }` in WebApi, but never in production.
- **The POSFirst order body changed.** It now sends `items: [{ name, quantity }]`, `requestedDeliveryTime`, `customerNote`, `customerId` and `source`. Before, `parsedOrder` was a JSON string. POSFirst's `/api/orders` endpoint will need to accept this shape when you connect it.
- **Admin.Web no longer needs** `Anthropic:*` or `Connectors:*` secrets. You can remove them from its user-secrets.
- **WebApi no longer reads** `Connectors:Merkai:*`, `Connectors:PosFirst:*` or `Connectors:Line:ChannelAccessToken`. These credentials are per client now and are set in Admin.
- **Meta Graph API version** defaults to `v23.0`. You can change it with `Connectors:Meta:GraphApiVersion`.

## What was checked before sending

- **All 8 projects compile cleanly**, including every Razor view. I had no NuGet access, so this was against stand-ins for the NuGet packages.
- **The real controllers, views, worker, handlers and connectors** were run in a test host with in-memory data, a fake LINE/Claude/POSFirst server and Playwright (Chromium). That covered webhook to reply, signature 401, unknown client 404, creating clients and checking codes, encrypted credentials (keep/remove, never in the HTML), the one-workflow-per-channel rule, JSON validation, log detail, and turning a workflow off. All checks passed.
- **Not run here (no SQL Server):** the SQL script, the EF queries, and the full E2E suite against a real database. Step 6 covers those on your machine.

## Next round (Phase 2)

- Admin login.
- Protection on `/api/scheduled-triggers`.
- A **Test** button on workflows, so you can send a sample message from Admin.
- An inbound-events page that shows skipped and failed webhook calls.
