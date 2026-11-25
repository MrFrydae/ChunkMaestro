using ChunkMaestro.PrivilegedHelper;
using Xunit;
using static ChunkMaestro.HelperPolicyTests.PolicySessionJournalTests;

namespace ChunkMaestro.HelperPolicyTests;

public sealed class PolicySessionCoordinatorTests
{
    [Fact]
    public void EffectsObserveDurableOrderAndReorderedExactStoreAndDrain()
    {
        using Rig rig = new();
        Assert.Throws<InvalidOperationException>(() => new PolicySessionCoordinatorUnqualified(rig.Journal, rig.Fixture.Boot, rig.Ports));
        rig.Ports.Reverse = true;
        rig.Start();
        string[] expected = ["empty-boundary", "start:StartAttempted", "bind:StartAttempted", "upload:Bound", "read:Bound", "publish:Armed"];
        Assert.Equal(expected, rig.Ports.Trace);
        rig.Owner.RevokeUnqualified(PolicySessionFault.Cancelled);
        rig.Owner.FinishRetirementUnqualified();
        Assert.True(rig.Owner.DrainedUnqualified);
        Assert.Equal(PolicySessionPhase.Empty, rig.Journal.Read().Phase);
        Assert.Equal(2, rig.Ports.Trace.Count(item => item == "remove:Retired"));
        Assert.Equal("close:Empty", rig.Ports.Trace[^1]);
        Assert.Throws<InvalidOperationException>(() => rig.Reserve());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void StoreRejectionNeverPublishesOrRetriesUpload(int defect)
    {
        using Rig rig = new();
        rig.Ports.Defect = defect;
        Assert.Throws<InvalidOperationException>(rig.Start);
        Assert.DoesNotContain("publish:Armed", rig.Ports.Trace);
        Assert.Equal(1, rig.Ports.Trace.Count(item => item == "upload:Bound"));
        Assert.Throws<InvalidOperationException>(rig.Owner.StartBindUploadReleaseUnqualified);
        Assert.NotEqual(PolicySessionPhase.Empty, rig.Journal.Read().Phase);
    }

    [Theory]
    [InlineData("start")]
    [InlineData("upload")]
    [InlineData("publish")]
    public void UnknownEffectAcknowledgementRetainsOccupancyAndNeverReplays(string effect)
    {
        using Rig rig = new();
        rig.Ports.Fail = effect;
        Assert.Throws<IOException>(rig.Start);
        Assert.False(rig.Owner.DrainedUnqualified);
        Assert.Equal(effect == "publish", rig.Owner.State.EverArmed);
        Assert.Throws<InvalidOperationException>(rig.Owner.StartBindUploadReleaseUnqualified);
        Assert.Equal(1, rig.Ports.Trace.Count(item => item.StartsWith(effect + ":", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OrderedCancellationBeforeOrAfterStartPreventsLaterRelease(bool late)
    {
        using Rig rig = new();
        rig.Reserve();
        if (late)
        {
            rig.Ports.OnEffect = effect =>
            {
                if (effect == "upload")
                {
                    // A reentrant cancellation request is refused/poisoned, never ignored.
                    Assert.Throws<InvalidOperationException>(() => rig.Owner.RevokeUnqualified(PolicySessionFault.Cancelled));
                }
            };
            Assert.Throws<InvalidOperationException>(rig.Owner.StartBindUploadReleaseUnqualified);
        }
        else
        {
            rig.Owner.RevokeUnqualified(PolicySessionFault.Cancelled);
            Assert.Throws<InvalidOperationException>(rig.Owner.StartBindUploadReleaseUnqualified);
        }
        Assert.DoesNotContain("publish:Armed", rig.Ports.Trace);
        Assert.False(rig.Owner.DrainedUnqualified);
    }

    [Theory]
    [InlineData("start")]
    [InlineData("bind")]
    [InlineData("upload")]
    [InlineData("read")]
    [InlineData("publish")]
    public void RevocationDuringEveryPortPoisonsItsCompletion(string point)
    {
        using Rig rig = new();
        rig.Ports.OnEffect = effect =>
        {
            if (effect == point)
            {
                Assert.Throws<InvalidOperationException>(() => rig.Owner.RevokeUnqualified(PolicySessionFault.Cancelled));
            }
        };
        Assert.Throws<InvalidOperationException>(rig.Start);
        Assert.EndsWith(point == "publish" ? "publish:Armed" : point + ":" + (point is "upload" or "read" ? "Bound" : "StartAttempted"), rig.Ports.Trace[^1]);
        int count = rig.Ports.Trace.Count;
        Assert.Throws<InvalidOperationException>(rig.Owner.StartBindUploadReleaseUnqualified);
        Assert.Equal(count, rig.Ports.Trace.Count);
    }

    [Fact]
    public void DeadlineEqualityBeforeSendIsExpiredAndCannotBeExtended()
    {
        using Rig rig = new();
        rig.Reserve();
        rig.Ports.Clock = 10;
        Assert.Throws<InvalidOperationException>(rig.Owner.StartBindUploadReleaseUnqualified);
        Assert.DoesNotContain("start:StartAttempted", rig.Ports.Trace);
        Assert.Equal(PolicySessionFault.Deadline, rig.Owner.State.FirstFailure);
    }

    [Fact]
    public void StaleProofAndCleanupFailureInvalidateRetiredRevision()
    {
        using Rig rig = new();
        rig.Start();
        rig.Owner.RevokeUnqualified(PolicySessionFault.Cancelled);
        rig.Ports.Fail = "remove";
        Assert.Throws<IOException>(rig.Owner.FinishRetirementUnqualified);
        Assert.Equal(PolicySessionPhase.Retiring, rig.Owner.State.Phase);
        Assert.Equal(0UL, rig.Owner.State.ProofRevision);
        Assert.Equal(PolicySessionFault.Cancelled, rig.Owner.State.FirstFailure);
        rig.Ports.Fail = null;
        rig.Ports.StaleProof = true;
        Assert.Throws<InvalidOperationException>(rig.Owner.FinishRetirementUnqualified);
        Assert.DoesNotContain("close:Empty", rig.Ports.Trace);
        rig.Ports.StaleProof = false;
        rig.Owner.FinishRetirementUnqualified();
        Assert.True(rig.Owner.DrainedUnqualified);
    }

    [Fact]
    public void PartialRemovalFaultRequiresFreshProofBeforeExactRemainingSubset()
    {
        using Rig rig = new();
        rig.Start();
        rig.Owner.RevokeUnqualified(PolicySessionFault.Agent);
        int removes = 0;
        rig.Ports.OnEffect = effect =>
        {
            if (effect == "remove" && ++removes == 2)
            {
                throw new IOException("Unknown second removal.");
            }
        };
        Assert.Throws<IOException>(rig.Owner.FinishRetirementUnqualified);
        Assert.True(rig.Owner.CleanupFailedUnqualified);
        Assert.Equal(0UL, rig.Owner.State.ProofRevision);
        rig.Ports.OnEffect = null;
        rig.Owner.FinishRetirementUnqualified();
        Assert.True(rig.Owner.DrainedUnqualified);
        Assert.Equal(2, rig.Ports.Trace.Count(item => item == "proof:Retiring"));
        Assert.Equal(3, rig.Ports.Trace.Count(item => item == "remove:Retired"));
    }

    [Fact]
    public void DeadlineAfterConsumeBeforeInlineStartPreventsSend()
    {
        using JournalFixture fixture = new();
        using (PolicySessionJournal initial = fixture.Initialize())
        {
        }
        Ports? ports = null;
        int writes = 0;
        using PolicySessionJournal journal = fixture.Open(barrier =>
        {
            if (barrier == PolicyJournalBarrier.Write)
            {
                writes++;
            }
            if (writes == 2 && barrier == PolicyJournalBarrier.DirectorySync)
            {
                Assert.NotNull(ports);
                ports.Clock = 10;
            }
        });
        ports = new(journal);
        PolicySessionCoordinatorUnqualified owner = new(journal, fixture.Boot, ports);
        owner.ReserveUnqualified(Guid.NewGuid(), JournalFixture.Identity(1), 0, 10);
        Assert.Throws<InvalidOperationException>(owner.StartBindUploadReleaseUnqualified);
        Assert.True(owner.State.AttemptConsumed);
        Assert.DoesNotContain("start:StartAttempted", ports.Trace);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmptyCommitHoldsExclusionUntilCloseCompletesAndCloseFailureNeverRetries(bool fail)
    {
        using Rig rig = new();
        rig.Start();
        rig.Owner.RevokeUnqualified(PolicySessionFault.Cancelled);
        using ManualResetEventSlim closing = new();
        using ManualResetEventSlim release = new();
        rig.Ports.OnEffect = effect =>
        {
            if (effect == "close")
            {
                Assert.Equal(PolicySessionPhase.Empty, rig.Journal.Read().Phase);
                Assert.Throws<IOException>(() => rig.Fixture.Open());
                closing.Set();
                Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
            }
        };
        rig.Ports.Fail = fail ? "close" : null;
        Task finish = Task.Run(rig.Owner.FinishRetirementUnqualified, TestContext.Current.CancellationToken);
        Assert.True(closing.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Task reserve = Task.Run(() => Assert.Throws<InvalidOperationException>(rig.Reserve), TestContext.Current.CancellationToken);
        Assert.False(finish.IsCompleted);
        Assert.False(reserve.IsCompleted);
        release.Set();
        if (fail)
        {
            await Assert.ThrowsAsync<IOException>(() => finish);
        }
        else
        {
            await finish;
        }
        await reserve;
        Assert.Equal(!fail, rig.Owner.DrainedUnqualified);
        Assert.Throws<InvalidOperationException>(rig.Owner.FinishRetirementUnqualified);
        Assert.Equal(1, rig.Ports.Trace.Count(item => item == "close:Empty"));
    }

    [Fact]
    public void DelayedCompletionAndReentryCannotPublishOrEscapeGeneration()
    {
        using Rig rig = new();
        rig.Reserve();
        rig.Ports.OnEffect = effect =>
        {
            if (effect == "bind")
            {
                Assert.Throws<InvalidOperationException>(rig.Reserve);
            }
        };
        Assert.Throws<InvalidOperationException>(rig.Owner.StartBindUploadReleaseUnqualified);
        Assert.DoesNotContain("upload:Bound", rig.Ports.Trace);
        Assert.DoesNotContain("publish:Armed", rig.Ports.Trace);
    }

    [Fact]
    public async Task DelayedBindCompletionCannotSucceedAfterOwnerReentryPoison()
    {
        using Rig rig = new();
        using ManualResetEventSlim entered = new();
        using ManualResetEventSlim release = new();
        rig.Ports.OnEffect = effect =>
        {
            if (effect == "bind")
            {
                entered.Set();
                Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
                Assert.Throws<InvalidOperationException>(rig.Reserve);
            }
        };
        Task startup = Task.Run(rig.Start, TestContext.Current.CancellationToken);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.False(startup.IsCompleted);
        release.Set();
        await Assert.ThrowsAsync<InvalidOperationException>(() => startup);
        Assert.DoesNotContain("upload:Bound", rig.Ports.Trace);
        Assert.DoesNotContain("publish:Armed", rig.Ports.Trace);
    }

    [Fact]
    public void EmptyReopenRequiresPriorOwnerBoundaryRatherThanJournalEnum()
    {
        using Rig rig = new();
        rig.Start();
        rig.Owner.RevokeUnqualified(PolicySessionFault.Cancelled);
        rig.Ports.Fail = "close";
        Assert.Throws<IOException>(rig.Owner.FinishRetirementUnqualified);
        rig.Journal.Dispose(); // Simulated process death/FD closure, NOT actual native proof.
        using PolicySessionJournal reopened = rig.Fixture.Open();
        Ports ports = new(reopened)
        {
            Fail = "empty-boundary"
        };
        PolicySessionCoordinatorUnqualified owner = new(reopened, rig.Fixture.Boot, ports);
        Assert.Throws<IOException>(() => owner.ReserveUnqualified(Guid.NewGuid(), JournalFixture.Identity(4), 0, 10));
        Assert.Equal(PolicySessionPhase.Empty, reopened.Read().Phase);
        Assert.Equal("empty-boundary", Assert.Single(ports.Trace));
    }

    [Fact]
    public void AllJournalBarrierFailuresAcrossAllTransitionsRefuseFurtherEffectsAndReopenNeverStarts()
    {
        foreach (PolicyJournalBarrier barrier in Enum.GetValues<PolicyJournalBarrier>())
        {
            for (int transition = 1; transition <= 7; transition++)
            {
                using JournalFixture fixture = new();
                using (PolicySessionJournal initial = fixture.Initialize())
                {
                }
                int writes = 0;
                using (PolicySessionJournal journal = fixture.Open(point =>
                {
                    if (point == PolicyJournalBarrier.Write)
                    {
                        writes++;
                    }
                    if (writes == transition && point == barrier)
                    {
                        throw new IOException("Injected barrier failure.");
                    }
                }))
                {
                    Ports ports = new(journal);
                    PolicySessionCoordinatorUnqualified owner = new(journal, fixture.Boot, ports);
                    Assert.Throws<IOException>(() =>
                    {
                        owner.ReserveUnqualified(Guid.NewGuid(), JournalFixture.Identity(1), 0, 10);
                        owner.StartBindUploadReleaseUnqualified();
                        owner.RevokeUnqualified(PolicySessionFault.Cancelled);
                        owner.FinishRetirementUnqualified();
                    });
                    int effects = ports.Trace.Count;
                    Assert.Throws<InvalidOperationException>(owner.StartBindUploadReleaseUnqualified);
                    Assert.Equal(effects, ports.Trace.Count);
                    Assert.DoesNotContain("close:Empty", ports.Trace);
                }
                using PolicySessionJournal reopened = fixture.Open();
                Ports recoveryPorts = new(reopened);
                PolicySessionCoordinatorUnqualified recovered = new(reopened, fixture.Boot, recoveryPorts);
                Assert.Throws<InvalidOperationException>(recovered.StartBindUploadReleaseUnqualified);
                Assert.Empty(recoveryPorts.Trace);
            }
        }
    }

    private sealed class Rig : IDisposable
    {
        internal readonly JournalFixture Fixture = new();
        internal readonly PolicySessionJournal Journal;
        internal readonly Ports Ports;
        internal readonly PolicySessionCoordinatorUnqualified Owner;

        internal Rig()
        {
            Journal = Fixture.Initialize();
            Ports = new(Journal);
            Owner = new(Journal, Fixture.Boot, Ports);
        }

        internal void Reserve()
        {
            Owner.ReserveUnqualified(Guid.NewGuid(), JournalFixture.Identity(1), 0, 10);
        }

        internal void Start()
        {
            Reserve();
            Owner.StartBindUploadReleaseUnqualified();
        }

        public void Dispose()
        {
            Journal.Dispose();
            Fixture.Dispose();
        }
    }

    private sealed class Ports(PolicySessionJournal journal) : PolicySessionCoordinatorPortsUnqualified
    {
        internal readonly List<string> Trace = [];
        internal long Clock;
        internal bool Reverse;
        internal bool StaleProof;
        internal int Defect = -1;
        internal string? Fail;
        internal Action<string>? OnEffect;
        private readonly Guid holder = Guid.NewGuid();
        private readonly List<PolicyEscrowEntryUnqualified> entries = [];

        internal override long BoottimeUnqualified()
        {
            return Clock;
        }

        private void Effect(string name, PolicySessionRecord? state = null)
        {
            if (state != null)
            {
                Assert.Equal(state, journal.Read());
            }
            Trace.Add(state == null ? name : $"{name}:{state.Phase}");
            OnEffect?.Invoke(name);
            if (Fail == name)
            {
                throw new IOException("Unknown unqualified effect acknowledgement.");
            }
        }

        internal override void ObserveEmptyOwnerBoundaryUnqualified(PolicySessionRecord state)
        {
            Assert.Equal(state, journal.Read());
            Effect("empty-boundary");
        }

        internal override void StartOriginalUnqualified(PolicySessionRecord state)
        {
            Assert.True(state.AttemptConsumed);
            Assert.Equal(PolicySessionPhase.StartAttempted, state.Phase);
            Effect("start", state);
        }

        internal override PolicySessionIdentity AcquireOriginalWorkerUnqualified(PolicySessionRecord state)
        {
            Effect("bind", state);
            return JournalFixture.Identity(2);
        }

        internal override void UploadOnceUnqualified(PolicySessionRecord state)
        {
            Effect("upload", state);
            PolicyEscrowFlags flags = PolicyEscrowFlags.OriginalNonThreadPidfd | PolicyEscrowFlags.CloseOnExec | PolicyEscrowFlags.PollingDisabled;
            entries.Add(new(state.AgentEscrowName, state.Agent, PolicyEscrowRole.Agent, flags));
            entries.Add(new(state.WorkerEscrowName, state.Worker, PolicyEscrowRole.Worker, flags));
        }

        internal override PolicySessionStoreUnqualified ReadStoreUnqualified(PolicySessionRecord state)
        {
            Effect("read", state);
            PolicyEscrowEntryUnqualified[] rows = entries.ToArray();
            if (Defect == 0)
            {
                rows = [];
            }
            if (Defect == 1)
            {
                rows = [rows[0]];
            }
            if (Defect == 2)
            {
                rows = [rows[0], rows[0]];
            }
            if (Defect == 3)
            {
                rows[1] = rows[1] with
                {
                    Original = JournalFixture.Identity(8)
                };
            }
            if (Defect == 4)
            {
                rows[1] = rows[1] with
                {
                    Name = "unknown"
                };
            }
            if (Defect == 5)
            {
                rows[1] = rows[1] with
                {
                    Flags = PolicyEscrowFlags.None
                };
            }
            if (Reverse)
            {
                Array.Reverse(rows);
            }
            return new(Evidence(state), Defect == 6 ? 3 : rows.Length, rows, holder, 1);
        }

        internal override void PublishOnceUnqualified(PolicySessionRecord state)
        {
            Assert.True(state.EverArmed);
            Effect("publish", state);
        }

        internal override PolicySessionEvidenceUnqualified ObserveRetirementUnqualified(PolicySessionRecord state)
        {
            Effect("proof", state);
            return Evidence(state) with
            {
                Revision = StaleProof ? state.Revision - 1 : state.Revision,
                Observations = (PolicyRetirementObservations) 1023
            };
        }

        internal override void RemoveExactNameUnqualified(PolicySessionRecord state, string name)
        {
            Assert.Equal(PolicySessionPhase.Retired, state.Phase);
            Assert.NotEqual(0UL, state.ProofRevision);
            Effect("remove", state);
            Assert.Equal(1, entries.RemoveAll(entry => entry.Name == name));
        }

        internal override void RemovalBarrierUnqualified(PolicySessionRecord state)
        {
            Effect("barrier", state);
        }

        internal override void CloseLocalWitnessesOnceUnqualified()
        {
            Effect("close", journal.Read());
        }

        private static PolicySessionEvidenceUnqualified Evidence(PolicySessionRecord state)
        {
            return new(state.Boot, state.Epoch, state.Revision, state.Agent, state.Worker, PolicyRetirementObservations.None);
        }
    }
}
