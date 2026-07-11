namespace MadeeAutomation.Domain.Enums;

public enum TriggerType
{
    Webhook = 1,        // inbound HTTP call (WhatsApp, LINE, POS event, form submit)
    Scheduled = 2,       // cron-style recurring job
    MessageReceived = 3, // WhatsApp / LINE / Facebook inbound message
    FileUploaded = 4,    // document dropped into a watched location
    Manual = 5           // triggered from the Admin dashboard
}

public enum ActionType
{
    SendMessage = 1,
    CallExternalApi = 2,
    GeneratePdf = 3,
    CallClaude = 4,
    WriteToDatabase = 5,
    NotifyInternal = 6 // e.g. notify MadeeAutomation staff, not the end client
}

public enum ExecutionStatus
{
    Pending = 1,
    Running = 2,
    Succeeded = 3,
    Failed = 4,
    Retrying = 5
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
