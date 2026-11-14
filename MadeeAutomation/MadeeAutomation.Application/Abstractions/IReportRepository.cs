using MadeeAutomation.Domain.Entities;
using MadeeAutomation.Domain.Enums;

namespace MadeeAutomation.Application.Abstractions;

/// <summary>Saves one AI call's usage. Must never throw into the caller (a failed save is only logged).</summary>
public interface IAiUsageRecorder
{
    Task RecordAsync(AiUsageRecord record, CancellationToken cancellationToken = default);
}

// ---- Light rows the reports read (only the columns they need) ----

public sealed record MessageRow(string Channel, string? CustomerId, string Kind, DateTime ReceivedAtUtc);

public sealed record RunRow(ExecutionStatus Status, DateTime StartedAtUtc, string? ActionsJson);

public sealed record AiPurposeTotal(string Purpose, bool IsTest, int Calls, long InputTokens, long OutputTokens, decimal CostUsd);

/// <summary>Per client, for the MadeeVision report. Every number is for the chosen period except LastMessageUtc (ever).</summary>
public sealed class ClientActivity
{
    public Guid ClientConfigId { get; set; }
    public int Messages { get; set; }
    public int Customers { get; set; }
    public int Orders { get; set; }
    public int Bookings { get; set; }
    public int Runs { get; set; }
    public int FailedRuns { get; set; }
    public int AiCalls { get; set; }
    public long AiTokens { get; set; }
    public decimal AiCostUsd { get; set; }
    public DateTime? LastMessageUtc { get; set; }
}

/// <summary>
/// Reads what the reports need. Kept to plain filters and simple counts so it behaves the same on
/// SQL Server and in the tests; the adding-up by day / hour / item is done in ReportBuilder.
/// </summary>
public interface IReportRepository
{
    /// <summary>Customer messages and button taps received for one client in [fromUtc, toUtc).</summary>
    Task<List<MessageRow>> GetMessagesAsync(Guid clientConfigId, DateTime fromUtc, DateTime toUtc, CancellationToken cancellationToken = default);

    /// <summary>"channel:customerId" of everyone who wrote to this client before beforeUtc (to tell new from returning).</summary>
    Task<HashSet<string>> GetCustomerKeysBeforeAsync(Guid clientConfigId, DateTime beforeUtc, CancellationToken cancellationToken = default);

    /// <summary>Orders created in [fromUtc, toUtc), with their items.</summary>
    Task<List<Order>> GetOrdersAsync(Guid clientConfigId, DateTime fromUtc, DateTime toUtc, CancellationToken cancellationToken = default);

    /// <summary>Bookings whose appointment starts in [fromUtc, toUtc) OR that were made in it.</summary>
    Task<List<Booking>> GetBookingsAsync(Guid clientConfigId, DateTime fromUtc, DateTime toUtc, CancellationToken cancellationToken = default);

    /// <summary>Workflow runs started in [fromUtc, toUtc). ActionsJson only filled when it holds a staff alert.</summary>
    Task<List<RunRow>> GetRunsAsync(Guid clientConfigId, DateTime fromUtc, DateTime toUtc, CancellationToken cancellationToken = default);

    /// <summary>MadeeVision report: one row per client that had anything in [fromUtc, toUtc) or ever wrote in.</summary>
    Task<List<ClientActivity>> GetClientActivityAsync(DateTime fromUtc, DateTime toUtc, CancellationToken cancellationToken = default);

    /// <summary>MadeeVision report: messages per day, all clients. offsetMinutes shifts UTC to the admin's own day.</summary>
    Task<Dictionary<DateOnly, int>> GetMessagesPerDayAsync(DateTime fromUtc, DateTime toUtc, int offsetMinutes, CancellationToken cancellationToken = default);

    /// <summary>MadeeVision report: AI cost per day, all clients (same offset as above).</summary>
    Task<Dictionary<DateOnly, decimal>> GetAiCostPerDayAsync(DateTime fromUtc, DateTime toUtc, int offsetMinutes, CancellationToken cancellationToken = default);

    /// <summary>MadeeVision report: AI usage by purpose (handler), all clients or one, in [fromUtc, toUtc).</summary>
    Task<List<AiPurposeTotal>> GetAiByPurposeAsync(Guid? clientConfigId, DateTime fromUtc, DateTime toUtc, CancellationToken cancellationToken = default);

    /// <summary>Clients that want the monthly email: switched on, an email set, and trial / active.</summary>
    Task<List<ClientConfig>> GetMonthlyReportClientsAsync(CancellationToken cancellationToken = default);

    /// <summary>Marks the month as sent for this client. False when it was already marked (someone else sent it).</summary>
    Task<bool> TryClaimMonthlyReportAsync(Guid clientConfigId, string month, CancellationToken cancellationToken = default);

    /// <summary>Undoes a claim after the email could not be sent, so the next check tries again.</summary>
    Task ReleaseMonthlyReportAsync(Guid clientConfigId, string month, string? previousMonth, CancellationToken cancellationToken = default);
}
