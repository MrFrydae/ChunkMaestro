using ChunkMaestro.Persistence;

namespace ChunkMaestro.PersistenceTests;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
#if IDENTITY_NATIVE_TESTS
        if (args.Length == 6 && args[0] == "--identity-phase")
        {
            return await ControllerIdentityLifecycleTests.RunPhaseWorker(args);
        }
#endif
        if (args.Length != 2 || args[0] != "--hold-test-store")
        {
            return 2;
        }

        await using LocalFleetStore store = await LocalFleetStore.OpenAsync(StorageLocation.ForTests(args[1]));
        Console.WriteLine("LOCKED");
        await Console.In.ReadLineAsync();
        return 0;
    }
}
