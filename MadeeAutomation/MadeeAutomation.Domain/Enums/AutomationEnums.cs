namespace MadeeAutomation.Domain.Enums;

public enum TriggerType
{
    Webhook = 1,        // inbound HTTP call (POS event, lead form, generic JSON)
    Scheduled = 2,       // cron-style recurring job
    MessageReceived = 3, // WhatsApp / LINE / Messenger inbound customer message
    FileUploaded = 4,    // document dropped into a watched location (not built yet)
    Manual = 5           // triggered from the Admin dashboard (not built yet)
}

public enum ActionType
{
    SendMessage = 1,
    CallExternalApi = 2,
    GeneratePdf = 3,      // no IAction yet - the engine records it as "not available"
    CallClaude = 4,       // no IAction yet - the engine records it as "not available"
    WriteToDatabase = 5,
    NotifyInternal = 6    // notify MadeeAutomation staff, not the end customer
}

public enum ExecutionStatus
{
    Pending = 1,
    Running = 2,
    Succeeded = 3,
    Failed = 4,
    Retrying = 5,
    Skipped = 6           // e.g. client paused - nothing was done on purpose
}

/// <summary>Life cycle of one inbound webhook event stored in InboundEvents.</summary>
public enum InboundEventStatus
{
    Pending = 1,     // stored, waiting for the background worker
    Processing = 2,  // the worker has picked it up
    Done = 3,        // workflow ran (see ExecutionLog for success/failure of the run itself)
    Failed = 4,      // could not be processed at all (bad payload, crash)
    Skipped = 5      // nothing to run: client paused, no enabled workflow for the channel, ...
}

public enum VerticalPack
{
    FnBRetail = 1,
    CrmSales = 2,
    FinanceChit = 3,
    CustomerSupport = 4
}

public enum ClientStatus
{
    Trial = 1,
    Active = 2,
    Paused = 3,
    Cancelled = 4
}
