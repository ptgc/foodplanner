namespace Foodplanner.Infrastructure.Persistence.Tenancy;

internal sealed class TenantWriteScope : ITenantWriteScope
{
    public Guid? HouseholdId { get; private set; }

    public IDisposable ForHousehold(Guid householdId)
    {
        if (householdId == Guid.Empty)
        {
            throw new ArgumentException("A write scope needs a household id.", nameof(householdId));
        }

        if (HouseholdId is not null)
        {
            throw new InvalidOperationException("A tenant write scope is already open; scopes do not nest.");
        }

        HouseholdId = householdId;
        return new Closer(this);
    }

    private sealed class Closer(TenantWriteScope owner) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            owner.HouseholdId = null;
        }
    }
}
