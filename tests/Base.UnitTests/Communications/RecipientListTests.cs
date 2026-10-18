using Base.Application.Communications;
using Base.Domain.Notifications;

namespace Base.UnitTests.Communications;

public sealed class RecipientListTests
{
    [Fact]
    public void Known_columns_are_recognised_and_the_rest_become_placeholders()
    {
        var parsed = RecipientList.Parse("Full name,E-mail address,Mobile,Student number,Class\nAmy Adams,amy@example.com,082 555 0101,S-1001,10B\n");

        parsed.Problems.ShouldBeEmpty();
        parsed.Fields.Select(f => f.Name).ShouldBe(["StudentNumber", "Class"]);
        var amy = parsed.Members.ShouldHaveSingleItem();
        amy.Name.ShouldBe("Amy Adams");
        amy.FirstName.ShouldBe("Amy");
        amy.Email.ShouldBe("amy@example.com");
        amy.Phone.ShouldBe("082 555 0101");
        amy.Values["StudentNumber"].ShouldBe("S-1001");
    }

    [Fact]
    public void Quotes_semicolons_and_rows_without_an_address_are_handled()
    {
        var parsed = RecipientList.Parse("Name;Email;Note\n\"Botha, Ben\";ben@example.com;\"He said \"\"hi\"\"\"\nNo Address;;x\n");

        var ben = parsed.Members.ShouldHaveSingleItem();
        ben.Name.ShouldBe("Botha, Ben");
        ben.Values["Note"].ShouldBe("He said \"hi\"");
        parsed.Problems.ShouldHaveSingleItem().ShouldContain("Row 3");
    }

    [Fact]
    public void A_list_needs_an_email_or_phone_column()
    {
        RecipientList.Parse("Name,Class\nAmy,10B").Problems.ShouldHaveSingleItem().ShouldContain("Email or a Phone column");
        RecipientList.Parse("").Members.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("Student number", "StudentNumber")]
    [InlineData("  e-mail  ", "EMail")]
    [InlineData("2nd contact", "NdContact")]
    public void Headers_become_placeholder_names(string header, string name) => RecipientList.Placeholder(header).ShouldBe(name);
}

public sealed class MessageWordingTests
{
    [Theory]
    [InlineData("", 0)]
    [InlineData("Hello", 1)]
    [InlineData("Hello 😀", 1)]
    public void Short_messages_are_one_sms(string text, int parts) => MessageWording.SmsParts(text).ShouldBe(parts);

    [Fact]
    public void Long_or_special_character_messages_split_into_shorter_parts()
    {
        MessageWording.SmsParts(new string('a', 160)).ShouldBe(1);
        MessageWording.SmsParts(new string('a', 161)).ShouldBe(2);
        MessageWording.SmsParts(new string('a', 306)).ShouldBe(2);
        MessageWording.SmsParts(new string('a', 307)).ShouldBe(3);
        MessageWording.SmsParts("€" + new string('a', 70)).ShouldBe(2, "a character outside the GSM alphabet limits parts to 70 (67)");
    }

    [Fact]
    public void Each_channel_needs_its_own_wording_and_only_known_placeholders()
    {
        var wording = new MessageWording("t", null, null, null, null, null, null, []);
        var problems = wording.Problems([NotificationChannel.Email, NotificationChannel.WhatsApp], new HashSet<string>(StringComparer.Ordinal) { "FirstName" });
        problems.Select(p => p.Field).ShouldBe(["Subject", "WhatsAppTemplateName"]);

        var unknown = new MessageWording("t", "Hi {{Nickname}}", "<p>x</p>", "x", null, null, null, []).Problems([NotificationChannel.Email], new HashSet<string>(StringComparer.Ordinal) { "FirstName" });
        unknown.ShouldHaveSingleItem().Message.ShouldContain("{{Nickname}} isn't available");
    }
}
