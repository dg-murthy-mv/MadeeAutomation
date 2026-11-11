using System.Globalization;
using System.Text;
using MadeeAutomation.Application.Abstractions;
using MadeeAutomation.Application.Common;
using MadeeAutomation.Domain.Entities;
using MadeeAutomation.Domain.Enums;
using MadeeAutomation.Domain.Regional;

namespace MadeeAutomation.Application.VerticalPacks.Messaging;

/// <summary>
/// The menu-buttons chat: categories -> items -> cart -> "When do you want it?" -> order.
///
/// Every button carries a small piece of data ("a=cat&c=Drinks"). When the customer taps it, LINE
/// sends that data back and this code answers straight from the menu - no AI call, so it is instant
/// and costs nothing (replies to a tap use the free reply token).
///
///   a=home[&p=2]            categories
///   a=cat&c=Drinks[&p=2]    the items of a category ("-" = items with no category)
///   a=add&i={id}&q=1        add to the cart
///   a=cart                  show the cart          a=rem&i={id}   remove one item
///   a=clear                 empty the cart
///   a=checkout              ask "When do you want it?"
///   a=when&t=now            as soon as possible    a=when + picked date-time    a chosen time
///   a=staff                 ask for a person
/// </summary>
public abstract partial class CustomerMessageHandlerBase
{
    private const int CategoriesPerPage = 10;
    private const int ItemsPerPage = 10;
    private const int MaxQuantityPerItem = 99;
    private const string NoCategory = "-";

    // ---------------- button data ----------------

    private static string Data(string action, params (string Key, string? Value)[] values)
    {
        var sb = new StringBuilder("a=").Append(action);
        foreach (var (key, value) in values)
            if (!string.IsNullOrEmpty(value)) sb.Append('&').Append(key).Append('=').Append(Uri.EscapeDataString(value));
        return sb.ToString();
    }

    private static Dictionary<string, string> ReadData(string data)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in data.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var at = part.IndexOf('=');
            if (at <= 0) continue;
            try { result[part[..at]] = Uri.UnescapeDataString(part[(at + 1)..]); }
            catch (UriFormatException) { /* a broken value is simply ignored */ }
        }
        return result;
    }

    private static ChatButton Button(string label, string data, string? displayText = null) =>
        new() { Label = label, Data = data, DisplayText = displayText ?? label };

    // ---------------- menu facts ----------------

    /// <summary>The categories in use, A-Z, with "no category" last (as "-").</summary>
    private static List<string> Categories(Turn turn)
    {
        var named = turn.Profile.Menu
            .Select(m => (m.Category ?? "").Trim())
            .Where(c => c.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(c => c, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (turn.Profile.Menu.Any(m => string.IsNullOrWhiteSpace(m.Category))) named.Add(NoCategory);
        return named;
    }

    private static string CategoryLabel(Turn turn, string category) => category == NoCategory ? turn.T.OtherItems : category;

    private static List<MenuLine> ItemsIn(Turn turn, string category) => turn.Profile.Menu
        .Where(m => category == NoCategory
            ? string.IsNullOrWhiteSpace(m.Category)
            : string.Equals((m.Category ?? "").Trim(), category, StringComparison.OrdinalIgnoreCase))
        .ToList();

    private static MenuLine? Item(Turn turn, Guid id) => turn.Profile.Menu.FirstOrDefault(m => m.Id == id);

    private static int CartCount(Turn turn) => turn.Cart.Sum(l => l.Quantity);

    /// <summary>How many more of this item can go in the cart today (daily limit minus today's orders minus the cart). Null = no limit.</summary>
    private static int? LeftForCart(Turn turn, MenuLine item)
    {
        if (turn.Profile.LeftToday(item) is not { } left) return null;
        var inCart = turn.Cart.Where(l => l.MenuItemId == item.Id).Sum(l => l.Quantity);
        return Math.Max(0, left - inCart);
    }

    /// <summary>The cart's money lines (with the client's tax). Null when an item has no price.</summary>
    private static TaxBreakdown? CartMoney(Turn turn)
    {
        decimal total = 0;
        foreach (var line in turn.Cart)
        {
            if (Item(turn, line.MenuItemId)?.Price is not { } price) return null;
            total += price * line.Quantity;
        }
        return Tax.Calculate(total, turn.Client?.TaxName, turn.Client?.TaxRatePercent, turn.Client?.PricesIncludeTax ?? true);
    }

    /// <summary>"Cart: 2 item(s), ฿150.00" - the amount is what the customer will pay (tax included).</summary>
    private string CartSummaryLine(Turn turn) => CartMoney(turn) is { } money
        ? ChatTexts.F(turn.T.CartLine, CartCount(turn), Money.Format(money.Total, turn.Currency)) +
          (money is { HasTax: true, PricesIncludeTax: false } ? $" (with {money.Label})" : "")
        : ChatTexts.F(turn.T.CartLineNoTotal, CartCount(turn));

    private async Task SaveConversationAsync(Turn turn, CancellationToken cancellationToken)
    {
        if (turn.Context.IsTest || !turn.Buttons) return;   // an Admin test run remembers nothing
        turn.Conversation.WriteCart(turn.Cart);
        await _conversations.SaveAsync(turn.Conversation, cancellationToken);
    }

    // ---------------- what we show ----------------

    /// <summary>A single "Menu" button (plus the cart, when it has something) under ordinary replies.</summary>
    private RichContent? MenuHint(Turn turn)
    {
        if (!turn.Buttons) return null;
        var rich = new RichContent();
        rich.QuickReplies.Add(Button(turn.T.Menu, Data("home")));
        if (turn.Cart.Count > 0) rich.QuickReplies.Add(Button(ChatTexts.F(turn.T.ViewCart, CartCount(turn)), Data("cart")));
        return rich;
    }

    /// <summary>The category buttons. A menu with one category (or none) shows its items straight away.</summary>
    private RichContent HomeButtons(Turn turn, int page)
    {
        var categories = Categories(turn);
        if (categories.Count <= 1) return CategoryCards(turn, categories.FirstOrDefault() ?? NoCategory, 1);

        var pages = (int)Math.Ceiling(categories.Count / (double)CategoriesPerPage);
        page = Math.Clamp(page, 1, pages);

        var rich = new RichContent();
        foreach (var category in categories.Skip((page - 1) * CategoriesPerPage).Take(CategoriesPerPage))
            rich.QuickReplies.Add(Button(CategoryLabel(turn, category), Data("cat", ("c", category))));
        if (page < pages) rich.QuickReplies.Add(Button(turn.T.MoreCategories, Data("home", ("p", (page + 1).ToString()))));
        if (turn.Cart.Count > 0) rich.QuickReplies.Add(Button(ChatTexts.F(turn.T.ViewCart, CartCount(turn)), Data("cart")));
        rich.QuickReplies.Add(Button(turn.T.TalkToStaff, Data("staff")));
        return rich;
    }

    /// <summary>One card per item: name, price, a note when sold out / nearly gone, and +1 / +2 buttons.</summary>
    private RichContent CategoryCards(Turn turn, string category, int page)
    {
        var items = ItemsIn(turn, category);
        var pages = Math.Max(1, (int)Math.Ceiling(items.Count / (double)ItemsPerPage));
        page = Math.Clamp(page, 1, pages);
        var rich = new RichContent();

        foreach (var item in items.Skip((page - 1) * ItemsPerPage).Take(ItemsPerPage))
        {
            var card = new ChatCard
            {
                Title = item.Name,
                Subtitle = item.Price is { } price ? Money.Format(price, turn.Currency) : null
            };

            var left = LeftForCart(turn, item);
            if (!item.IsAvailable) card.Note = turn.T.SoldOutToday;
            else if (left == 0) card.Note = turn.T.FullyBookedToday;
            else
            {
                if (left is { } remaining) card.Note = ChatTexts.F(turn.T.LeftToday, remaining);
                var id = item.Id?.ToString() ?? "";
                card.Buttons.Add(Button("+1", Data("add", ("i", id), ("q", "1")), $"{item.Name} +1"));
                if (left is null or >= 2) card.Buttons.Add(Button("+2", Data("add", ("i", id), ("q", "2")), $"{item.Name} +2"));
            }
            rich.Cards.Add(card);
        }

        if (page < pages)
            rich.QuickReplies.Add(Button(ChatTexts.F(turn.T.MoreItems, CategoryLabel(turn, category)), Data("cat", ("c", category), ("p", (page + 1).ToString()))));
        if (Categories(turn).Count > 1) rich.QuickReplies.Add(Button(turn.T.OtherCategories, Data("home")));
        if (turn.Cart.Count > 0)
        {
            rich.QuickReplies.Add(Button(ChatTexts.F(turn.T.ViewCart, CartCount(turn)), Data("cart")));
            rich.QuickReplies.Add(Button(turn.T.PlaceOrder, Data("checkout")));
        }
        rich.QuickReplies.Add(Button(turn.T.TalkToStaff, Data("staff")));
        return rich;
    }

    private RichContent AfterAddButtons(Turn turn)
    {
        var rich = new RichContent();
        var back = string.IsNullOrWhiteSpace(turn.Conversation.LastCategory) ? Data("home") : Data("cat", ("c", turn.Conversation.LastCategory));
        rich.QuickReplies.Add(Button(turn.T.AddMore, back));
        rich.QuickReplies.Add(Button(ChatTexts.F(turn.T.ViewCart, CartCount(turn)), Data("cart")));
        rich.QuickReplies.Add(Button(turn.T.PlaceOrder, Data("checkout")));
        return rich;
    }

    private (string Text, RichContent Buttons) CartView(Turn turn)
    {
        var sb = new StringBuilder(turn.T.YourCart);
        var rich = new RichContent();
        rich.QuickReplies.Add(Button(turn.T.PlaceOrder, Data("checkout")));
        rich.QuickReplies.Add(Button(turn.T.AddMore, Data("home")));

        foreach (var line in turn.Cart)
        {
            var item = Item(turn, line.MenuItemId);
            if (item is null) continue;
            var price = item.Price is { } p ? " - " + Money.Format(p * line.Quantity, turn.Currency) : "";
            sb.Append('\n').Append($"{line.Quantity} x {item.Name}{price}");
            if (rich.QuickReplies.Count < 11)
                rich.QuickReplies.Add(Button(ChatTexts.F(turn.T.Remove, item.Name), Data("rem", ("i", line.MenuItemId.ToString()))));
        }
        if (CartMoney(turn) is { } money) sb.Append('\n').Append(string.Join("\n", Tax.Lines(money, turn.Currency)));

        rich.QuickReplies.Add(Button(turn.T.ClearCart, Data("clear")));
        return (sb.ToString(), rich);
    }

    private RichContent WhenButtons(Turn turn)
    {
        // Suggest half an hour from now, rounded up to the next 5 minutes.
        var suggested = turn.LocalNow.AddMinutes(30);
        suggested = suggested.AddMinutes((5 - suggested.Minute % 5) % 5).AddSeconds(-suggested.Second);
        const string format = "yyyy-MM-dd't'HH:mm";

        var rich = new RichContent();
        rich.QuickReplies.Add(Button(turn.T.AsSoonAsPossible, Data("when", ("t", "now"))));
        rich.QuickReplies.Add(new ChatButton
        {
            Label = turn.T.ChooseTime,
            Data = Data("when"),
            PickDateTime = true,
            PickInitial = suggested.ToString(format, CultureInfo.InvariantCulture),
            PickMin = turn.LocalNow.ToString(format, CultureInfo.InvariantCulture),
            PickMax = turn.LocalNow.AddDays(30).ToString(format, CultureInfo.InvariantCulture)
        });
        rich.QuickReplies.Add(Button(turn.T.BackToCart, Data("cart")));
        return rich;
    }

    // ---------------- handling a tap ----------------

    private async Task<HandlerResult> HandleTapAsync(Turn turn, string kind, CancellationToken cancellationToken)
    {
        var t = turn.T;
        var clientName = turn.Client?.ClientName ?? "";

        // Drop cart lines whose item was deleted from the menu since.
        turn.Cart = turn.Cart.Where(l => Item(turn, l.MenuItemId) is not null).ToList();

        if (kind == InboundKinds.Follow)
            return HandlerResult.Ok("New friend: sent the welcome and the menu buttons.",
                Reply(turn, ChatTexts.F(t.Welcome, clientName) + "\n" + t.ChooseCategory, HomeButtons(turn, 1)));

        var data = ReadData(turn.Text);
        var action = data.GetValueOrDefault("a", "home");
        int Page() => int.TryParse(data.GetValueOrDefault("p"), out var p) ? p : 1;
        Guid? ItemId() => Guid.TryParse(data.GetValueOrDefault("i"), out var id) ? id : null;

        switch (action)
        {
            case "cat":
            {
                var category = data.GetValueOrDefault("c", NoCategory);
                var items = ItemsIn(turn, category);
                if (items.Count == 0)
                    return HandlerResult.Ok("Menu: empty category - showed the categories.", Reply(turn, t.NoItemsHere + "\n" + t.ChooseCategory, HomeButtons(turn, 1)));

                turn.Conversation.LastCategory = Cut(category, 100);
                await SaveConversationAsync(turn, cancellationToken);
                return HandlerResult.Ok($"Menu: showed {CategoryLabel(turn, category)} ({items.Count} item(s)).",
                    Reply(turn, CategoryLabel(turn, category), CategoryCards(turn, category, Page())));
            }

            case "add":
            {
                var item = ItemId() is { } id ? Item(turn, id) : null;
                if (item is null)
                    return HandlerResult.Ok("Cart: item no longer on the menu.", Reply(turn, t.ItemGone, HomeButtons(turn, 1)));

                var quantity = int.TryParse(data.GetValueOrDefault("q"), out var q) ? Math.Clamp(q, 1, 10) : 1;
                if (!item.IsAvailable)
                    return HandlerResult.Ok($"Cart: {item.Name} is sold out - not added.", Reply(turn, ChatTexts.F(t.ItemSoldOut, item.Name), AfterAddOrHome(turn)));

                var left = LeftForCart(turn, item);
                if (left is { } remaining && quantity > remaining)
                    return HandlerResult.Ok($"Cart: daily limit - {item.Name} not added.",
                        Reply(turn, remaining == 0 ? ChatTexts.F(t.ItemFullyBooked, item.Name) : ChatTexts.F(t.OnlyLeft, remaining, item.Name), AfterAddOrHome(turn)));

                var line = turn.Cart.FirstOrDefault(l => l.MenuItemId == item.Id);
                if (line is null) turn.Cart.Add(line = new CartLine { MenuItemId = item.Id!.Value });
                line.Quantity = Math.Min(MaxQuantityPerItem, line.Quantity + quantity);
                turn.Conversation.State = string.Empty;
                await SaveConversationAsync(turn, cancellationToken);

                return HandlerResult.Ok($"Cart: added {quantity} x {item.Name} ({CartCount(turn)} in the cart).",
                    Reply(turn, ChatTexts.F(t.Added, quantity, item.Name) + "\n" + CartSummaryLine(turn), AfterAddButtons(turn)));
            }

            case "rem":
            {
                var item = ItemId() is { } id ? Item(turn, id) : null;
                var removed = turn.Cart.RemoveAll(l => l.MenuItemId == ItemId());
                await SaveConversationAsync(turn, cancellationToken);
                var prefix = removed > 0 && item is not null ? ChatTexts.F(t.Removed, item.Name) + "\n" : "";
                if (turn.Cart.Count == 0)
                    return HandlerResult.Ok("Cart: removed the last item.", Reply(turn, prefix + t.CartEmpty + "\n" + t.ChooseCategory, HomeButtons(turn, 1)));
                var (text, buttons) = CartView(turn);
                return HandlerResult.Ok("Cart: removed an item.", Reply(turn, prefix + text, buttons));
            }

            case "cart":
            {
                turn.Conversation.State = string.Empty;
                await SaveConversationAsync(turn, cancellationToken);
                if (turn.Cart.Count == 0)
                    return HandlerResult.Ok("Cart: empty.", Reply(turn, t.CartEmpty + "\n" + t.ChooseCategory, HomeButtons(turn, 1)));
                var (text, buttons) = CartView(turn);
                return HandlerResult.Ok($"Cart: showed {CartCount(turn)} item(s).", Reply(turn, text, buttons));
            }

            case "clear":
                turn.Cart.Clear();
                turn.Conversation.State = string.Empty;
                await SaveConversationAsync(turn, cancellationToken);
                return HandlerResult.Ok("Cart: cleared.", Reply(turn, t.CartCleared + "\n" + t.ChooseCategory, HomeButtons(turn, 1)));

            case "checkout":
                if (turn.Cart.Count == 0)
                    return HandlerResult.Ok("Checkout: the cart is empty.", Reply(turn, t.CartEmpty + "\n" + t.ChooseCategory, HomeButtons(turn, 1)));
                turn.Conversation.State = Conversation.AwaitingTime;
                await SaveConversationAsync(turn, cancellationToken);
                return HandlerResult.Ok("Checkout: asked when they want it.", Reply(turn, CartSummaryLine(turn) + "\n" + t.WhenQuestion, WhenButtons(turn)));

            case "when":
            {
                // A second tap after the order is placed finds an empty cart - so it can never order twice.
                if (turn.Cart.Count == 0)
                    return HandlerResult.Ok("Checkout: the cart is empty (already ordered?).", Reply(turn, t.CartEmpty + "\n" + t.ChooseCategory, HomeButtons(turn, 1)));

                if (data.GetValueOrDefault("t") == "now")
                    return await PlaceCartAsync(turn, null, null, cancellationToken);

                var picked = MessageAssistant.ParseLocal(data.GetValueOrDefault("datetime")?.Replace('t', 'T'));
                if (picked is null || picked < turn.LocalNow.AddMinutes(-2))
                    return HandlerResult.Ok("Checkout: the chosen time is missing or in the past - asked again.",
                        Reply(turn, picked is null ? t.WhenQuestion : t.TimePassed, WhenButtons(turn)));

                return await PlaceCartAsync(turn, picked, null, cancellationToken);
            }

            case "staff":
                return HandlerResult.Ok("Escalated to staff (the customer tapped Talk to staff).",
                    Reply(turn, t.StaffWillReply, MenuHint(turn)),
                    ActionRequest.For(ActionType.NotifyInternal, new Dictionary<string, string>
                    {
                        ["message"] = $"[{clientName}] {Channels.DisplayName(turn.Channel)} customer {turn.From} tapped \"Talk to staff\" - please reply to them in the chat."
                    }, required: false));

            default:   // "home" and anything we don't know
                return HandlerResult.Ok("Menu: showed the categories.", Reply(turn, t.ChooseCategory, HomeButtons(turn, Page())));
        }
    }

    private RichContent AfterAddOrHome(Turn turn) => turn.Cart.Count > 0 ? AfterAddButtons(turn) : HomeButtons(turn, 1);

    /// <summary>
    /// Turns the cart into an order. when = the chosen local time (null = as soon as possible);
    /// typedTime = the customer's own words when they typed the time instead of tapping.
    /// </summary>
    private async Task<HandlerResult> PlaceCartAsync(Turn turn, DateTime? when, string? typedTime, CancellationToken cancellationToken)
    {
        var t = turn.T;
        var wanted = turn.Cart
            .Select(l => (line: l, item: Item(turn, l.MenuItemId)))
            .Where(x => x.item is not null)
            .Select(x => new Wanted(x.item, x.item!.Name, x.line.Quantity))
            .ToList();

        if (wanted.Count == 0)
            return HandlerResult.Ok("Checkout: the cart is empty.", Reply(turn, t.CartEmpty + "\n" + t.ChooseCategory, HomeButtons(turn, 1)));

        var whenText = when is { } chosen ? chosen.ToString("ddd d MMM, HH:mm", CultureInfo.InvariantCulture) : t.AsSoonAsPossible;

        // Whatever happens next, this cart is finished: emptied before the reply goes out, so a
        // double tap or a retry can never place the same order twice.
        async Task Finish()
        {
            turn.Cart = new List<CartLine>();
            turn.Conversation.State = string.Empty;
            await SaveConversationAsync(turn, cancellationToken);
        }

        var menuButton = new RichContent();
        menuButton.QuickReplies.Add(Button(t.Menu, Data("home")));

        var result = await PlaceAsync(turn, wanted, when, typedTime ?? (when is null ? "As soon as possible" : null), null,
            turn.Conversation.Language, "(ordered with the menu buttons)",
            (orderId, lines) =>
            {
                var sb = new StringBuilder(ChatTexts.F(t.OrderConfirmed, orderId));
                foreach (var (name, quantity) in lines) sb.Append('\n').Append($"{quantity} x {name}");
                sb.Append('\n').Append(ChatTexts.F(t.ForTime, whenText));
                return sb.ToString();
            },
            cancellationToken, menuButton, Finish);

        // Nothing could be ordered (everything sold out / over the limit since it went in the cart): start clean.
        if (turn.Cart.Count > 0) await Finish();
        return result;
    }
}
