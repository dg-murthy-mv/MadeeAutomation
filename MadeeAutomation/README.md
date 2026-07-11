# MadeeAutomation

Standalone AI automation engine for MadeeAutomation services — separate from Merkai CRM
and POSFirst, integrating with them only over HTTP via `MadeeAutomation.Connectors`.

## Architecture

```
MadeeAutomation.sln
├── MadeeAutomation.Domain          Entities: ClientConfig, WorkflowDefinition, ExecutionLog
├── MadeeAutomation.Application     ITrigger / IHandler / IAction, WorkflowEngine, vertical packs
│   └── VerticalPacks/
│       ├── FnB/          WhatsAppOrderParserHandler, LowStockAlertHandler
│       ├── CRM/          LeadScoringHandler
│       ├── Finance/      InvoiceExtractionHandler
│       └── Support/      FaqResponderHandler
├── MadeeAutomation.Infrastructure  EF Core (AutomationDbContext), repositories
├── MadeeAutomation.Connectors      MerkaiApiConnector, PosFirstApiConnector, MessagingConnector
├── MadeeAutomation.AI              ClaudeService (wraps api.anthropic.com/v1/messages)
├── MadeeAutomation.WebApi          Webhook ingestion (WhatsApp, LINE, generic, scheduled)
└── MadeeAutomation.Admin.Web       Internal MVC dashboard: clients, workflows, execution logs
```

This mirrors the five-project clean architecture pattern used in Merkai CRM
(Domain / Application / Infrastructure / Web / WebApi), extended with `Connectors` and `AI`.

## How it fits together

1. An event happens (WhatsApp message, POS webhook, scheduled tick).
2. `MadeeAutomation.WebApi` receives it and wraps it into a `TriggerContext`.
3. `WorkflowEngine` (in `Application/Common`) looks up the `WorkflowDefinition`, resolves the
   matching `IHandler` by `HandlerKey`, and runs it.
4. The handler does the actual work (often calling `IClaudeService` for extraction/reasoning),
   and returns a list of `ActionRequest`s.
5. `WorkflowEngine` executes each `IAction` (send message, write to DB, call external API, notify staff).
6. Every run is recorded in `ExecutionLog` — visible in the Admin dashboard under a client's
   "View Execution Logs" — so you can prove ROI and debug fast instead of "launch and leave."

## Adding a new automation for a client

No code changes needed for most cases:
1. Open `MadeeAutomation.Admin.Web` → Clients → select client → **+ New Workflow**.
2. Pick a `HandlerKey` from the dropdown (one of the pre-built vertical pack handlers).
3. Pick the `TriggerType` and fill in `TriggerConfigJson` (e.g. webhook path or cron schedule).
4. Save — the workflow is now live.

Adding a genuinely new automation type = add a new `IHandler` implementation under
`VerticalPacks/<Vertical>/`, following the pattern in the existing handlers. Scrutor
auto-registers it — no manual DI wiring required.

## First-time setup

### 1. Prerequisites
- .NET 8 SDK
- SQL Server (LocalDB is fine for development)

### 2. Restore & build
```bash
cd MadeeAutomation
dotnet restore
dotnet build
```

### 3. Configure secrets (do NOT put real keys in appsettings.json)
For each of `MadeeAutomation.WebApi` and `MadeeAutomation.Admin.Web`:
```bash
cd MadeeAutomation.WebApi
dotnet user-secrets set "Anthropic:ApiKey" "sk-ant-..."
dotnet user-secrets set "Connectors:Merkai:ApiKey" "..."
dotnet user-secrets set "Connectors:PosFirst:ApiKey" "..."
dotnet user-secrets set "Connectors:WhatsApp:AccessToken" "..."
dotnet user-secrets set "Connectors:Line:ChannelAccessToken" "..."
```
Repeat for `MadeeAutomation.Admin.Web` (it also talks to Claude/connectors directly for now;
you can later move all trigger/action execution behind WebApi only and make Admin.Web
purely a management UI over the database).

### 4. Create the database
From `MadeeAutomation.Infrastructure` (with `MadeeAutomation.WebApi` as startup project):
```bash
dotnet tool install --global dotnet-ef   # if not already installed
dotnet ef migrations add InitialCreate --startup-project ../MadeeAutomation.WebApi
dotnet ef database update --startup-project ../MadeeAutomation.WebApi
```

### 5. Run
```bash
# Terminal 1 - webhook ingestion API
cd MadeeAutomation.WebApi
dotnet run

# Terminal 2 - internal admin dashboard
cd MadeeAutomation.Admin.Web
dotnet run
```

Open the Admin.Web URL shown in the console, add your first client (e.g. MadeeBakers),
add a workflow using `whatsapp-order-parser`, and point your WhatsApp Business API
webhook at:
```
POST https://<your-webapi-host>/api/webhooks/whatsapp/{clientCode}/{workflowId}
```

## Notes / things to decide next
- `WriteToDatabaseAction` is currently a stub — decide on typed tables per vertical
  (e.g. `ExtractedInvoices`, `ReconciledPayments`) rather than a generic dynamic write.
- Scheduled triggers currently need an external caller hitting
  `POST /api/scheduled-triggers/run/{clientConfigId}` (e.g. a simple cron job or
  Windows/Linux scheduled task hitting that URL, or a `BackgroundService` you add later).
- `IntegrationSettingsJson` on `ClientConfig` should be encrypted at rest before production use —
  it will hold each client's WhatsApp number, Merkai/POSFirst API keys, etc.
- Authentication/authorization has intentionally been left out of `Admin.Web` and the
  webhook endpoints in this starter — add ASP.NET Core Identity (or Azure AD) on
  `Admin.Web` and signature verification (e.g. Meta's `X-Hub-Signature-256`) on
  `WebhooksController` before this touches real client data.
