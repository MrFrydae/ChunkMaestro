using ChunkMaestro.PrivilegedHelper;
using System.Security.Cryptography;
using Xunit;
using static ChunkMaestro.HelperPolicyTests.PolicySessionJournalTests;

namespace ChunkMaestro.HelperPolicyTests;

public sealed class PolicySessionJournalV2Tests
{
    private static PolicySessionRecordV2Unqualified[] Generation(Guid boot)
    {
        PolicySessionRecordV2Unqualified empty = new(boot, Guid.Empty, 1, PolicySessionPhase.Empty,
            default, default, false, false, PolicySessionFault.None, 0, 0, 0, default);
        PolicySessionRecordV2Unqualified occupied = empty with
        {
            Revision = 2,
            Epoch = Guid.NewGuid(),
            Phase = PolicySessionPhase.Occupied,
            Agent = JournalFixture.Identity(1),
            NativeMetadata = new(1, 1, 1, 1, 0x802, 0)
        };
        PolicySessionRecordV2Unqualified attempted = occupied with
        {
            Revision = 3,
            Phase = PolicySessionPhase.StartAttempted,
            AttemptConsumed = true
        };
        PolicySessionRecordV2Unqualified bound = attempted with
        {
            Revision = 4,
            Phase = PolicySessionPhase.Bound,
            Worker = JournalFixture.Identity(2),
            NativeMetadata = new(3, 1, 1, 1, 0x802, 2)
        };
        PolicySessionRecordV2Unqualified armed = bound with
        {
            Revision = 5,
            Phase = PolicySessionPhase.Armed,
            EverArmed = true
        };
        PolicySessionRecordV2Unqualified retiring = armed with
        {
            Revision = 6,
            Phase = PolicySessionPhase.Retiring,
            FirstFailure = PolicySessionFault.Cancelled,
            FaultRevision = 6
        };
        PolicySessionRecordV2Unqualified retired = retiring with
        {
            Revision = 7,
            Phase = PolicySessionPhase.Retired,
            ProofRevision = 7,
            ProofSummary = 991
        };
        return [empty, occupied, attempted, bound, armed, retiring, retired, retired with
        {
            Revision = 8,
            Phase = PolicySessionPhase.Empty
        }];
    }

    private static PolicySessionJournalV2Unqualified Open(JournalFixture fixture,
        Action<PolicyJournalBarrier>? barrier = null, IPolicyJournalDurability? durability = null)
    {
        return new(fixture.Directory, durability ?? new TestDurability(), barrier);
    }

    [Fact]
    public void FullGenerationRoundtripKeepsMetadataAndOrdersBarriersBeforeReadback()
    {
        using JournalFixture fixture = new();
        List<PolicyJournalBarrier> trace = [];
        ModelDurability durability = new();
        using PolicySessionJournalV2Unqualified journal = Open(fixture, trace.Add, durability);
        journal.InitializeNewInstallation(new TestInstall(), fixture.Boot);
        PolicySessionRecordV2Unqualified[] states = Generation(fixture.Boot);
        Assert.Equal(states[0], journal.Read());
        for (int step = 1; step < states.Length; step++)
        {
            trace.Clear();
            journal.Commit(states[step - 1], states[step]);
            Assert.Equal(Enum.GetValues<PolicyJournalBarrier>(), trace);
            Assert.Equal(states[step], journal.Read());
            Assert.Equal(states[step].Encode(), File.ReadAllBytes(fixture.Path));
        }
        Assert.Equal(8, durability.Syncs);
        Assert.Equal(states[3].NativeMetadata, states[7].NativeMetadata);
        Assert.Empty(System.IO.Directory.GetFiles(fixture.Directory, "*.tmp"));
    }

    [Fact]
    public void EveryLegacyPhaseIncludingBothEmptyShapesRefusesCommitAndInitializationWithoutChanges()
    {
        using JournalFixture fixture = new();
        foreach (PolicySessionRecordV2Unqualified state in Generation(fixture.Boot))
        {
            PolicySessionRecord legacy = new(state.Boot, state.Epoch, state.Revision, state.Phase,
                state.Agent, state.Worker, state.AttemptConsumed, state.EverArmed, state.FirstFailure,
                state.FaultRevision, state.ProofRevision, state.ProofSummary);
            RefuseOriginal(fixture, legacy.Encode());
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(287)]
    [InlineData(289)]
    [InlineData(1048576)]
    public void WrongLengthNeverRewrites(int length)
    {
        using JournalFixture fixture = new();
        RefuseOriginal(fixture, new byte[length]);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(202)]
    [InlineData(212)]
    [InlineData(255)]
    [InlineData(287)]
    public void UnknownVersionAndSecondHalfCorruptionNeverRewrites(int offset)
    {
        using JournalFixture fixture = new();
        byte[] bytes = Generation(fixture.Boot)[3].Encode();
        bytes[offset] ^= 64;
        if (offset == 4)
        {
            SHA256.HashData(bytes.AsSpan(0, 256), bytes.AsSpan(256));
        }
        RefuseOriginal(fixture, bytes);
    }

    private static void RefuseOriginal(JournalFixture fixture, byte[] bytes)
    {
        File.WriteAllBytes(fixture.Path, bytes);
        string temporary = System.IO.Path.Combine(fixture.Directory, "session-interrupted.tmp");
        File.WriteAllText(temporary, "preserve");
        for (int operation = 0; operation < 3; operation++)
        {
            using PolicySessionJournalV2Unqualified journal = Open(fixture, _ => Assert.Fail("No write barrier allowed."));
            PolicySessionRecordV2Unqualified[] states = Generation(fixture.Boot);
            Assert.Throws<InvalidDataException>(() =>
            {
                if (operation == 0)
                {
                    journal.Read();
                }
                else if (operation == 1)
                {
                    journal.Commit(states[0], states[1]);
                }
                else
                {
                    journal.InitializeNewInstallation(new RejectInstall(), fixture.Boot);
                }
            });
            Assert.Throws<IOException>(() => journal.Read());
            Assert.Equal(bytes, File.ReadAllBytes(fixture.Path));
            Assert.Equal("preserve", File.ReadAllText(temporary));
            Assert.True(File.Exists(System.IO.Path.Combine(fixture.Directory, "session.lock")));
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void EveryBarrierAcrossEveryTransitionPoisonsAndReopensOnlyVisibleRecord(int stop)
    {
        using JournalFixture fixture = new();
        PolicySessionRecordV2Unqualified[] states = Generation(fixture.Boot);
        for (int step = 1; step < states.Length; step++)
        {
            File.WriteAllBytes(fixture.Path, states[step - 1].Encode());
            string[] before = System.IO.Directory.GetFiles(fixture.Directory, "*.tmp");
            using (PolicySessionJournalV2Unqualified journal = Open(fixture, point =>
            {
                if ((int) point == stop)
                {
                    throw new IOException("Modeled interrupted barrier.");
                }
            }))
            {
                Assert.Throws<IOException>(() => journal.Commit(states[step - 1], states[step]));
                Assert.Throws<IOException>(() => journal.Read());
                Assert.Throws<IOException>(() => journal.Commit(states[step - 1], states[step]));
            }
            using PolicySessionJournalV2Unqualified reopened = Open(fixture);
            Assert.Equal(stop == 3 ? states[step] : states[step - 1], reopened.Read());
            string[] after = System.IO.Directory.GetFiles(fixture.Directory, "*.tmp");
            Assert.Equal(before.Length + (stop == 3 ? 0 : 1), after.Length);
            if (stop != 3)
            {
                string added = Assert.Single(after.Except(before));
                byte[] partial = File.ReadAllBytes(added);
                Assert.Equal(stop == 0 ? 144 : 288, partial.Length);
                Assert.Equal(states[step].Encode()[..partial.Length], partial);
            }
            // Reopen only parses. No recovery/coordinator or start/publication API exists here.
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void DurabilitySupportSyncAndExactReadbackAmbiguityRefuse(int defect)
    {
        using JournalFixture fixture = new();
        PolicySessionRecordV2Unqualified[] states = Generation(fixture.Boot);
        File.WriteAllBytes(fixture.Path, states[2].Encode());
        ModelDurability durability = new()
        {
            Supported = defect != 0,
            OnSync = () =>
            {
                if (defect == 1)
                {
                    throw new IOException("Modeled directory sync/close ambiguity.");
                }
                if (defect == 2)
                {
                    File.WriteAllBytes(fixture.Path, states[2].Encode());
                }
                if (defect == 3)
                {
                    File.AppendAllText(fixture.Path, "changed length");
                }
            }
        };
        using (PolicySessionJournalV2Unqualified journal = Open(fixture, durability: durability))
        {
            Exception? failure = Record.Exception(() => journal.Commit(states[2], states[3]));
            Assert.True(defect == 3 ? failure is InvalidDataException : failure is IOException);
            Assert.Throws<IOException>(() => journal.Read());
        }
        using PolicySessionJournalV2Unqualified reopened = Open(fixture);
        if (defect == 3)
        {
            Assert.Throws<InvalidDataException>(() => reopened.Read());
        }
        else
        {
            Assert.Equal(states[defect == 1 ? 3 : 2], reopened.Read());
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    public void StaleCasAndGenerationMetadataDriftNeverReachReplacement(int defect)
    {
        using JournalFixture fixture = new();
        PolicySessionRecordV2Unqualified[] states = Generation(fixture.Boot);
        PolicySessionRecordV2Unqualified expected = states[2];
        PolicySessionRecordV2Unqualified next = states[3];
        switch (defect)
        {
            case 0:
                expected = expected with
                {
                    NativeMetadata = new(1, 1, 1, 1, 2, 0)
                };
                break;
            case 1:
                next = next with
                {
                    NativeMetadata = new(3, 1, 1, 1, 2, 2)
                };
                break;
            case 2:
                next = next with
                {
                    NativeMetadata = new(3, 2, 1, 1, 0x802, 2)
                };
                break;
            case 3:
                next = next with
                {
                    Agent = JournalFixture.Identity(9)
                };
                break;
            case 4:
                next = next with
                {
                    Worker = next.Agent
                };
                break;
            case 5:
                expected = states[3];
                next = states[4] with
                {
                    Worker = JournalFixture.Identity(9)
                };
                break;
            case 6:
                expected = states[6];
                next = states[7] with
                {
                    NativeMetadata = new(3, 1, 1, 1, 0x802, 0x802)
                };
                break;
            case 7:
                next = next with
                {
                    Boot = Guid.NewGuid()
                };
                break;
            case 8:
                next = next with
                {
                    Epoch = Guid.NewGuid()
                };
                break;
            case 9:
                next = next with
                {
                    NativeMetadata = default
                };
                break;
            case 10:
                expected = states[5];
                next = states[6] with
                {
                    FirstFailure = PolicySessionFault.Cleanup
                };
                break;
            case 11:
                expected = states[7];
                next = states[7] with
                {
                    Revision = 9
                };
                break;
        }
        PolicySessionRecordV2Unqualified actual = defect == 0 ? states[2] : expected;
        byte[] original = actual.Encode();
        File.WriteAllBytes(fixture.Path, original);
        using PolicySessionJournalV2Unqualified journal = Open(fixture, _ => Assert.Fail("No write allowed."));
        Exception? failure = Record.Exception(() => journal.Commit(expected, next));
        Assert.True(failure is InvalidOperationException or InvalidDataException);
        Assert.Equal(original, File.ReadAllBytes(fixture.Path));
        Assert.Empty(System.IO.Directory.GetFiles(fixture.Directory, "*.tmp"));
    }

    [Fact]
    public void SubsequentFaultKeepsFirstInvalidatesProofAndNeedsFreshRevision()
    {
        using JournalFixture fixture = new();
        PolicySessionRecordV2Unqualified retired = Generation(fixture.Boot)[6];
        File.WriteAllBytes(fixture.Path, retired.Encode());
        using PolicySessionJournalV2Unqualified journal = Open(fixture);
        PolicySessionRecordV2Unqualified faulted = retired with
        {
            Revision = 8,
            Phase = PolicySessionPhase.Retiring,
            FaultRevision = 8,
            ProofRevision = 0,
            ProofSummary = 0
        };
        journal.Commit(retired, faulted);
        Assert.Equal(PolicySessionFault.Cancelled, journal.Read().FirstFailure);
        PolicySessionRecordV2Unqualified proved = faulted with
        {
            Revision = 9,
            Phase = PolicySessionPhase.Retired,
            ProofRevision = 9,
            ProofSummary = 991
        };
        journal.Commit(faulted, proved);
        Assert.Equal(proved, journal.Read());
    }

    [Fact]
    public void CheckedOverflowDoesNotReplace()
    {
        using JournalFixture fixture = new();
        PolicySessionRecordV2Unqualified state = Generation(fixture.Boot)[2] with
        {
            Revision = ulong.MaxValue
        };
        byte[] original = state.Encode();
        File.WriteAllBytes(fixture.Path, original);
        using PolicySessionJournalV2Unqualified journal = Open(fixture);
        Assert.Throws<OverflowException>(() => journal.Commit(state, state));
        Assert.Equal(original, File.ReadAllBytes(fixture.Path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CaughtReentrantReadOrClosePoisonsBeforeLaterWrite(bool close)
    {
        using JournalFixture fixture = new();
        PolicySessionRecordV2Unqualified[] states = Generation(fixture.Boot);
        File.WriteAllBytes(fixture.Path, states[2].Encode());
        PolicySessionJournalV2Unqualified? owner = null;
        using PolicySessionJournalV2Unqualified journal = Open(fixture, point =>
        {
            if (point == PolicyJournalBarrier.Write)
            {
                Assert.NotNull(owner);
                Assert.Throws<InvalidOperationException>(() =>
                {
                    if (close)
                    {
                        owner.Dispose();
                    }
                    else
                    {
                        owner.Read();
                    }
                });
            }
        });
        owner = journal;
        Assert.Throws<IOException>(() => journal.Commit(states[2], states[3]));
        Assert.Equal(states[2].Encode(), File.ReadAllBytes(fixture.Path));
        Assert.Throws<IOException>(() => Open(fixture));
        Assert.Equal(144, new FileInfo(Assert.Single(System.IO.Directory.GetFiles(fixture.Directory, "*.tmp"))).Length);
    }

    [Fact]
    public void MissingJournalIgnoresCompleteTempsAndExplicitDisposeReleasesOnlyExclusion()
    {
        using JournalFixture fixture = new();
        File.WriteAllBytes(System.IO.Path.Combine(fixture.Directory, "session-valid.tmp"), Generation(fixture.Boot)[3].Encode());
        PolicySessionJournalV2Unqualified journal = Open(fixture);
        Assert.Throws<IOException>(() => Open(fixture));
        Assert.Throws<FileNotFoundException>(() => journal.Read());
        Assert.Throws<IOException>(() => journal.InitializeNewInstallation(new TestInstall(), fixture.Boot));
        journal.Dispose();
        journal.Dispose();
        Assert.Throws<ObjectDisposedException>(() => journal.Read());
        using PolicySessionJournalV2Unqualified reopened = Open(fixture);
        Assert.Throws<FileNotFoundException>(() => reopened.Read());
        Assert.Single(System.IO.Directory.GetFiles(fixture.Directory, "*.tmp"));
        Assert.True(File.Exists(System.IO.Path.Combine(fixture.Directory, "session.lock")));
    }

    [Fact]
    public void ProductionDurabilityCommitsOnLinuxAndRefusesUnsupportedPlatforms()
    {
        using JournalFixture fixture = new();
        using PolicySessionJournalV2Unqualified journal = new(fixture.Directory);
        if (OperatingSystem.IsLinux())
        {
            PolicySnapshotNative.RequireLinux();
            journal.InitializeNewInstallation(new TestInstall(), fixture.Boot);
            PolicySessionRecordV2Unqualified[] states = Generation(fixture.Boot);
            Assert.Equal(states[0], journal.Read());
            journal.Commit(states[0], states[1]);
            Assert.Equal(states[1], journal.Read());
            Assert.Equal(states[1].Encode(), File.ReadAllBytes(fixture.Path));
            Assert.Empty(System.IO.Directory.GetFiles(fixture.Directory, "*.tmp"));
            return;
        }
        Assert.Throws<PlatformNotSupportedException>(() => journal.InitializeNewInstallation(new TestInstall(), fixture.Boot));
        Assert.False(File.Exists(fixture.Path));
        Assert.Empty(System.IO.Directory.GetFiles(fixture.Directory, "*.tmp"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void InitializationInterruptionDoesNotPromoteTemporaryState(int stop)
    {
        using JournalFixture fixture = new();
        using (PolicySessionJournalV2Unqualified journal = Open(fixture, point =>
        {
            if ((int) point == stop)
            {
                throw new IOException("Modeled install interruption.");
            }
        }))
        {
            Assert.Throws<IOException>(() => journal.InitializeNewInstallation(new TestInstall(), fixture.Boot));
            Assert.Throws<IOException>(() => journal.Read());
        }
        using PolicySessionJournalV2Unqualified reopened = Open(fixture);
        if (stop == 3)
        {
            Assert.Equal(PolicySessionPhase.Empty, reopened.Read().Phase);
        }
        else
        {
            Assert.Throws<FileNotFoundException>(() => reopened.Read());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EarlyRetirementRetainsAgentOnlyAndNewGenerationReplacesClaimsOnlyAtEmpty(bool attempted)
    {
        using JournalFixture fixture = new();
        PolicySessionRecordV2Unqualified state = Generation(fixture.Boot)[attempted ? 2 : 1];
        File.WriteAllBytes(fixture.Path, state.Encode());
        using PolicySessionJournalV2Unqualified journal = Open(fixture);
        PolicySessionRecordV2Unqualified retiring = state with
        {
            Revision = state.Revision + 1,
            Phase = PolicySessionPhase.Retiring
        };
        journal.Commit(state, retiring);
        PolicySessionRecordV2Unqualified retired = retiring with
        {
            Revision = retiring.Revision + 1,
            Phase = PolicySessionPhase.Retired,
            ProofRevision = retiring.Revision + 1,
            ProofSummary = 575
        };
        journal.Commit(retiring, retired);
        PolicySessionRecordV2Unqualified empty = retired with
        {
            Revision = retired.Revision + 1,
            Phase = PolicySessionPhase.Empty
        };
        journal.Commit(retired, empty);
        Assert.Equal(state.NativeMetadata, journal.Read().NativeMetadata);
        PolicySessionRecordV2Unqualified fresh = Generation(fixture.Boot)[1] with
        {
            Revision = empty.Revision + 1
        };
        journal.Commit(empty, fresh);
        Assert.Equal(fresh, journal.Read());
        // Only claims were serialized: no prior-owner closure, manager or install proof obtained.
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LatestFaultCannotCarryOldProofOrBeHiddenByFutureProof(bool future)
    {
        using JournalFixture fixture = new();
        PolicySessionRecordV2Unqualified retired = Generation(fixture.Boot)[6];
        File.WriteAllBytes(fixture.Path, retired.Encode());
        using PolicySessionJournalV2Unqualified journal = Open(fixture, _ => Assert.Fail("No write allowed."));
        PolicySessionRecordV2Unqualified invalid = retired with
        {
            Revision = 8,
            Phase = future ? PolicySessionPhase.Retired : PolicySessionPhase.Retiring,
            FaultRevision = 8,
            ProofRevision = future ? 8UL : 7UL
        };
        Assert.Throws<InvalidDataException>(() => journal.Commit(retired, invalid));
        Assert.Equal(retired.Encode(), File.ReadAllBytes(fixture.Path));
    }

    private sealed class RejectInstall : PolicySessionInstallAdapter
    {
        internal override void RequireQualifiedNewInstallation(string directory)
        {
            Assert.Fail("Existing journal must be refused before installation adapter.");
        }
    }

    // MODEL ONLY: no directory fsync, power loss, native ancestry or platform qualification.
    private sealed class ModelDurability : IPolicyJournalDurability
    {
        internal bool Supported = true;
        internal int Syncs;
        internal Action? OnSync;

        public void RequireSupported()
        {
            if (!Supported)
            {
                throw new IOException("Modeled unsupported durability.");
            }
        }

        public void SynchronizeDirectory(string directory)
        {
            Syncs++;
            OnSync?.Invoke();
        }
    }
}
