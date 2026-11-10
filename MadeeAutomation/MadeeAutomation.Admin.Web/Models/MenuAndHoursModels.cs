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

    [Display(Name = "Available")] public bool IsAvailable { get; set; } = true;
    [Display(Name = "Sort order")] public int SortOrder { get; set; }
}

public class MenuPageModel
{
    public ClientConfig Client { get; set; } = null!;
    public List<MenuItem> Items { get; set; } = new();
    public MenuItemFormModel NewItem { get; set; } = new();
    public bool HasOldMenuText => !string.IsNullOrWhiteSpace(Client.MenuText);
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
