using ChunkMaestro.PrivilegedHelper;
using System.Buffers.Binary;
using System.Security.Cryptography;
using Xunit;

namespace ChunkMaestro.HelperPolicyTests;

public sealed class PolicySessionRecordV2Tests
{
    private const string Golden = "434d504a0200000033221100554477668899aabbccddeeff443322116655887799aabbccddeeff000807060504030201030100000000000000000000000000000000000000000000010101010101010101010101010101010132547698badcfe01223344556677880177665544332211013344556677889903030303030303030303030303030303020202020202020202020202020202020232547698badcfe022233445566778802776655443322110233445566778899040404040404040404040404040404040000030101010000020800000200000000000000000000000000000000000000000000000000000000000000000000000000000000000000853680fa079b868184e8a592ee8a4138040163670c385aa55fee9f6af7616dcd";
    private const string LegacyGolden = "434d504a0100000033221100554477668899aabbccddeeff443322116655887799aabbccddeeff000807060504030201030100000000000000000000000000000000000000000000010101010101010101010101010101010132547698badcfe01223344556677880177665544332211013344556677889903030303030303030303030303030303020202020202020202020202020202020232547698badcfe02223344556677880277665544332211023344556677889904040404040404040404040404040404000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000072ee7427b1fed6064cba1e3a4ec26acaa59df0a3870ee72fa3e68d9e6e00e1c0";

    private static byte[] Fixture()
    {
        return Convert.FromHexString(Golden);
    }

    private static void Checksum(byte[] bytes)
    {
        SHA256.HashData(bytes.AsSpan(0, 256), bytes.AsSpan(256));
    }

    private static void Refuse(byte[] bytes)
    {
        InvalidDataException error = Assert.Throws<InvalidDataException>(() => PolicySessionRecordV2Unqualified.Decode(bytes));
        Assert.True(error.Message is "Invalid or unsupported session journal." or "Inconsistent session journal correlation." or
            "Invalid session journal metadata." or "Inconsistent session journal metadata.");
    }

    [Theory]
    [InlineData((byte) 1)]
    [InlineData((byte) 2)]
    public void LiteralGoldenRoundtripsBothOfflineProfilesWithExplicitRoleOrder(byte architecture)
    {
        byte[] bytes = Fixture();
        bytes[203] = architecture;
        Checksum(bytes);
        PolicySessionRecordV2Unqualified record = PolicySessionRecordV2Unqualified.Decode(bytes);
        Assert.Equal(bytes, record.Encode());
        PolicySessionRecordV2Unqualified literalValue = new(
            Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"),
            Guid.Parse("11223344-5566-7788-99aa-bbccddeeff00"),
            0x0102030405060708UL, PolicySessionPhase.Bound,
            new(Guid.Parse("01010101-0101-0101-0101-010101010101"), 0xfedcba9876543201UL,
                0x8877665544332201UL, 0x1122334455667701UL, 0x9988776655443301UL,
                Guid.Parse("03030303-0303-0303-0303-030303030303")),
            new(Guid.Parse("02020202-0202-0202-0202-020202020202"), 0xfedcba9876543202UL,
                0x8877665544332202UL, 0x1122334455667702UL, 0x9988776655443302UL,
                Guid.Parse("04040404-0404-0404-0404-040404040404")),
            true, false, PolicySessionFault.None, 0, 0, 0, new(3, architecture, 1, 1, 0x802, 2));
        Assert.Equal(literalValue, record);
        Assert.Equal(bytes, literalValue.Encode());
        Assert.Equal(Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"), record.Boot);
        Assert.Equal(0x102030405060708UL, record.Revision);
        Assert.Equal(0xfedcba9876543201UL, record.Agent.PidfsDevice);
        Assert.Equal(0x8877665544332202UL, record.Worker.PidfsInode);
        PolicySessionExpectedNativePairV2Unqualified pair = record.ExpectedNativePair(record.Boot, architecture);
        Assert.Equal(new(record.Worker.PidfsDevice, record.Worker.PidfsInode, 2), pair.AtNativeIndex(0));
        Assert.Equal(new(record.Agent.PidfsDevice, record.Agent.PidfsInode, 0x802), pair.AtNativeIndex(1));
        pair.RequireMatch(pair.Worker, pair.Agent);
        Assert.Throws<InvalidDataException>(() => pair.RequireMatch(pair.Agent, pair.Worker));
        Assert.Throws<InvalidDataException>(() => pair.RequireMatch(pair.Worker with
        {
            Flags = 0x802
        }, pair.Agent));
        Assert.Throws<InvalidDataException>(() => pair.AtNativeIndex(2));
        Assert.Throws<InvalidDataException>(() => record.ExpectedNativePair(Guid.Empty, architecture));
        Assert.Throws<InvalidDataException>(() => record.ExpectedNativePair(record.Boot, architecture == 1 ? (byte) 2 : (byte) 1));
        bytes[208] = 0;
        byte[] encoded = record.Encode();
        encoded[212] = 0;
        Assert.Equal(0x802U, record.NativeMetadata.AgentFlags);
        Assert.Equal(2U, record.NativeMetadata.WorkerFlags);
        Assert.Equal(2U, pair.Worker.Flags);
        PolicySessionRecordV2Unqualified inverse = record with
        {
            NativeMetadata = record.NativeMetadata with
            {
                AgentFlags = 2,
                WorkerFlags = 0x802
            }
        };
        Assert.Equal(0x802U, PolicySessionRecordV2Unqualified.Decode(inverse.Encode()).ExpectedNativePair(record.Boot, architecture).AtNativeIndex(0).Flags);
        Assert.NotEqual(record, inverse);
    }

    [Fact]
    public void EveryShapeByteAndFlagBitIsValidatedAfterChecksum()
    {
        foreach (int offset in new int[]
        {
            48, 49, 50, 51, 202, 203, 204, 205
        })
        {
            for (int value = 0; value <= 255; value++)
            {
                byte[] bytes = Fixture();
                bytes[offset] = (byte) value;
                Checksum(bytes);
                bool valid = offset switch
                {
                    48 => value is 3 or 5,
                    49 => value == 1,
                    50 => value == 0,
                    51 => value == 0,
                    202 => value == 3,
                    203 => value is 1 or 2,
                    _ => value == 1
                };
                if (valid)
                {
                    Assert.Equal(bytes, PolicySessionRecordV2Unqualified.Decode(bytes).Encode());
                }
                else
                {
                    Refuse(bytes);
                }
            }
        }
        for (int offset = 0; offset < 256; offset++)
        {
            if (offset is >= 52 and < 56 or >= 206 and < 208 or >= 216 and < 256)
            {
                byte[] bytes = Fixture();
                bytes[offset] = 1;
                Checksum(bytes);
                Refuse(bytes);
            }
        }
        foreach (int offset in new int[]
        {
            208, 212
        })
        {
            for (int bit = 0; bit < 32; bit++)
            {
                byte[] bytes = Fixture();
                uint flags = 2U ^ (1U << bit);
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), flags);
                Checksum(bytes);
                if (bit == 11)
                {
                    Assert.Equal(flags, offset == 208 ? PolicySessionRecordV2Unqualified.Decode(bytes).NativeMetadata.AgentFlags : PolicySessionRecordV2Unqualified.Decode(bytes).NativeMetadata.WorkerFlags);
                }
                else
                {
                    Refuse(bytes);
                }
            }
        }
    }

    [Fact]
    public void MetadataAndCorrelationCannotInventMissingMeasurementsOrIndependentTasks()
    {
        PolicySessionRecordV2Unqualified bound = PolicySessionRecordV2Unqualified.Decode(Fixture());
        foreach (PolicySessionIdentity duplicate in new PolicySessionIdentity[]
        {
            bound.Worker with
            {
                PidfsDevice = bound.Agent.PidfsDevice, PidfsInode = bound.Agent.PidfsInode
            },
            bound.Worker with
            {
                PidfsInode = bound.Agent.PidfsInode
            },
            bound.Worker with
            {
                Invocation = bound.Agent.Invocation
            }
        })
        {
            byte[] bytes = Fixture();
            duplicate.Invocation.TryWriteBytes(bytes.AsSpan(136, 16));
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(152), duplicate.PidfsDevice);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(160), duplicate.PidfsInode);
            Checksum(bytes);
            Refuse(bytes);
            Assert.Throws<InvalidDataException>(() => (bound with
            {
                Worker = duplicate
            }).Encode());
        }
        byte[] missing = Fixture();
        Array.Clear(missing, 202, 14);
        Checksum(missing);
        Refuse(missing);
        foreach (int offset in new int[]
        {
            208, 212
        })
        {
            byte[] bytes = Fixture();
            Array.Clear(bytes, offset, 4);
            Checksum(bytes);
            Refuse(bytes);
        }
        for (int offset = 72; offset < 200; offset += 8)
        {
            byte[] bytes = Fixture();
            Array.Clear(bytes, offset, 8);
            Checksum(bytes);
            if (offset % 64 is 24 or 32 or 40 or 48)
            {
                Refuse(bytes);
            }
        }
        byte[] proof = Fixture();
        proof[200] = 1;
        Checksum(proof);
        Refuse(proof);
    }

    [Fact]
    public void EveryLegacyPhaseAndFutureVersionRefusesWithoutFallback()
    {
        byte[] literal = Convert.FromHexString(LegacyGolden);
        PolicySessionRecord legacy = PolicySessionRecord.Decode(literal);
        Assert.Equal(literal, legacy.Encode());
        Assert.Throws<InvalidDataException>(() => PolicySessionRecord.Decode(Fixture()));
        foreach (PolicySessionPhase phase in Enum.GetValues<PolicySessionPhase>())
        {
            PolicySessionRecord candidate = legacy with
            {
                Phase = phase,
                EverArmed = phase == PolicySessionPhase.Armed,
                AttemptConsumed = phase is not (PolicySessionPhase.Empty or PolicySessionPhase.Occupied),
                Worker = phase is PolicySessionPhase.Empty or PolicySessionPhase.Occupied or PolicySessionPhase.StartAttempted ? default : legacy.Worker,
                Epoch = phase == PolicySessionPhase.Empty ? Guid.Empty : legacy.Epoch,
                Agent = phase == PolicySessionPhase.Empty ? default : legacy.Agent,
                ProofRevision = phase == PolicySessionPhase.Retired ? 2UL : 0UL,
                ProofSummary = phase == PolicySessionPhase.Retired ? (ushort) 1023 : (ushort) 0
            };
            byte[] bytes = candidate.Encode();
            Assert.Equal(candidate, PolicySessionRecord.Decode(bytes));
            Refuse(bytes);
        }
        foreach (uint version in new uint[]
        {
            0, 1, 3, uint.MaxValue
        })
        {
            byte[] bytes = Fixture();
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), version);
            Checksum(bytes);
            Refuse(bytes);
            Array.Clear(bytes, 24, 16);
            bytes[48] = 0;
            Checksum(bytes);
            Refuse(bytes);
        }
    }

    [Fact]
    public void AllPhasesPreserveProofAndPresenceRules()
    {
        PolicySessionRecordV2Unqualified bound = PolicySessionRecordV2Unqualified.Decode(Fixture());
        PolicySessionRecordV2Unqualified pristine = bound with
        {
            Epoch = Guid.Empty,
            Phase = PolicySessionPhase.Empty,
            Agent = default,
            Worker = default,
            AttemptConsumed = false,
            NativeMetadata = new(0, 0, 0, 0, 0, 0)
        };
        Assert.Equal(pristine, PolicySessionRecordV2Unqualified.Decode(pristine.Encode()));
        foreach (PolicySessionPhase phase in Enum.GetValues<PolicySessionPhase>())
        {
            foreach (bool armed in new bool[]
            {
                false, true
            })
            {
                bool early = phase is PolicySessionPhase.Occupied or PolicySessionPhase.StartAttempted;
                bool proved = phase is PolicySessionPhase.Empty or PolicySessionPhase.Retired;
                PolicySessionRecordV2Unqualified record = bound with
                {
                    Phase = phase,
                    EverArmed = armed,
                    AttemptConsumed = phase != PolicySessionPhase.Occupied,
                    Worker = early ? default : bound.Worker,
                    NativeMetadata = early ? new(1, 1, 1, 1, 0x802, 0) : bound.NativeMetadata,
                    ProofRevision = proved ? 2UL : 0UL,
                    ProofSummary = proved ? (ushort) 1023 : (ushort) 0
                };
                if ((early || phase == PolicySessionPhase.Bound) && armed || phase == PolicySessionPhase.Armed && !armed)
                {
                    Assert.Throws<InvalidDataException>(() => record.Encode());
                }
                else
                {
                    Assert.Equal(record, PolicySessionRecordV2Unqualified.Decode(record.Encode()));
                }
            }
        }
        foreach (int offset in new int[]
        {
            40, 56, 64, 200, 201
        })
        {
            byte[] bytes = Fixture();
            bytes[offset] = 255;
            Checksum(bytes);
            if (offset != 40)
            {
                Refuse(bytes);
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RetainedProofRequiresEveryConjunctAndLatestFaultOrdering(bool armed)
    {
        PolicyRetirementObservations common = PolicyRetirementObservations.GateRevoked |
            PolicyRetirementObservations.SoleJobTerminal | PolicyRetirementObservations.NoPendingJobs |
            PolicyRetirementObservations.ProtectedDomainEmpty | PolicyRetirementObservations.OwnedReferencesClosed |
            PolicyRetirementObservations.OriginalAgentReferencesClosed;
        PolicyRetirementObservations required = common | (armed
            ? PolicyRetirementObservations.OriginalWorkerHup | PolicyRetirementObservations.SameWorkerGenerationTerminal |
                PolicyRetirementObservations.AllThreadDescendantAndAsyncClosure
            : PolicyRetirementObservations.StartupNoInputRightsChildrenOrAsyncRoutes);
        PolicySessionRecordV2Unqualified proved = PolicySessionRecordV2Unqualified.Decode(Fixture()) with
        {
            Phase = PolicySessionPhase.Retired,
            EverArmed = armed,
            FirstFailure = PolicySessionFault.Cancelled,
            FaultRevision = 2,
            ProofRevision = 3,
            ProofSummary = (ushort) required
        };
        byte[] encoded = proved.Encode();
        Assert.Equal(proved, PolicySessionRecordV2Unqualified.Decode(encoded));
        foreach (PolicyRetirementObservations observation in Enum.GetValues<PolicyRetirementObservations>())
        {
            if (observation != 0 && (required & observation) != 0)
            {
                byte[] bytes = encoded.ToArray();
                BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(200), (ushort) (required & ~observation));
                Checksum(bytes);
                Refuse(bytes);
            }
        }
        foreach (ulong fault in new ulong[]
        {
            0, 3, 4, ulong.MaxValue
        })
        {
            byte[] bytes = encoded.ToArray();
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(56), fault);
            Checksum(bytes);
            Refuse(bytes);
        }
        PolicySessionRecordV2Unqualified retained = proved with
        {
            Phase = PolicySessionPhase.Empty
        };
        Assert.Equal(retained, PolicySessionRecordV2Unqualified.Decode(retained.Encode()));
        Assert.Equal(proved.NativeMetadata, retained.NativeMetadata);
        if (!armed)
        {
            PolicySessionRecordV2Unqualified early = retained with
            {
                Worker = default,
                NativeMetadata = new(1, 1, 1, 1, 0x802, 0)
            };
            Assert.Equal(early, PolicySessionRecordV2Unqualified.Decode(early.Encode()));
            Assert.Throws<InvalidDataException>(() => early.ExpectedNativePair(early.Boot, 1));
        }
    }

    [Fact]
    public void BoundedRandomAndStructuralCorruptionOnlyReturnUnqualifiedValuesOrSanitizedRefusals()
    {
        for (int length = 0; length <= 1024; length++)
        {
            if (length != 288)
            {
                Refuse(new byte[length]);
            }
        }
        Refuse(new byte[1024 * 1024]);
        for (int offset = 0; offset < 288; offset++)
        {
            byte[] bytes = Fixture();
            bytes[offset] ^= 1;
            Refuse(bytes);
        }
        Random random = new(1402026);
        for (int trial = 0; trial < 2000; trial++)
        {
            byte[] bytes = new byte[random.Next(0, 1025)];
            random.NextBytes(bytes);
            Refuse(bytes);
            byte[] mutation = Fixture();
            mutation[random.Next(8, 256)] ^= (byte) (1 << random.Next(8));
            Checksum(mutation);
            try
            {
                PolicySessionRecordV2Unqualified value = PolicySessionRecordV2Unqualified.Decode(mutation);
                Assert.Equal(mutation, value.Encode());
            }
            catch (InvalidDataException)
            {
                Refuse(mutation);
            }
        }
    }
}
