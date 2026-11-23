using ChunkMaestro.Credentials;
using ChunkMaestro.Domain;

namespace ChunkMaestro.CredentialsTests;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length != 6 || args[0] is not ("creator-a" or "creator-b"))
        {
            return 99;
        }
        CredentialScope scope = new(new FleetId(Guid.Parse(args[2])), Guid.Parse(args[3]), args[4]);
        CredentialReference reference = new(scope, Guid.Parse(args[5]));
        try
        {
            using CredentialStore store = OperatingSystem.IsMacOS() ? new CredentialStore(MacCredentials.OpenFixture(args[1])) : new CredentialStore();
            byte[] synthetic = args[0] == "creator-a" ? [1] : [2];
            try
            {
                await store.CreateAsync(scope, reference, synthetic);
            }
            finally
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(synthetic);
            }
            return 0;
        }
        catch (CredentialException error) when (error.Failure == CredentialFailure.Conflict)
        {
            return 20;
        }
    }
}
