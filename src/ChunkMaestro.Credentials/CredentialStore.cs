using ChunkMaestro.Domain;
using System.Security.Cryptography;

namespace ChunkMaestro.Credentials;

public enum CredentialFailure
{
    Absent,
    Denied,
    Locked,
    Unavailable,
    Unsupported,
    WrongScope,
    Conflict
}

public enum CredentialCompletion
{
    NotStarted,
    NotCreated,
    Unknown
}

public sealed class CredentialException(CredentialFailure failure, CredentialCompletion completion = CredentialCompletion.NotStarted) : Exception($"Credential operation failed: {failure}.")
{
    public CredentialCompletion Completion
    {
        get;
    } = completion;

    public CredentialFailure Failure
    {
        get;
    } = failure;
}

// Target is a nonsecret host/server/integration identity, never a display name or secret.
// Purpose is canonical lowercase ASCII; Windows credential targets are case-insensitive.
public sealed record CredentialScope(FleetId Fleet, Guid Target, string Purpose);
public sealed record CredentialReference(CredentialScope Scope, Guid Id);
public delegate void SecretConsumer(ReadOnlySpan<byte> secret);

internal interface INativeCredentials : IDisposable
{
    void Create(string name, byte[] secret);
    byte[] Read(string name);
    void Delete(string name);
}

/// <summary>Trusted core only. Callbacks must not retain, log or export secrets.</summary>
public sealed class CredentialStore : IDisposable
{
    private static readonly AsyncLocal<CredentialStore?> CallbackOwner = new();
    private readonly INativeCredentials native;
    private readonly SemaphoreSlim gate = new(1, 1);
    private bool disposed;

    public CredentialStore() : this(CreateNative())
    {
    }

    internal CredentialStore(INativeCredentials native)
    {
        this.native = native;
    }

    private static INativeCredentials CreateNative()
    {
        if (OperatingSystem.IsMacOS())
        {
            return new MacCredentials();
        }
        if (OperatingSystem.IsWindows())
        {
            return new WindowsCredentials();
        }
        throw new CredentialException(CredentialFailure.Unsupported);
    }

    /// <summary>Pure reservation. Persist this nonsecret locator before requesting native creation.</summary>
    public static CredentialReference Reserve(CredentialScope scope)
    {
        Validate(scope);
        return new CredentialReference(scope, Guid.NewGuid());
    }

    public async Task<CredentialReference> CreateAsync(CredentialScope scope, CredentialReference reference, ReadOnlyMemory<byte> secret, CancellationToken cancellationToken = default)
    {
        RejectReentry();
        ValidateReference(scope, reference);
        if (secret.Length is < 1 or > 2560)
        {
            throw new ArgumentException("Credential size must be between 1 and 2560 bytes.", nameof(secret));
        }
        cancellationToken.ThrowIfCancellationRequested();
        byte[] copy = secret.ToArray();
        try
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Run(() =>
                {
                    try
                    {
                        native.Create(Name(reference), copy);
                    }
                    catch (UnauthorizedAccessException)
                    {
                        throw new CredentialException(CredentialFailure.Denied, CredentialCompletion.Unknown);
                    }
                    catch (CredentialException error)
                    {
                        throw new CredentialException(error.Failure, error.Failure == CredentialFailure.Conflict ? CredentialCompletion.NotCreated : CredentialCompletion.Unknown);
                    }
                }, CancellationToken.None).ConfigureAwait(false);
                return reference;
            }
            finally
            {
                gate.Release();
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(copy);
        }
    }

    public Task UseAsync(CredentialScope scope, CredentialReference reference, SecretConsumer consumer, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(consumer);
        return ExecuteAsync(scope, reference, () =>
        {
            byte[] secret = native.Read(Name(reference));
            try
            {
                CallbackOwner.Value = this;
                consumer(secret);
            }
            finally
            {
                CallbackOwner.Value = null;
                CryptographicOperations.ZeroMemory(secret);
            }
        }, cancellationToken);
    }

    public Task DeleteAsync(CredentialScope scope, CredentialReference reference, CancellationToken cancellationToken = default)
    {
        return ExecuteAsync(scope, reference, () => native.Delete(Name(reference)), cancellationToken);
    }

    private async Task ExecuteAsync(CredentialScope scope, CredentialReference reference, Action action, CancellationToken cancellationToken)
    {
        RejectReentry();
        ValidateReference(scope, reference);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Run(action, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private static void ValidateReference(CredentialScope scope, CredentialReference reference)
    {
        Validate(scope);
        ArgumentNullException.ThrowIfNull(reference);
        if (reference.Scope != scope || reference.Id == Guid.Empty)
        {
            throw new CredentialException(CredentialFailure.WrongScope);
        }
    }

    private static void Validate(CredentialScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (scope.Fleet.Value == Guid.Empty || scope.Target == Guid.Empty || scope.Purpose is null || scope.Purpose.Length is < 1 or > 64 || scope.Purpose.Any(c => !(c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-')))
        {
            throw new CredentialException(CredentialFailure.WrongScope);
        }
    }

    private static string Name(CredentialReference reference)
    {
        return $"ChunkMaestro.credentials.v1/{reference.Scope.Fleet.Value:N}/{reference.Scope.Target:N}/{reference.Scope.Purpose}/{reference.Id:N}";
    }

    // Waits for in-flight native calls and callbacks. Do not dispose/reenter from a callback.
    private static void RejectReentry()
    {
        if (CallbackOwner.Value is not null)
        {
            throw new InvalidOperationException("Credential callbacks cannot reenter their store.");
        }
    }

    public void Dispose()
    {
        RejectReentry();
        gate.Wait();
        try
        {
            if (!disposed)
            {
                disposed = true;
                native.Dispose();
            }
        }
        finally
        {
            gate.Release();
        }
    }
}
