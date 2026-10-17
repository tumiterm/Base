using Base.Application.Common.Abstractions;
using Base.Application.Common.Persistence;
using Base.Application.Common.Records;
using Base.Application.Security;
using Base.Domain.People;

namespace Base.Application.People;

/// <summary>Names people wherever a record points at one (documents, ...). Hidden without <c>People.View</c>.</summary>
internal sealed class PersonRecordLabels(IUnitOfWork unitOfWork, ICurrentUser currentUser) : IRecordLabelProvider
{
    public string EntityType => nameof(Person);

    public string TypeName => "Person";

    public async Task<IReadOnlyDictionary<string, RecordLabel>> GetLabelsAsync(IReadOnlyCollection<string> entityIds, CancellationToken cancellationToken = default)
    {
        var ids = RecordIds.Guids(entityIds);
        if (ids.Count == 0 || !currentUser.HasPermission(PermissionCodes.People.View))
        {
            return new Dictionary<string, RecordLabel>(StringComparer.Ordinal);
        }

        var people = await unitOfWork.Repository<Person>().GetAllAsync(
            p => new { p.Id, Name = (p.PreferredName ?? p.FirstName) + " " + p.LastName, p.JobTitle },
            p => ids.Contains(p.Id),
            cancellationToken: cancellationToken);
        return RecordIds.ByStoredId(entityIds, people.ToDictionary(p => p.Id, p => new RecordLabel(p.Name, p.JobTitle)));
    }
}
