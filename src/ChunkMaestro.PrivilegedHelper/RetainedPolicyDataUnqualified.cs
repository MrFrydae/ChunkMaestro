using System.Security.Cryptography;
using ParsedData = ChunkMaestro.PrivilegedHelper.PolicyDecodeResult.ParsedInspectionPolicyData;
using SnapshotFacts = ChunkMaestro.PrivilegedHelper.PolicySnapshotNative.SnapshotFacts;
using SnapshotHandle = ChunkMaestro.PrivilegedHelper.PolicySnapshotNative.SnapshotHandle;

namespace ChunkMaestro.PrivilegedHelper;

internal static partial class RetainedPolicyAcquisition
{
    // Passing the necessary numeric filter grants no admission or protection.
    internal sealed class RetainedPolicyDataUnqualified : IDisposable
    {
        private readonly object gate = new();
        private readonly SnapshotHandle handle;
        private readonly ParsedData data;
        private readonly SnapshotFacts facts;
        private bool disposed;
        private bool invalid;

        private RetainedPolicyDataUnqualified(SnapshotHandle handle, ParsedData data, SnapshotFacts facts)
        {
            this.handle = handle;
            this.data = data;
            this.facts = facts;
        }

        internal static RetainedPolicyDataUnqualified AcquireOwned(ref int parent, ref int file, CancellationToken token)
        {
            PolicySnapshotNative.RequireLinux();
            token.ThrowIfCancellationRequested();
            // Allocate cleanup ownership before native take can consume descriptors.
            SnapshotHandle pending = new(0);
            bool published = false;
            bool retained = false;
            try
            {
                int status = PolicySnapshotNative.Take(ref parent, ref file, out nint pointer);
                pending.Adopt(pointer);
                token.ThrowIfCancellationRequested();
                PolicySnapshotNative.CheckStatus(status);
                if (pending.IsInvalid)
                {
                    throw new InvalidOperationException("InvalidSnapshotAbi");
                }
                pending.DangerousAddRef(ref retained);
                token.ThrowIfCancellationRequested();
                byte[] envelope = new byte[128];
                status = PolicySnapshotNative.Facts(pending, envelope, 128, out uint written);
                token.ThrowIfCancellationRequested();
                PolicySnapshotNative.CheckStatus(status);
                if (written != 128)
                {
                    throw new InvalidOperationException("InvalidSnapshotAbi");
                }
                SnapshotFacts facts = PolicySnapshotNative.DecodeFacts(envelope);
                if (!facts.HasRootNumericShape)
                {
                    throw new InvalidOperationException("ProtectedShapeRefusal");
                }
                byte[] bytes = new byte[facts.ByteCount];
                token.ThrowIfCancellationRequested();
                status = PolicySnapshotNative.Copy(pending, bytes, facts.ByteCount, out written);
                token.ThrowIfCancellationRequested();
                PolicySnapshotNative.CheckStatus(status);
                if (written != facts.ByteCount)
                {
                    throw new InvalidOperationException("InvalidSnapshotAbi");
                }
                token.ThrowIfCancellationRequested();
                PolicyDecodeResult decoded = InstalledPolicyCodec.Decode(bytes, token);
                token.ThrowIfCancellationRequested();
                if (decoded is not ParsedData data)
                {
                    throw new InvalidOperationException("PolicyParseRefusal:" + ((PolicyDecodeResult.PolicyParseRefusal) decoded).Code);
                }
                if (!string.Equals(data.PolicyRevision, Convert.ToHexStringLower(SHA256.HashData(bytes)), StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("SnapshotDigestMismatch");
                }
                token.ThrowIfCancellationRequested();
                status = PolicySnapshotNative.Check(pending);
                token.ThrowIfCancellationRequested();
                PolicySnapshotNative.CheckStatus(status);
                // Transfer only this handle and the DATA decoded from its private copy.
                RetainedPolicyDataUnqualified lease = new(pending, data, facts);
                token.ThrowIfCancellationRequested();
                published = true;
                return lease;
            }
            finally
            {
                if (retained)
                {
                    pending.DangerousRelease();
                }
                if (!published)
                {
                    pending.Dispose();
                }
            }
        }

        internal string PolicyRevision
        {
            get
            {
                lock (gate)
                {
                    EnsureLive();
                    return data.PolicyRevision;
                }
            }
        }

        internal SnapshotFacts MeasuredFacts
        {
            get
            {
                lock (gate)
                {
                    EnsureLive();
                    return facts;
                }
            }
        }

        internal void Check(CancellationToken token = default)
        {
            lock (gate)
            {
                EnsureLive();
                token.ThrowIfCancellationRequested();
                int status = PolicySnapshotNative.Check(handle);
                invalid = status != 0;
                token.ThrowIfCancellationRequested();
                PolicySnapshotNative.CheckStatus(status);
            }
        }

        private void EnsureLive()
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (invalid)
            {
                throw new InvalidOperationException("SnapshotInvalidated");
            }
        }

        public void Dispose()
        {
            lock (gate)
            {
                if (!disposed)
                {
                    disposed = true;
                    handle.Dispose();
                }
            }
        }
    }
}
