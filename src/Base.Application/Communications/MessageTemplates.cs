using Base.Application.Common.Abstractions;
using Base.Application.Common.Persistence;
using Base.Application.Common.Results;
using Base.Application.Security;
using Base.Domain.Communications;

namespace Base.Application.Communications;

/// <summary>Wording the organisation keeps for messages it sends often. Needs <c>Messages.Send</c>.</summary>
public interface IMessageTemplateService
{
    Task<IReadOnlyList<MessageTemplateView>> ListAsync(CancellationToken cancellationToken = default);

    Task<ValidationResponse<MessageTemplateView>> GetAsync(Guid id, CancellationToken cancellationToken = default);

    Task<ValidationResponse<Guid>> CreateAsync(SaveMessageTemplateRequest request, CancellationToken cancellationToken = default);

    Task<ValidationResponse> UpdateAsync(Guid id, SaveMessageTemplateRequest request, CancellationToken cancellationToken = default);

    Task<ValidationResponse> DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}

internal sealed class MessageTemplateService(IUnitOfWork unitOfWork, ICurrentUser currentUser, IRichText richText) : IMessageTemplateService
{
    public async Task<IReadOnlyList<MessageTemplateView>> ListAsync(CancellationToken cancellationToken = default) =>
        (await unitOfWork.Repository<MessageTemplate>().GetAllAsync(null, Array.Empty<System.Linq.Expressions.Expression<Func<MessageTemplate, object?>>>(), q => q.OrderBy(t => t.Name), asNoTracking: true, cancellationToken))
            .Select(ToView).ToList();

    public async Task<ValidationResponse<MessageTemplateView>> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
        await unitOfWork.Repository<MessageTemplate>().GetAsync(t => t.Id == id, asNoTracking: true, cancellationToken: cancellationToken) is { } template
            ? ToView(template)
            : ValidationResponse.NotFound("Template not found.").As<MessageTemplateView>();

    public async Task<ValidationResponse<Guid>> CreateAsync(SaveMessageTemplateRequest request, CancellationToken cancellationToken = default)
    {
        var check = await CheckAsync(request, null, cancellationToken);
        if (check.IsError)
        {
            return check.As<Guid>();
        }

        var template = new MessageTemplate(request.Name);
        Apply(template, request);
        await unitOfWork.Repository<MessageTemplate>().AddAsync(template, cancellationToken);
        await unitOfWork.SaveAsync(cancellationToken);
        return ValidationResponse.Success(template.Id, $"\"{template.Name}\" saved.");
    }

    public async Task<ValidationResponse> UpdateAsync(Guid id, SaveMessageTemplateRequest request, CancellationToken cancellationToken = default)
    {
        var check = await CheckAsync(request, id, cancellationToken);
        if (check.IsError)
        {
            return check;
        }

        var template = await unitOfWork.Repository<MessageTemplate>().GetAsync(t => t.Id == id, cancellationToken: cancellationToken);
        if (template is null)
        {
            return ValidationResponse.NotFound("Template not found.");
        }

        template.Rename(request.Name);
        Apply(template, request);
        await unitOfWork.SaveAsync(cancellationToken);
        return ValidationResponse.Success($"\"{template.Name}\" saved.");
    }

    public async Task<ValidationResponse> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        if (!currentUser.HasPermission(PermissionCodes.Messages.Send))
        {
            return ValidationResponse.Forbidden();
        }

        var repository = unitOfWork.Repository<MessageTemplate>();
        var template = await repository.GetAsync(t => t.Id == id, cancellationToken: cancellationToken);
        if (template is null)
        {
            return ValidationResponse.NotFound("Template not found.");
        }

        repository.Remove(template);
        await unitOfWork.SaveAsync(cancellationToken);
        return ValidationResponse.Success($"\"{template.Name}\" deleted. Messages already sent with it are unchanged.");
    }

    private async Task<ValidationResponse> CheckAsync(SaveMessageTemplateRequest request, Guid? id, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!currentUser.HasPermission(PermissionCodes.Messages.Send))
        {
            return ValidationResponse.Forbidden();
        }

        var errors = new List<ValidationError>();
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > MessageTemplate.NameMaxLength)
        {
            errors.Add(new ValidationError(nameof(request.Name), $"Give it a name of up to {MessageTemplate.NameMaxLength} characters."));
        }
        else
        {
            var name = request.Name.Trim();
            if (await unitOfWork.Repository<MessageTemplate>().ExistsAsync(t => t.Name == name && t.Id != id, cancellationToken))
            {
                errors.Add(new ValidationError(nameof(request.Name), "Another template has this name."));
            }
        }

        if (string.IsNullOrWhiteSpace(richText.Sanitize(request.HtmlBody)) && string.IsNullOrWhiteSpace(request.SmsBody) && string.IsNullOrWhiteSpace(request.WhatsAppTemplateName))
        {
            errors.Add(new ValidationError(nameof(request.HtmlBody), "Write the message (or at least an SMS or a WhatsApp template)."));
        }

        return errors.Count == 0 ? ValidationResponse.Success() : ValidationResponse.Failure(errors);
    }

    private void Apply(MessageTemplate template, SaveMessageTemplateRequest request) =>
        template.SetContent(
            request.Subject,
            richText.Sanitize(request.HtmlBody) is { Length: > 0 } html ? html : null,
            request.SmsBody,
            request.WhatsAppTemplateName,
            request.WhatsAppLanguage,
            request.WhatsAppParameters is { Count: > 0 } parameters ? MessagingJson.Write(parameters) : null);

    private static MessageTemplateView ToView(MessageTemplate t) =>
        new(t.Id, t.Name, t.Subject, t.HtmlBody, t.SmsBody, t.WhatsAppTemplateName, t.WhatsAppLanguage, MessagingJson.List(t.WhatsAppParameters), t.DateModified);
}
