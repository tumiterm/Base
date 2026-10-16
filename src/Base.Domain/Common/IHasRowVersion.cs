namespace Base.Domain.Common;

/// <summary>Optimistic concurrency token (SQL Server <c>rowversion</c>).</summary>
public interface IHasRowVersion
{
    byte[] RowVersion { get; set; }
}
