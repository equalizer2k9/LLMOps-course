using System.Text.RegularExpressions;

// Guardrails (W4). Окремий public static class, а не локальна функція Program.cs:
// функції top-level Program.cs невидимі для тест-збірки (GETTING_STARTED §10).
public static class Guards
{
    // Email: локальна частина (літери/цифри . _ % + -) @ домен з хоча б однією крапкою.
    // Свідомо лише email — це не PII-детектор (телефони, картки, адреси — поза core).
    private static readonly Regex Email = new(@"[\w.%+\-]+@[\w\-]+(\.[\w\-]+)+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string MaskPii(string s) => Email.Replace(s, "[email]");
}
