using System.Runtime.CompilerServices;
using VideoOptimiser.Infrastructure.Persistence;

namespace VideoOptimiser.UnitTests;

/// <summary>
/// Shortens the finalise lock's refusal wait for every test in the assembly. The CLI, the queue verbs and the TUI take the lock
/// through <see cref="DatabaseLocks.TakeFinalizeAsync"/>, which waits in <see cref="DatabaseLock.AcquireFinalizeAsync"/>, and a test that holds it to see the refusal would otherwise sit out
/// the production 5 s. The wait-then-refuse and wait-then-acquire behaviour is tested on its own with explicit waits
/// (<c>RunLockTests</c>).
/// </summary>
internal static class FastLockWait
{
#pragma warning disable CA2255 // A test assembly has no entry point to set this from.
    [ModuleInitializer]
    internal static void Shorten() => DatabaseLock.FinalizeWait = TimeSpan.FromMilliseconds(50);
#pragma warning restore CA2255
}
