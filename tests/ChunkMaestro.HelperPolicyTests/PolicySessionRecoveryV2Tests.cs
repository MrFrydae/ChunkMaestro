using ChunkMaestro.PrivilegedHelper;
using Xunit;
using static ChunkMaestro.HelperPolicyTests.PolicySessionJournalTests;

namespace ChunkMaestro.HelperPolicyTests;

public sealed class PolicySessionRecoveryV2Tests
{
    internal static PolicySessionJournalV2Unqualified Open(JournalFixture fixture, Action<PolicyJournalBarrier>? barrier = null)
    {
        return new(System.IO.Path.GetDirectoryName(fixture.Path)!, new ModelDurability(), barrier);
    }

    internal static PolicySessionJournalV2Unqualified Initialize(JournalFixture fixture)
    {
        File.WriteAllBytes(fixture.Path, new PolicySessionRecordV2Unqualified(fixture.Boot, Guid.Empty, 1,
            PolicySessionPhase.Empty, default, default, false, false, PolicySessionFault.None, 0, 0, 0, default).Encode());
        return Open(fixture);
    }

    private sealed class ModelDurability : IPolicyJournalDurability
    {
        public void RequireSupported()
        {
        }

        public void SynchronizeDirectory(string directory)
        {
            // BCL model fixture only, no native directory fsync claim.
        }
    }

    internal static void Bound(PolicySessionRecoveryV2Unqualified recovery, byte architecture = 2, bool inverse = false)
    {
        uint agentFlags = inverse ? 2U : 0x802U;
        uint workerFlags = inverse ? 0x802U : 2U;
        recovery.Reserve(Guid.NewGuid(), new(JournalFixture.Identity(1), new(1, architecture, 1, 1, agentFlags, 0)));
        recovery.ConsumeStartAttempt();
        PolicySessionIdentity worker = JournalFixture.Identity(2);
        PolicySessionIdentity agent = recovery.State.Agent;
        recovery.BindOriginalWorker(new(worker, new(worker.PidfsDevice, worker.PidfsInode, workerFlags),
            new(agent.PidfsDevice, agent.PidfsInode, agentFlags), new(3, architecture, 1, 1, agentFlags, workerFlags)));
    }

    internal static EscrowEntryV2Unqualified[] Rows(PolicySessionRecordV2Unqualified state)
    {
        PolicySessionExpectedNativePairV2Unqualified pair = state.ExpectedNativePair(state.Boot, state.NativeMetadata.Architecture);
        return [new($"cm-worker-{state.Epoch:N}", state.Worker, PolicyEscrowRole.Worker, (PolicyEscrowFlags) 7, pair.Worker),
            new($"cm-agent-{state.Epoch:N}", state.Agent, PolicyEscrowRole.Agent, (PolicyEscrowFlags) 7, pair.Agent)];
    }

    internal static IEnumerable<EvidenceKeyV2Unqualified> WrongKeys(EvidenceKeyV2Unqualified key)
    {
        yield return key with
        {
            Boot = Guid.NewGuid()
        };
        yield return key with
        {
            Epoch = Guid.NewGuid()
        };
        yield return key with
        {
            Revision = key.Revision + 1
        };
        foreach (PolicySessionIdentity identity in WrongIdentities(key.Agent))
        {
            yield return key with
            {
                Agent = identity
            };
        }
        foreach (PolicySessionIdentity identity in WrongIdentities(key.Worker))
        {
            yield return key with
            {
                Worker = identity
            };
        }
        yield return key with
        {
            NativeMetadata = key.NativeMetadata with
            {
                Presence = 1
            }
        };
        yield return key with
        {
            NativeMetadata = key.NativeMetadata with
            {
                Architecture = 1
            }
        };
        yield return key with
        {
            NativeMetadata = key.NativeMetadata with
            {
                Normalization = 2
            }
        };
        yield return key with
        {
            NativeMetadata = key.NativeMetadata with
            {
                Source = 2
            }
        };
        yield return key with
        {
            NativeMetadata = key.NativeMetadata with
            {
                AgentFlags = 2
            }
        };
        yield return key with
        {
            NativeMetadata = key.NativeMetadata with
            {
                WorkerFlags = 0x802
            }
        };
    }

    private static IEnumerable<PolicySessionIdentity> WrongIdentities(PolicySessionIdentity identity)
    {
        yield return identity with
        {
            Invocation = Guid.NewGuid()
        };
        yield return identity with
        {
            PidfsDevice = identity.PidfsDevice + 1
        };
        yield return identity with
        {
            PidfsInode = identity.PidfsInode + 1
        };
        yield return identity with
        {
            StartTime = identity.StartTime + 1
        };
        yield return identity with
        {
            NamespaceInode = identity.NamespaceInode + 1
        };
        yield return identity with
        {
            Domain = Guid.NewGuid()
        };
    }

    [Fact]
    public void EveryKeyFieldIsRequiredAtEveryProofInventoryAndEmptyBoundary()
    {
        using JournalFixture fixture = new();
        using PolicySessionJournalV2Unqualified journal = Initialize(fixture);
        PolicySessionRecoveryV2Unqualified recovery = new(journal, fixture.Boot, 2);
        Bound(recovery);
        foreach (EvidenceKeyV2Unqualified wrong in WrongKeys(recovery.Key))
        {
            Assert.Throws<InvalidOperationException>(() => recovery.Arm(new(wrong, 0), Rows(recovery.State)));
            Assert.Equal(PolicySessionPhase.Bound, journal.Read().Phase);
        }
        recovery.Retire(PolicySessionFault.Agent);
        foreach (EvidenceKeyV2Unqualified wrong in WrongKeys(recovery.Key))
        {
            Assert.Throws<InvalidOperationException>(() => recovery.ConfirmRetirement(new(wrong, (PolicyRetirementObservations) 575)));
        }
        recovery.ConfirmRetirement(new(recovery.Key, (PolicyRetirementObservations) 575));
        byte[] bytes = File.ReadAllBytes(fixture.Path);
        foreach (EvidenceKeyV2Unqualified wrong in WrongKeys(recovery.Key))
        {
            Assert.Throws<InvalidOperationException>(() => recovery.ValidateRemainingEscrow(new(wrong, 0), []));
            Assert.Throws<InvalidOperationException>(() => recovery.ConfirmEmptyStoreReadback(new(wrong, 0), []));
            Assert.Equal(bytes, File.ReadAllBytes(fixture.Path));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EveryMissingConjunctDeniesAndLaterFaultRequiresNewProof(bool armed)
    {
        using JournalFixture fixture = new();
        using PolicySessionJournalV2Unqualified journal = Initialize(fixture);
        PolicySessionRecoveryV2Unqualified recovery = new(journal, fixture.Boot, 2);
        Bound(recovery);
        if (armed)
        {
            recovery.Arm(new(recovery.Key, 0), Rows(recovery.State));
        }
        recovery.Retire(PolicySessionFault.Agent);
        ushort required = armed ? (ushort) 991 : (ushort) 575;
        for (int bit = 1; bit <= 512; bit <<= 1)
        {
            if ((required & bit) != 0)
            {
                Assert.Throws<InvalidOperationException>(() => recovery.ConfirmRetirement(new(recovery.Key, (PolicyRetirementObservations) (required & ~bit))));
            }
        }
        recovery.ConfirmRetirement(new(recovery.Key, (PolicyRetirementObservations) required));
        EvidenceKeyV2Unqualified stale = recovery.Key;
        recovery.Retire(PolicySessionFault.Worker);
        Assert.Equal(PolicySessionFault.Agent, journal.Read().FirstFailure);
        Assert.Equal(journal.Read().Revision, journal.Read().FaultRevision);
        Assert.Equal(0UL, journal.Read().ProofRevision);
        Assert.Throws<InvalidOperationException>(() => recovery.ConfirmRetirement(new(stale, (PolicyRetirementObservations) required)));
        recovery.ConfirmRetirement(new(recovery.Key, (PolicyRetirementObservations) required));
        Assert.True(journal.Read().ProofRevision > journal.Read().FaultRevision);
    }

    [Fact]
    public void EveryRemainingRoleTupleAndPredicateMismatchDeniesBeforeMutation()
    {
        using JournalFixture fixture = new();
        using PolicySessionJournalV2Unqualified journal = Initialize(fixture);
        PolicySessionRecoveryV2Unqualified recovery = new(journal, fixture.Boot, 2);
        Bound(recovery);
        recovery.Retire(PolicySessionFault.None);
        recovery.ConfirmRetirement(new(recovery.Key, (PolicyRetirementObservations) 575));
        byte[] bytes = File.ReadAllBytes(fixture.Path);
        EscrowEntryV2Unqualified[] rows = Rows(recovery.State);
        foreach (EscrowEntryV2Unqualified row in rows)
        {
            EscrowEntryV2Unqualified[] wrong =
            [
                row with
                {
                    Name = "unknown"
                },
                row with
                {
                    Role = (PolicyEscrowRole) 99
                },
                row with
                {
                    Role = row.Role == PolicyEscrowRole.Agent ? PolicyEscrowRole.Worker : PolicyEscrowRole.Agent
                },
                row with
                {
                    Flags = (PolicyEscrowFlags) 3
                },
                row with
                {
                    Original = JournalFixture.Identity(9)
                },
                row with
                {
                    Native = row.Native with
                    {
                        Device = row.Native.Device + 1
                    }
                },
                row with
                {
                    Native = row.Native with
                    {
                        Inode = row.Native.Inode + 1
                    }
                },
                row with
                {
                    Native = row.Native with
                    {
                        Flags = row.Native.Flags == 2 ? 0x802U : 2U
                    }
                }
            ];
            foreach (EscrowEntryV2Unqualified invalid in wrong)
            {
                Assert.Throws<InvalidOperationException>(() => recovery.ValidateRemainingEscrow(new(recovery.Key, 0), [invalid]));
                Assert.Equal(bytes, File.ReadAllBytes(fixture.Path));
            }
            Assert.Throws<InvalidOperationException>(() => recovery.ValidateRemainingEscrow(new(recovery.Key, 0), [row, row]));
            recovery.ValidateRemainingEscrow(new(recovery.Key, 0), [row]);
        }
        Assert.Throws<InvalidOperationException>(() => recovery.ConfirmEmptyStoreReadback(new(recovery.Key, 0), rows));
        Assert.Throws<InvalidOperationException>(() => recovery.ValidateRemainingEscrow(new(recovery.Key, 0), [rows[0], rows[1], rows[0]]));
        Assert.Equal(bytes, File.ReadAllBytes(fixture.Path));
    }

    [Fact]
    public void OverflowRetireNoneAndAgentOnlyUnexpectedWorkerPreserveBytes()
    {
        using JournalFixture fixture = new();
        using PolicySessionJournalV2Unqualified journal = Initialize(fixture);
        PolicySessionRecoveryV2Unqualified recovery = new(journal, fixture.Boot, 2);
        recovery.Reserve(Guid.NewGuid(), new(JournalFixture.Identity(1), new(1, 2, 1, 1, 2, 0)));
        recovery.Retire(PolicySessionFault.Agent);
        ulong fault = recovery.State.FaultRevision;
        recovery.Retire(PolicySessionFault.None);
        Assert.Equal(fault, recovery.State.FaultRevision);
        Assert.Equal(PolicySessionFault.Agent, recovery.State.FirstFailure);
        recovery.ConfirmRetirement(new(recovery.Key, (PolicyRetirementObservations) 575));
        EscrowEntryV2Unqualified worker = new($"cm-worker-{recovery.State.Epoch:N}", JournalFixture.Identity(2),
            PolicyEscrowRole.Worker, (PolicyEscrowFlags) 7, new(1, 2, 2));
        Assert.Throws<InvalidOperationException>(() => recovery.ValidateRemainingEscrow(new(recovery.Key, 0), [worker]));
        PolicySessionRecordV2Unqualified last = recovery.State with
        {
            Revision = ulong.MaxValue
        };
        File.WriteAllBytes(fixture.Path, last.Encode());
        PolicySessionRecoveryV2Unqualified overflow = new(journal, fixture.Boot, 2);
        Assert.Throws<OverflowException>(() => overflow.Retire(PolicySessionFault.Worker));
        Assert.Equal(last.Encode(), File.ReadAllBytes(fixture.Path));
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    public void BothProfilesAndAsymmetricNativeRoleMappingsRoundtrip(byte architecture, bool inverse)
    {
        using JournalFixture fixture = new();
        using PolicySessionJournalV2Unqualified journal = Initialize(fixture);
        PolicySessionRecoveryV2Unqualified recovery = new(journal, fixture.Boot, architecture);
        Bound(recovery, architecture, inverse);
        PolicySessionExpectedNativePairV2Unqualified pair = journal.Read().ExpectedNativePair(fixture.Boot, architecture);
        Assert.Equal(inverse ? 0x802U : 2U, pair.AtNativeIndex(0).Flags);
        Assert.Equal(inverse ? 2U : 0x802U, pair.AtNativeIndex(1).Flags);
        recovery.Arm(new(recovery.Key, 0), Rows(recovery.State));
        Assert.Equal(recovery.State.Encode(), File.ReadAllBytes(fixture.Path));
    }
}
