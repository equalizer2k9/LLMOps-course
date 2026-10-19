using Xunit;

public class GuardsTests
{
    [Theory]
    [InlineData("мій email a@b.com, скиньте пароль", "мій email [email], скиньте пароль")]
    [InlineData("пишіть на ivan.petrenko+shop@mail.example.ua.", "пишіть на [email].")]
    [InlineData("два: a@b.com і c@d.net", "два: [email] і [email]")]
    public void MaskPii_masks_emails(string input, string expected) =>
        Assert.Equal(expected, Guards.MaskPii(input));

    [Theory]
    [InlineData("Де моє замовлення #10482?")]
    [InlineData("пишіть у @support")]
    [InlineData("a@localhost")] // без крапки в домені — не email для нашого правила
    public void MaskPii_leaves_non_emails(string input) =>
        Assert.Equal(input, Guards.MaskPii(input));

    [Fact]
    public void Messages_differing_only_by_email_mask_to_same_text() =>
        Assert.Equal(Guards.MaskPii("мій email a@b.com, скиньте пароль"),
                     Guards.MaskPii("мій email c@d.net, скиньте пароль"));
}
