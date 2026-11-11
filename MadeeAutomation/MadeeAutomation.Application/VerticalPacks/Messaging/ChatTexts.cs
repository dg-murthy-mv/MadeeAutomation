namespace MadeeAutomation.Application.VerticalPacks.Messaging;

/// <summary>
/// The fixed words on the menu buttons and in the instant replies to a tap. These are answered
/// by our code without the AI (that is why they are instant), so they cannot be translated on
/// the fly. They are English only - our side of the product is English. Typed messages are still
/// answered by the AI in the customer's own language.
/// </summary>
public class ChatTexts
{
    public string Code { get; private init; } = "en";

    public string ChooseCategory { get; private init; } = "What would you like? Choose a category:";
    public string Welcome { get; private init; } = "Welcome to {0}!";
    public string OtherItems { get; private init; } = "Other";
    public string Menu { get; private init; } = "Menu";
    public string MoreCategories { get; private init; } = "More";
    public string MoreItems { get; private init; } = "More {0}";
    public string OtherCategories { get; private init; } = "Other categories";
    public string ViewCart { get; private init; } = "Cart ({0})";
    public string PlaceOrder { get; private init; } = "Place order";
    public string AddMore { get; private init; } = "Add more";
    public string ClearCart { get; private init; } = "Clear cart";
    public string Remove { get; private init; } = "Remove {0}";
    public string TalkToStaff { get; private init; } = "Talk to staff";
    public string SoldOutToday { get; private init; } = "Sold out today";
    public string LeftToday { get; private init; } = "{0} left today";
    public string FullyBookedToday { get; private init; } = "Fully booked today";
    public string NoItemsHere { get; private init; } = "There is nothing in this category right now.";
    public string Added { get; private init; } = "Added {0} x {1}.";
    public string CartLine { get; private init; } = "Cart: {0} item(s), {1}";
    public string CartLineNoTotal { get; private init; } = "Cart: {0} item(s)";
    public string YourCart { get; private init; } = "Your cart:";
    public string Total { get; private init; } = "Total: {0}";
    public string CartEmpty { get; private init; } = "Your cart is empty.";
    public string CartCleared { get; private init; } = "Your cart is now empty.";
    public string Removed { get; private init; } = "Removed {0}.";
    public string ItemGone { get; private init; } = "Sorry, that item is no longer on the menu.";
    public string ItemSoldOut { get; private init; } = "Sorry, {0} is sold out today.";
    public string OnlyLeft { get; private init; } = "Sorry, only {0} {1} left today.";
    public string ItemFullyBooked { get; private init; } = "Sorry, {0} is fully booked today.";
    public string WhenQuestion { get; private init; } = "When do you want it?";
    public string AsSoonAsPossible { get; private init; } = "As soon as possible";
    public string ChooseTime { get; private init; } = "Choose a time";
    public string BackToCart { get; private init; } = "Back to cart";
    public string TimePassed { get; private init; } = "That time has already passed. When do you want it?";
    public string OrderConfirmed { get; private init; } = "Thank you! Your order is confirmed. Order number: {0}";
    public string ForTime { get; private init; } = "For: {0}";
    public string StaffWillReply { get; private init; } = "A member of our team will reply to you here shortly.";

    public static readonly ChatTexts English = new();

    /// <summary>
    /// Our side is English only, so the button words are English for every customer and every market.
    /// (Typed messages are still answered by the AI in the customer's own language.) The parameters are
    /// kept so a language could be added here later without touching the callers.
    /// </summary>
    public static ChatTexts For(string? language, string? country) => English;

    public static string F(string template, params object?[] values) => string.Format(template, values);
}
