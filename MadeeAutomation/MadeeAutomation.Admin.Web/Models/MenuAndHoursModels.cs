using System.ComponentModel.DataAnnotations;
using MadeeAutomation.Domain.Entities;

namespace MadeeAutomation.Admin.Web.Models;

public class MenuItemFormModel
{
    public Guid? Id { get; set; }
    public Guid ClientConfigId { get; set; }

    [Required, StringLength(200)] public string Name { get; set; } = string.Empty;
    [StringLength(100)] public string? Category { get; set; }

    [Range(0, 10_000_000, ErrorMessage = "Enter a price between 0 and 10,000,000 (or leave blank).")]
    public decimal? Price { get; set; }

    [StringLength(500), Display(Name = "Other names")]
    public string? Aliases { get; set; }

    [Range(1, 100000, ErrorMessage = "Enter a daily limit of 1 or more (or leave blank for no limit).")]
    [Display(Name = "Daily limit")]
    public int? DailyLimit { get; set; }

    [Display(Name = "Available")] public bool IsAvailable { get; set; } = true;
    [Display(Name = "Sort order")] public int SortOrder { get; set; }
}

/// <summary>What a list's pager needs: the page numbers and the filters to keep in every link.</summary>
public class PagerModel
{
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int TotalCount { get; set; }
    public int TotalPages => Math.Max(1, (int)Math.Ceiling(TotalCount / (double)PageSize));
    public string Controller { get; set; } = string.Empty;
    public string Noun { get; set; } = "item";            // "order", "item"
    /// <summary>The current filters (search, tab, page size...). "page" is added per link.</summary>
    public Dictionary<string, string?> Route { get; set; } = new();
    public int From => TotalCount == 0 ? 0 : (Page - 1) * PageSize + 1;
    public int To => Math.Min(Page * PageSize, TotalCount);
}

public class MenuPageModel
{
    public ClientConfig Client { get; set; } = null!;

    /// <summary>The items on THIS page (after search and filters).</summary>
    public List<MenuItem> Items { get; set; } = new();

    /// <summary>All items of the client, ignoring filters.</summary>
    public int TotalItems { get; set; }

    public string? Search { get; set; }
    public string? Category { get; set; }                 // null = all, "" = no category
    public string? Status { get; set; }                   // null, "available", "soldout", "limited"
    public List<string> Categories { get; set; } = new();
    public bool IsFiltered => !string.IsNullOrWhiteSpace(Search) || Category is not null || Status is not null;
    public PagerModel Pager { get; set; } = new();
    public MenuItemFormModel NewItem { get; set; } = new();
    public bool HasOldMenuText => !string.IsNullOrWhiteSpace(Client.MenuText);

    /// <summary>Already ordered for today, per menu item id (only filled when an item has a daily limit).</summary>
    public Dictionary<Guid, int> OrderedToday { get; set; } = new();
}

public class DayHoursModel
{
    public DayOfWeek Day { get; set; }
    public bool Closed { get; set; }
    public string? Open1 { get; set; }
    public string? Close1 { get; set; }
    public string? Open2 { get; set; }
    public string? Close2 { get; set; }
}

public class HoursPageModel
{
    public ClientConfig Client { get; set; } = null!;
    public List<DayHoursModel> Days { get; set; } = new();
    public List<ClosedDate> ClosedDates { get; set; } = new();
    public string StatusText { get; set; } = string.Empty;
    public bool HoursConfigured { get; set; }
    public string LocalNowText { get; set; } = string.Empty;
}

public class OrdersPageModel
{
    public ClientConfig Client { get; set; } = null!;
    public MadeeAutomation.Application.Common.PagedResult<Order> Orders { get; set; } = new();
    public string Show { get; set; } = "open";
    public string? Search { get; set; }
    public DateOnly? Day { get; set; }                    // orders FOR this day
    public DateOnly Today { get; set; }
    public bool IsFiltered => !string.IsNullOrWhiteSpace(Search) || Day is not null;
    public PagerModel Pager { get; set; } = new();
    public int NewCount { get; set; }
    public bool HasPos { get; set; }
    public bool StaffChannelSet => !string.IsNullOrWhiteSpace(Client.StaffAlertEmail) || !string.IsNullOrWhiteSpace(Client.StaffAlertLineUserId);
}

public class PortalHomeModel
{
    public ClientConfig Client { get; set; } = null!;
    public MadeeAutomation.Domain.Regional.BusinessStatus Hours { get; set; } = null!;
    public string LocalNowText { get; set; } = string.Empty;
    public int MenuItemCount { get; set; }
    public int SoldOutCount { get; set; }
    public int NewOrderCount { get; set; }
    public List<Order> LatestNewOrders { get; set; } = new();
}

public class BusinessInfoModel
{
    public string ClientName { get; set; } = string.Empty;

    [StringLength(20000, ErrorMessage = "Keep it under 20,000 characters.")]
    [Display(Name = "Business information (FAQ)")]
    public string? KnowledgeBaseText { get; set; }

    [EmailAddress, StringLength(256), Display(Name = "Email for new orders and alerts")]
    public string? StaffAlertEmail { get; set; }

    [Display(Name = "Message the customer when I accept or cancel an order")]
    public bool NotifyCustomerOnOrderStatus { get; set; } = true;

    [Display(Name = "Monthly report email")]
    public bool MonthlyReportEnabled { get; set; } = true;
}

public class ClientUserFormModel
{
    public Guid ClientConfigId { get; set; }
    public string ClientName { get; set; } = string.Empty;

    [Required, StringLength(200), Display(Name = "Name")] public string DisplayName { get; set; } = string.Empty;
    [Required, EmailAddress, StringLength(256)] public string Email { get; set; } = string.Empty;
}
