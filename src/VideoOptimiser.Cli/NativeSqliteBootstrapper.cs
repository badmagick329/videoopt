using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace VideoOptimiser.Cli;

internal static class NativeSqliteBootstrapper
{
    private const string ResourceName = "VideoOptimiser.Native.e_sqlite3.dll";
    private static readonly object Gate = new();
    private static IntPtr _libraryHandle;

    public static void Initialize()
    {
        lock (Gate)
        {
            if (_libraryHandle != IntPtr.Zero)
            {
                return;
            }

            var assembly = typeof(NativeSqliteBootstrapper).Assembly;
            using var resource = assembly.GetManifestResourceStream(ResourceName)
                ?? throw new InvalidOperationException("The embedded SQLite library is missing.");
            using var memory = new MemoryStream();
            resource.CopyTo(memory);
            var bytes = memory.ToArray();
            var hash = Convert.ToHexString(SHA256.HashData(bytes));
            var directory = Path.Combine(Path.GetTempPath(), "VideoOptimiser", "native", hash);
            var path = Path.Combine(directory, "e_sqlite3.dll");

            Directory.CreateDirectory(directory);
            if (!File.Exists(path) || !SHA256.HashData(File.ReadAllBytes(path)).AsSpan().SequenceEqual(SHA256.HashData(bytes)))
            {
                File.WriteAllBytes(path, bytes);
            }

            _libraryHandle = NativeLibrary.Load(path);
        }
    }
}
