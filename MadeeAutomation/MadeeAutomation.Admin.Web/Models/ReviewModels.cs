using System.ComponentModel.DataAnnotations;
using MadeeAutomation.Application.Reviews;

namespace MadeeAutomation.Admin.Web.Models;

/// <summary>Reviews page: the business's review links.</summary>
public class ReviewLinksForm
{
    public Guid ClientConfigId { get; set; }

    [Display(Name = "Google review link")]
    [StringLength(500)]
    public string? GoogleReviewUrl { get; set; }

    [Display(Name = "Facebook review link (optional)")]
    [StringLength(500)]
    public string? FacebookReviewUrl { get; set; }

    [Display(Name = "Send the review links after the rating question")]
    public bool Enabled { get; set; } = true;
}

/// <summary>Reviews page: a review the owner wants a reply for.</summary>
public class ReviewDraftForm
{
    public Guid ClientConfigId { get; set; }

    [Display(Name = "Stars")]
    [Range(1, 5)]
    public int? Stars { get; set; }

    [Display(Name = "Reviewer's name (optional)")]
    [StringLength(100)]
    public string? ReviewerName { get; set; }

    [Display(Name = "The review")]
    [StringLength(ReviewReplyWriter.MaxReviewLength)]
    public string? Review { get; set; }

    [Display(Name = "Tone (optional)")]
    [StringLength(200)]
    public string? Tone { get; set; }
}

public class ReviewsPageModel
{
    public required ReviewDashboard Dashboard { get; init; }
    public ReviewLinksForm Links { get; set; } = new();
    public ReviewDraftForm Draft { get; set; } = new();

    /// <summary>The AI reply, after "Write a reply".</summary>
    public string? DraftText { get; set; }
    public string? DraftError { get; set; }
}
