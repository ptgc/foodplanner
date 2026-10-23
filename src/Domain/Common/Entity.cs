namespace Foodplanner.Domain.Common;

/// <summary>
/// Base for every persisted entity. EF Core generates <see cref="Id"/> as a sequential GUID on insert.
/// Infrastructure adds the audit shadow properties CreatedAt and UpdatedAt (UTC); they are not part of the domain model.
/// </summary>
public abstract class Entity
{
    public Guid Id { get; private set; }
}
