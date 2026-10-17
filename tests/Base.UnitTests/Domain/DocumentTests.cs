using Base.Domain.Documents;

namespace Base.UnitTests.Domain;

public sealed class DocumentTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 17, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Versions_number_up_and_raise_events()
    {
        var document = New();
        document.AddVersion(Guid.NewGuid(), "a.pdf", "application/pdf", 1, new string('a', 64), DocumentSource.Uploaded, null, null);
        var second = document.AddVersion(Guid.NewGuid(), "b.pdf", "application/pdf", 1, new string('b', 64), DocumentSource.Generated, "  ", "T");

        second.Number.ShouldBe(2);
        second.Comment.ShouldBeNull();
        second.DocumentId.ShouldBe(document.Id);
        document.CurrentVersion.ShouldBe(2);
        document.DomainEvents.OfType<DocumentVersionAdded>().Select(e => e.Number).ShouldBe([1, 2]);
    }

    [Fact]
    public void Status_changes_follow_the_lifecycle()
    {
        var document = New();
        Should.Throw<InvalidOperationException>(() => document.MarkFinal(Now)).Message.ShouldContain("needs a version");
        document.AddVersion(Guid.NewGuid(), "a.pdf", "application/pdf", 1, new string('a', 64), DocumentSource.Uploaded, null, null);

        document.MarkFinal(Now);
        Should.Throw<InvalidOperationException>(() => document.AddVersion(Guid.NewGuid(), "b.pdf", "application/pdf", 1, new string('b', 64), DocumentSource.Uploaded, null, null));

        document.Archive(Now);
        Should.Throw<InvalidOperationException>(() => document.Update("New", null, null));
        document.Restore();
        document.Status.ShouldBe(DocumentStatus.Final);

        document.Reopen();
        document.Status.ShouldBe(DocumentStatus.Draft);
        document.Archive(Now);
        document.Restore();
        document.Status.ShouldBe(DocumentStatus.Draft, "never finalised, so it comes back as a draft");
    }

    [Fact]
    public void Tags_are_normalised_and_links_need_both_parts()
    {
        New(["  Finance", "finance", "", "HR"]).Tags.ShouldBe(["finance", "hr"]);
        Should.Throw<ArgumentException>(() => New(Enumerable.Range(0, 21).Select(i => $"t{i}")));
        Should.Throw<ArgumentException>(() => new Document("t", "general", null, null, "Invoice", null, null));
    }

    private static Document New(IEnumerable<string>? tags = null) => new("Title", "general", null, tags, null, null, Guid.NewGuid());
}
