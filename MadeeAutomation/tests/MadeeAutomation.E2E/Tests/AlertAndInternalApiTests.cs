using System.Net;
using System.Text;
using MadeeAutomation.E2E.Infrastructure;
using NUnit.Framework;

namespace MadeeAutomation.E2E.Tests;

/// <summary>Round 002: fallback replies, staff alerts, and the X-Internal-Key protected endpoints.</summary>
[TestFixture]
[NonParallelizable]
public class AlertAndInternalApiTests
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };
    private static string Api => GlobalSetup.Settings.WebApiUrl;

    private static async Task<HttpStatusCode> PostAsync(string path, string? key, string body = "{}")
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Api + path)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        if (key is not null) request.Headers.Add("X-Internal-Key", key);
        using var response = await Http.SendAsync(request);
        return response.StatusCode;
    }

    [Test]
    public async Task T42_AI_outage_sends_the_fallback_reply_and_alerts_staff_by_email()
    {
        var user = Line.NewUser("t42");
        await Line.SendAsync(World.Bakery, Line.TextEvent(user, "[aidown] 2 croissants"));

        var calls = await Waits.LineCallsAsync(user);
        Assert.That(calls[0].Kind, Is.EqualTo("reply"));
        Assert.That(calls[0].Text, Is.EqualTo("Thanks for your message! A member of our team will reply shortly."),
            "our side is English-only: the default fallback is English for every country");

        var log = await Waits.FinishedLogAsync(user);
        Assert.That(log["Status"], Is.EqualTo(4));
        Assert.That(log["ResultSummary"], Is.EqualTo("Fallback reply sent; staff alerted."));
        Assert.That(GlobalSetup.Fakes.OrdersFor(user), Is.Empty);

        // The bakery owner also gets "New order" emails from other tests - wait for THIS customer's alert.
        var mail = await Eventually.GetAsync(() => Mailbox.To("owner@bakery.test").Where(m => m.Body.Contains(user)).ToList(),
            m => m.Count > 0, 15000, "alert email for t42");
        var alert = mail.Last();
        Assert.That(alert.Subject, Does.Contain("E2E Bakery needs attention"));
        Assert.That(alert.Body, Does.Contain("please follow up"));
    }

    [Test]
    public async Task T43_Undeliverable_reply_alerts_staff()
    {
        var user = Line.NewUser("t43");
        await Line.SendAsync(World.FailSend, Line.TextEvent(user, "hello"));

        var log = await Waits.FinishedLogAsync(user);
        Assert.That(log["Status"], Is.EqualTo(4));
        Assert.That((string)log["ActionsJson"]!, Does.Contain("NotifyInternal"));

        var mail = await Eventually.GetAsync(() => Mailbox.To("ops@failsend.test").Where(m => m.Body.Contains(user)).ToList(),
            m => m.Count > 0, 15000, "alert email for t43");
        Assert.That(mail[0].Body, Does.Contain("could not be delivered"));
    }

    [Test]
    public async Task T44_Scheduled_triggers_need_the_internal_key()
    {
        var path = $"/api/scheduled-triggers/run/{World.Bakery.Id}";
        Assert.That(await PostAsync(path, null), Is.EqualTo(HttpStatusCode.Unauthorized));
        Assert.That(await PostAsync(path, "wrong-key"), Is.EqualTo(HttpStatusCode.Unauthorized));
        Assert.That(await PostAsync(path, E2ESettings.InternalApiKey), Is.EqualTo(HttpStatusCode.OK));
    }

    [Test]
    public async Task T45_Workflow_test_endpoint_needs_the_internal_key()
    {
        var path = $"/api/internal/workflows/{World.Bakery.WorkflowId}/test";
        Assert.That(await PostAsync(path, null, "{\"message\":\"hello\"}"), Is.EqualTo(HttpStatusCode.Unauthorized));
        Assert.That(await PostAsync(path, E2ESettings.InternalApiKey, "{\"message\":\"hello\"}"), Is.EqualTo(HttpStatusCode.OK));
    }
}
