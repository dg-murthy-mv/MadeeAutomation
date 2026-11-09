# MadeeAutomation

Standalone AI automation engine for MadeeAutomation services, separate from Merkai CRM
and POSFirst. It talks to them only over HTTP, through `MadeeAutomation.Connectors`.

## Architecture

```
MadeeAutomation.sln
├── MadeeAutomation.Domain          Entities: ClientConfig, WorkflowDefinition, ExecutionLog, InboundEvent
│                                   SettingsCipher (AES-GCM), ClientIntegrationSettings
├── MadeeAutomation.Application     IHandler / IAction, WorkflowEngine, Inbound pipeline, vertical packs
│   ├── Inbound/        ChannelPayloadParser, LineSignature, InboundEventIngestor, InboundEventProcessor
│   └── VerticalPacks/
│       ├── Messaging/  customer-message, order-parser, faq-responder (+ MessageAssistant)
│       ├── FnB/        low-stock-alert
│       ├── CRM/        lead-scoring
│       └── Finance/    invoice-extraction
├── MadeeAutomation.Infrastructure  EF Core (AutomationDbContext), repositories, ClientSettingsService
├── MadeeAutomation.Connectors      MessagingConnector (LINE/WhatsApp/Messenger), PosFirst, Merkai
├── MadeeAutomation.AI              ClaudeService (Anthropic Messages API, timeouts + retries)
├── MadeeAutomation.WebApi          Webhooks + InboundEventWorker (runs customer messages)
├── MadeeAutomation.Admin.Web       Internal dashboard: clients, credentials, workflows, logs
├── database/                       SQL scripts (no EF migrations) - @Commit = 0 dry run first
└── tests/MadeeAutomation.E2E       Playwright + NUnit end-to-end tests
```

## How a customer message flows

1. LINE (or WhatsApp / Messenger) calls `POST /api/webhooks/line/{clientCode}`.
2. WebApi checks the `X-Line-Signature` with the client's channel secret, stores every text
   message as an `InboundEvent`, and answers **200 at once**. A re-delivered event is ignored
   (unique index on client + channel + event id).
3. `InboundEventWorker` picks the event up and finds the client's **one enabled message workflow
   for that channel**.
4. `WorkflowEngine` runs the handler. `customer-message` makes one Claude call that decides
   whether the message is an order, a question or something for staff. It only uses the
   client's **knowledge base** and **menu**, and it replies in the customer's language.
5. Actions run: a POSFirst order is created when the message really is an order, and the reply
   goes back on the **same channel**. On LINE the reply token is used first (free), with a push
   message as fallback.
6. Everything is recorded in `ExecutionLog`, including every action and its result. Admin shows
   it under the client's **View Execution Logs**. If a required action fails (for example, the
   reply could not be delivered), the run is marked **Failed**.

## Webhook URLs (shown on each client's page in Admin)

| Channel | URL |
|---|---|
| LINE | `POST /api/webhooks/line/{clientCode}` |
| WhatsApp | `GET+POST /api/webhooks/whatsapp/{clientCode}` |
| Messenger | `GET+POST /api/webhooks/messenger/{clientCode}` |
| Generic (forms, POS events) | `POST /api/webhooks/generic/{clientCode}/{workflowId}` (runs synchronously) |

The old `.../{clientCode}/{workflowId}` URLs still work for LINE, WhatsApp and Messenger.

## Adding an automation for a client

1. Admin → Clients → client → **Edit client & credentials**. Paste the LINE channel secret and
   access token, the POSFirst URL and key, the knowledge base and the menu.
2. **+ New Workflow** → *Customer messages* → Channel *LINE* → Save.
3. Copy the LINE webhook URL from the client page into the LINE Developers Console.

A new kind of automation means a new `IHandler` under `VerticalPacks/<Vertical>/`, plus one
line in `Application/Common/HandlerCatalog.cs` so Admin lists it. Scrutor registers the handler
automatically.

## First-time setup

See `SETUP.md` for the current round. In short:

1. Run `database/001_Baseline_and_Round001.sql` in SSMS (first with `@Commit = 0`, then `1`).
2. Generate one encryption key and set it in **both** WebApi and Admin.Web:
   ```powershell
   $b = New-Object byte[] 32; [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($b); [Convert]::ToBase64String($b)
   cd MadeeAutomation.WebApi;    dotnet user-secrets set "Security:SettingsEncryptionKey" "<key>"
   cd ..\MadeeAutomation.Admin.Web; dotnet user-secrets set "Security:SettingsEncryptionKey" "<key>"
   ```
3. Set the Claude key in WebApi only: `dotnet user-secrets set "Anthropic:ApiKey" "sk-ant-..."`.
   Admin.Web no longer needs Claude or connector secrets.
4. Optional, for internal staff alerts: `Connectors:WhatsApp:PhoneNumberId`, `AccessToken` and
   `InternalNotifyTo` in WebApi. Without them, alerts only go to the log.

## Still to build

- Admin login, plus protection on `/api/scheduled-triggers` (Phase 2)
- A scheduler that reads the cron in `TriggerConfigJson`, and retries with backoff (Phase 3)
- Meta `X-Hub-Signature-256` checks on WhatsApp / Messenger (Phase 4)
- Typed tables for `WriteToDatabaseAction`, invoice image/PDF upload, multi-turn orders (Phase 5)
