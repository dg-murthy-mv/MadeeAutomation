using System.Globalization;
using System.Text;

namespace MadeeAutomation.Domain.Regional;

/// <summary>
/// The clinic chat's emergency rule. A chat assistant must never be where a patient in danger waits:
/// when a message sounds like an emergency, the patient is told at once to call the local emergency
/// number or go to the nearest emergency room, and the clinic's staff are alerted - before (and
/// whatever) the AI would say. Checked by our own code, so it works even when the AI is down.
///
/// The word list is deliberately broad (better one unnecessary "please call 112" than a missed one) and
/// covers English, Hindi / Hinglish, Tagalog / Taglish, Thai and Arabic. The AI is told the same rule as
/// a second net (intent "emergency").
/// </summary>
public static class ClinicSafety
{
    /// <summary>
    /// Phrases that mean "get help now". General words such as "emergency", "ambulance" or "accident" are
    /// left out on purpose: "do you take emergency patients?" is a question, not an emergency.
    /// </summary>
    /// <remarks>Matched case-insensitively, ignoring accents and extra spaces.</remarks> Matched case-insensitively, ignoring accents and extra spaces.</summary>
    private static readonly string[] Phrases =
    {
        // English
        "chest pain", "pain in my chest", "heart attack", "can't breathe", "cant breathe", "cannot breathe", "can not breathe",
        "difficulty breathing", "trouble breathing", "not breathing", "stopped breathing", "choking", "unconscious", "passed out",
        "fainted", "not waking up", "won't wake up", "wont wake up", "unresponsive", "seizure", "convulsion", "stroke",
        "face drooping", "slurred speech", "severe bleeding", "heavy bleeding", "bleeding a lot", "won't stop bleeding",
        "wont stop bleeding", "vomiting blood", "coughing blood", "overdose", "poisoned", "poisoning", "swallowed poison",
        "suicide", "kill myself", "end my life", "want to die", "self harm", "hurt myself", "severe allergic", "anaphylaxis",
        "throat swelling", "throat is closing", "severe burn", "head injury", "broken bone", "snake bite", "snakebite",
        "pregnant and bleeding", "water broke",
        // Hindi / Hinglish (romanised) and Hindi script
        "seene mein dard", "seene me dard", "chhati mein dard", "saans nahi", "saans lene mein", "behosh", "khoon beh",
        "khoon nahi ruk", "dil ka daura", "atmahatya", "mar jana chahta", "mar jaana", "zeher", "दिल का दौरा", "सीने में दर्द",
        "सांस नहीं", "बेहोश", "आत्महत्या", 
        // Tagalog / Taglish
        "masakit ang dibdib", "sakit sa dibdib", "hindi makahinga", "di makahinga", "nahimatay", "walang malay", "dumudugo nang marami",
        "malakas na pagdurugo", "atake sa puso", "nalason", "magpapakamatay", "gusto ko nang mamatay", "kombulsyon", "nasagasaan",
        // Thai
        "เจ็บหน้าอก", "แน่นหน้าอก", "หายใจไม่ออก", "หายใจลำบาก", "หมดสติ", "เป็นลม", "เลือดออกมาก", "ชักกระตุก", "หัวใจวาย",
        "ฆ่าตัวตาย", "อยากตาย", "กินยาเกินขนาด",
        // Arabic
        "ألم في الصدر", "ألم بالصدر", "لا أستطيع التنفس", "ضيق تنفس", "فقد الوعي", "نزيف", "جلطة", "نوبة قلبية", "انتحار",
        "تسمم"
    };

    private static readonly string[] Normalized = Phrases.Select(Normalize).ToArray();

    /// <summary>True when the message should get the emergency answer (and a staff alert).</summary>
    public static bool LooksLikeEmergency(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return false;
        var text = " " + Normalize(message) + " ";
        foreach (var phrase in Normalized)
        {
            if (phrase.Length == 0) continue;
            // Latin words: whole words only ("fits" must not match "benefits"); other scripts: anywhere.
            if (IsLatin(phrase) ? text.Contains(" " + phrase + " ", StringComparison.Ordinal) : text.Contains(phrase, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    /// <summary>The number to call, by country: India 112 (ambulance 108), Philippines 911, Thailand 1669, UAE 998 / 999.</summary>
    public static string EmergencyNumber(string? country) => (country ?? "").ToUpperInvariant() switch
    {
        "IN" => "112 (ambulance 108)",
        "PH" => "911",
        "TH" => "1669",
        "AE" => "998 (ambulance) or 999",
        _ => "your local emergency number"
    };

    /// <summary>The fixed answer. Written by us, not the AI, so it is always right.</summary>
    public static string EmergencyReply(string clinicName, string? country, string? phone = null) =>
        "This sounds like it could be an emergency. Please call " + EmergencyNumber(country) +
        " now, or go to the nearest hospital emergency room. Do not wait for a reply here." +
        (string.IsNullOrWhiteSpace(phone) ? "" : $"\nYou can also call {clinicName} on {phone}.") +
        $"\nWe have alerted the {clinicName} team.";

    private static bool IsLatin(string s) => s.All(c => c < 0x250);

    /// <summary>Lower case, accents removed, punctuation turned into spaces, single spaces.</summary>
    public static string Normalize(string s)
    {
        var d = s.ToLowerInvariant().Replace('’', '\'').Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(d.Length);
        var lastSpace = false;
        foreach (var c in d)
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(c);
            if (cat == UnicodeCategory.NonSpacingMark && c < 0x0300 + 0x70) continue;   // Latin accents only (Thai / Hindi marks stay)
            var keep = char.IsLetterOrDigit(c) || c == '\'' || cat is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark;
            if (keep) { sb.Append(c); lastSpace = false; }
            else if (!lastSpace) { sb.Append(' '); lastSpace = true; }
        }
        return sb.ToString().Normalize(NormalizationForm.FormC).Trim();
    }
}
