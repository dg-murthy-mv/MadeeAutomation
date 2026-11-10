using System.Net;
using MadeeAutomation.E2E.Infrastructure;
using NUnit.Framework;

namespace MadeeAutomation.E2E.Tests;

/// <summary>
/// The LINE pipeline end to end: signed webhook -> InboundEvents -> worker -> AI (fake) ->
/// POSFirst (fake) -> reply on LINE (fake). Statuses: ExecutionStatus 3 Succeeded, 4 Failed, 6 Skipped;
/// InboundEventStatus 3 Done, 5 Skipped.
/// </summary>
[TestFixture]
[NonParallelizable]
public class LineWebhookTests
{
    private static FakeServer Fakes => GlobalSetup.Fakes;

    [Test]
    public async Task T01_Order_on_LINE_creates_POS_order_and_replies_with_reply_token()
    {
        var user = Line.NewUser("t01");
        var response = await Line.SendAsync(World.Bakery, Line.TextEvent(user, "2 croissants please"));

        Assert.That(response.Status, Is.EqualTo(HttpStatusCode.OK), response.Body);
        Assert.That(response.Int("received"), Is.EqualTo(1));

        var calls = await Waits.LineCallsAsync(user);
        Assert.That(calls[0].Kind, Is.EqualTo("reply"), "should answer with the free reply API");
        Assert.That(calls[0].ReplyToken, Is.EqualTo("rt-" + user));
        Assert.That(calls[0].Authorization, Is.EqualTo("Bearer bakery-token-1234"), "must use the client's own token");

        var orders = Fakes.OrdersFor(user);
        Assert.That(orders, Has.Count.EqualTo(1));
        Assert.That(orders[0].BodyJson, Does.Contain("Croissant"));
        Assert.That(orders[0].BodyJson, Does.Contain("\"quantity\":2"));
        Assert.That(orders[0].ApiKey, Is.EqualTo("pos-key-bakery"));
        Assert.That(calls[0].Text, Does.Contain(orders[0].OrderId), "reply must carry the POS order ref");

        var log = await Waits.FinishedLogAsync(user);
        Assert.That(log["Status"], Is.EqualTo(3), $"{log["ErrorMessage"]}");
        Assert.That(log["Channel"], Is.EqualTo("line"));
        Assert.That((string)log["ResultSummary"]!, Does.StartWith($"Order {orders[0].OrderId} created"));
        Assert.That((string)log["ActionsJson"]!, Does.Contain("Sent via line (reply)"));
    }

    [Test]
    public async Task T02_Invalid_signature_is_refused_and_nothing_is_stored()
    {
        var user = Line.NewUser("t02");
        var body = Line.Body(Line.TextEvent(user, "2 croissants"));

        var response = await Line.PostAsync($"/api/webhooks/line/{World.Bakery.Code}", body, "wrong-secret");

        Assert.That(response.Status, Is.EqualTo(HttpStatusCode.Unauthorized));
        await Waits.QuietPeriodAsync();
        Assert.That(await Waits.EventsAsync(user), Is.Empty);
        Assert.That(Fakes.OrdersFor(user), Is.Empty);
    }

    [Test]
    public async Task T03_Missing_signature_header_is_refused()
    {
        var user = Line.NewUser("t03");
        var response = await Line.PostAsync($"/api/webhooks/line/{World.Bakery.Code}", Line.Body(Line.TextEvent(user, "hello")), null);

        Assert.That(response.Status, Is.EqualTo(HttpStatusCode.Unauthorized));
        Assert.That(await Waits.EventsAsync(user), Is.Empty);
    }

    [Test]
    public async Task T04_Client_without_channel_secret_is_refused()
    {
        var user = Line.NewUser("t04");
        var body = Line.Body(Line.TextEvent(user, "hello"));
        var response = await Line.PostAsync($"/api/webhooks/line/{World.NoSecret.Code}", body, "anything");

        Assert.That(response.Status, Is.EqualTo(HttpStatusCode.Unauthorized));
        Assert.That(response.Body, Does.Contain("not configured"));
        Assert.That(await Waits.EventsAsync(user), Is.Empty);
    }

    [Test]
    public async Task T05_Redelivered_event_is_processed_once()
    {
        var user = Line.NewUser("t05");
        var evt = Line.TextEvent(user, "3 croissants", eventId: "E-redeliver-" + user);

        var first = await Line.SendAsync(World.Bakery, evt);
        var second = await Line.SendAsync(World.Bakery, evt);

        Assert.That(first.Int("received"), Is.EqualTo(1));
        Assert.That(second.Status, Is.EqualTo(HttpStatusCode.OK), "duplicates still get 200 so LINE stops re-sending");
        Assert.That(second.Int("received"), Is.EqualTo(0));
        Assert.That(second.Int("duplicates"), Is.EqualTo(1));

        await Waits.FinishedLogAsync(user);
        await Waits.QuietPeriodAsync();

        Assert.That(Fakes.OrdersFor(user), Has.Count.EqualTo(1), "exactly one POS order");
        Assert.That(Fakes.LineCallsFor(user), Has.Count.EqualTo(1), "exactly one reply");
        Assert.That(await Waits.EventsAsync(user), Has.Count.EqualTo(1));
        Assert.That(await Waits.LogsAsync(user), Has.Count.EqualTo(1));
    }

    [Test]
    public async Task T06_Batched_events_are_all_processed_and_stickers_ignored()
    {
        var a = Line.NewUser("t06a");
        var b = Line.NewUser("t06b");

        var response = await Line.SendAsync(World.Bakery,
            Line.TextEvent(a, "hello"), Line.StickerEvent(a), Line.TextEvent(b, "hi there"));

        Assert.That(response.Int("received"), Is.EqualTo(2));
        Assert.That(response.Int("ignored"), Is.EqualTo(1));

        await Waits.LineCallsAsync(a);
        await Waits.LineCallsAsync(b);
    }

    [Test]
    public async Task T07_LINE_console_Verify_call_gets_200()
    {
        var response = await Line.SendAsync(World.Bakery);   // { "events": [] }
        Assert.That(response.Status, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(response.Int("received"), Is.EqualTo(0));
    }

    [Test]
    public async Task T08_Question_is_answered_from_the_knowledge_base_on_LINE()
    {
        var user = Line.NewUser("t08");
        await Line.SendAsync(World.Bakery, Line.TextEvent(user, "What time do you open?"));

        var calls = await Waits.LineCallsAsync(user);
        Assert.That(calls[0].Kind, Is.EqualTo("reply"), "answers on LINE - not WhatsApp");
        Assert.That(calls[0].Text, Does.Contain("7am-6pm"));

        var log = await Waits.FinishedLogAsync(user);
        Assert.That(log["ResultSummary"], Is.EqualTo("Answered from knowledge base."));
        Assert.That(Fakes.OrdersFor(user), Is.Empty);

        var prompt = Fakes.ClaudeCalls.Last(c => c.UserMessage == "What time do you open?").System;
        Assert.That(prompt, Does.Contain("Opening hours: 7am-6pm"), "the client's knowledge base is sent to the AI");
        Assert.That(prompt, Does.Contain("- Croissant: ฿45.00"), "the client's menu is sent to the AI (with the price in the client's currency)");
    }

    [Test]
    public async Task T09_Greeting_does_not_create_an_order()
    {
        var user = Line.NewUser("t09");
        await Line.SendAsync(World.Bakery, Line.TextEvent(user, "hello"));

        var calls = await Waits.LineCallsAsync(user);
        Assert.That(calls[0].Text, Does.StartWith("Hello!"));

        var log = await Waits.FinishedLogAsync(user);
        Assert.That(log["ResultSummary"], Is.EqualTo("Replied (greeting)."));
        Assert.That(Fakes.OrdersFor(user), Is.Empty);
    }

    [Test]
    public async Task T10_Refund_request_is_handed_to_staff()
    {
        var user = Line.NewUser("t10");
        await Line.SendAsync(World.Bakery, Line.TextEvent(user, "I want a refund"));

        var calls = await Waits.LineCallsAsync(user);
        Assert.That(calls[0].Text, Does.Contain("staff member"));

        var log = await Waits.FinishedLogAsync(user);
        Assert.That(log["Status"], Is.EqualTo(3));
        Assert.That(log["ResultSummary"], Is.EqualTo("Escalated to staff."));
        Assert.That((string)log["ActionsJson"]!, Does.Contain("NotifyInternal"));
    }

    [Test]
    public async Task T11_Expired_reply_token_falls_back_to_push()
    {
        var user = Line.NewUser("t11");
        await Line.SendAsync(World.Bakery, Line.TextEvent(user, "hello", replyToken: "expired-" + user));

        var calls = await Waits.LineCallsAsync(user, count: 2);
        Assert.That(calls[0].Kind, Is.EqualTo("reply"));
        Assert.That(calls[0].StatusReturned, Is.EqualTo(400));
        Assert.That(calls[1].Kind, Is.EqualTo("push"));
        Assert.That(calls[1].To, Is.EqualTo(user));

        var log = await Waits.FinishedLogAsync(user);
        Assert.That(log["Status"], Is.EqualTo(3));
        Assert.That((string)log["ActionsJson"]!, Does.Contain("push (reply token failed)"));
    }

    [Test]
    public async Task T12_Failed_LINE_send_marks_the_run_Failed()
    {
        var user = Line.NewUser("t12");
        await Line.SendAsync(World.FailSend, Line.TextEvent(user, "hello"));

        var log = await Waits.FinishedLogAsync(user);
        Assert.That(log["Status"], Is.EqualTo(4), "a reply that never reached the customer must show as Failed");
        Assert.That((string)log["ErrorMessage"]!, Does.StartWith("SendMessage failed"));
    }

    [Test]
    public async Task T13_Paused_client_is_skipped()
    {
        var user = Line.NewUser("t13");
        var response = await Line.SendAsync(World.Paused, Line.TextEvent(user, "2 croissants"));
        Assert.That(response.Int("received"), Is.EqualTo(1));

        var log = await Waits.FinishedLogAsync(user);
        Assert.That(log["Status"], Is.EqualTo(6));
        Assert.That((string)log["ResultSummary"]!, Does.Contain("Paused"));

        var evt = await Waits.SettledEventAsync(user);
        Assert.That(evt["Status"], Is.EqualTo(5));

        await Waits.QuietPeriodAsync();
        Assert.That(Fakes.LineCallsFor(user), Is.Empty);
        Assert.That(Fakes.OrdersFor(user), Is.Empty);
    }

    [Test]
    public async Task T14_No_enabled_workflow_is_skipped_with_a_reason()
    {
        var user = Line.NewUser("t14");
        await Line.SendAsync(World.NoFlow, Line.TextEvent(user, "hello"));

        var evt = await Waits.SettledEventAsync(user);
        Assert.That(evt["Status"], Is.EqualTo(5));
        Assert.That(evt["LastError"], Is.EqualTo("No enabled message workflow for LINE."));

        await Waits.QuietPeriodAsync();
        Assert.That(Fakes.LineCallsFor(user), Is.Empty);
    }

    [Test]
    public async Task T15_Unknown_client_code_is_404()
    {
        var body = Line.Body(Line.TextEvent(Line.NewUser("t15"), "hello"));
        var response = await Line.PostAsync("/api/webhooks/line/no-such-client", body, "x");
        Assert.That(response.Status, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task T16_Old_per_workflow_URL_still_works_but_not_across_clients()
    {
        var user = Line.NewUser("t16");
        var body = Line.Body(Line.TextEvent(user, "hello"));

        var ok = await Line.PostAsync($"/api/webhooks/line/{World.Bakery.Code}/{World.Bakery.WorkflowId}", body, World.Bakery.LineSecret);
        Assert.That(ok.Status, Is.EqualTo(HttpStatusCode.OK), ok.Body);
        await Waits.LineCallsAsync(user);

        // bakery's code with ANOTHER client's workflow id
        var user2 = Line.NewUser("t16x");
        var body2 = Line.Body(Line.TextEvent(user2, "hello"));
        var cross = await Line.PostAsync($"/api/webhooks/line/{World.Bakery.Code}/{World.Other.WorkflowId}", body2, World.Bakery.LineSecret);
        Assert.That(cross.Status, Is.EqualTo(HttpStatusCode.NotFound));
        Assert.That(await Waits.EventsAsync(user2), Is.Empty);
    }

    [Test]
    public async Task T17_Sticker_only_is_ignored()
    {
        var user = Line.NewUser("t17");
        var response = await Line.SendAsync(World.Bakery, Line.StickerEvent(user));

        Assert.That(response.Status, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(response.Int("received"), Is.EqualTo(0));
        Assert.That(response.Int("ignored"), Is.EqualTo(1));
        await Waits.QuietPeriodAsync();
        Assert.That(await Waits.EventsAsync(user), Is.Empty);
    }

    [Test]
    public async Task T18_Webhook_answers_at_once_even_when_the_AI_is_slow()
    {
        var user = Line.NewUser("t18");
        var response = await Line.SendAsync(World.Bakery, Line.TextEvent(user, "[slow] hello"));

        Assert.That(response.Status, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(response.ElapsedMs, Is.LessThan(1500), "LINE must get its 200 before the AI answers");

        var calls = await Waits.LineCallsAsync(user, timeoutMs: 20000);
        Assert.That(calls[0].Text, Does.StartWith("Hello!"));
    }

    [Test]
    public async Task T19_Unreadable_AI_answer_fails_the_run_but_customer_gets_the_fallback_reply()
    {
        var user = Line.NewUser("t19");
        await Line.SendAsync(World.Bakery, Line.TextEvent(user, "[badjson] hello"));

        var log = await Waits.FinishedLogAsync(user);
        Assert.That(log["Status"], Is.EqualTo(4));
        Assert.That((string)log["ErrorMessage"]!, Does.Contain("not valid JSON"));
        Assert.That(log["ResultSummary"], Is.EqualTo("Fallback reply sent; staff alerted."));

        var calls = await Waits.LineCallsAsync(user);
        Assert.That(calls[0].Text, Does.Contain("A member of our team will reply shortly"), "since Round 002 the customer always gets an answer");
    }

    [Test]
    public async Task T20_Old_plain_JSON_credentials_still_work()
    {
        var user = Line.NewUser("t20");
        var response = await Line.SendAsync(World.Legacy, Line.TextEvent(user, "hello"));
        Assert.That(response.Status, Is.EqualTo(HttpStatusCode.OK), response.Body);

        var calls = await Waits.LineCallsAsync(user);
        Assert.That(calls[0].Authorization, Is.EqualTo("Bearer legacy-token"));
    }
}
