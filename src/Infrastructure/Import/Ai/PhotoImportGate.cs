namespace Foodplanner.Infrastructure.Import.Ai;

/// <summary>
/// The per-process cap on photo imports running at once (T-118, <c>AiPhotoImport:MaxConcurrent</c>). A call that finds it full does
/// not wait: the endpoint answers 503 photo-import-busy and nothing is reserved.
/// </summary>
public sealed class PhotoImportGate(int maxConcurrent) : IDisposable
{
    private readonly SemaphoreSlim _slots = new(Math.Max(1, maxConcurrent), Math.Max(1, maxConcurrent));

    /// <summary>A slot to dispose when the call is over, or null when all slots are taken.</summary>
    public IDisposable? TryEnter() => _slots.Wait(0) ? new Slot(_slots) : null;

    public void Dispose() => _slots.Dispose();

    private sealed class Slot(SemaphoreSlim slots) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                slots.Release();
            }
        }
    }
}
