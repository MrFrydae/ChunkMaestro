using ChunkMaestro.PrivilegedHelper;
using Xunit;
using static ChunkMaestro.HelperPolicyTests.PolicySessionJournalTests;
using static ChunkMaestro.HelperPolicyTests.PolicySessionRecoveryV2Tests;

namespace ChunkMaestro.HelperPolicyTests;

public sealed class PolicySessionCoordinatorV2Tests
{
    [Fact]
    public void EffectsObserveDurableOrderAndReorderedExactStoreAndDrain()
    {
        using Rig rig = new();
        Assert.Throws<InvalidOperationException>(() => new PolicySessionCoordinatorV2Unqualified(rig.Journal, rig.Fixture.Boot, 2, rig.Ports));
        rig.Ports.Reverse = true;
        rig.Start();
        string[] expected = ["empty-boundary", "agent", "start:StartAttempted", "bind:StartAttempted", "upload:Bound", "read:Bound", "publish:Armed"];
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
        using (PolicySessionJournalV2Unqualified initial = Initialize(fixture))
        {
        }
        Ports? ports = null;
        int writes = 0;
        using PolicySessionJournalV2Unqualified journal = Open(fixture, barrier =>
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
        ports = new(journal, fixture.Path);
        PolicySessionCoordinatorV2Unqualified owner = new(journal, fixture.Boot, 2, ports);
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
                Assert.Throws<IOException>(() => Open(rig.Fixture));
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
        using PolicySessionJournalV2Unqualified reopened = Open(rig.Fixture);
        Ports ports = new(reopened, rig.Fixture.Path)
        {
            Fail = "empty-boundary"
        };
        PolicySessionCoordinatorV2Unqualified owner = new(reopened, rig.Fixture.Boot, 2, ports);
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
                using (PolicySessionJournalV2Unqualified initial = Initialize(fixture))
                {
                }
                int writes = 0;
                using (PolicySessionJournalV2Unqualified journal = Open(fixture, point =>
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
                    Ports ports = new(journal, fixture.Path);
                    PolicySessionCoordinatorV2Unqualified owner = new(journal, fixture.Boot, 2, ports);
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
                using PolicySessionJournalV2Unqualified reopened = Open(fixture);
                Ports recoveryPorts = new(reopened, fixture.Path);
                PolicySessionCoordinatorV2Unqualified recovered = new(reopened, fixture.Boot, 2, recoveryPorts);
                Assert.Throws<InvalidOperationException>(recovered.StartBindUploadReleaseUnqualified);
                Assert.Empty(recoveryPorts.Trace);
            }
        }
    }

    [Fact]
    public void SnapshotOwnsScalarsAndAgentDriftNeverUploads()
    {
        using (Rig rig = new())
        {
            rig.Ports.MutateAfterSnapshot = true;
            rig.Start();
            Assert.True(rig.Owner.State.EverArmed);
            rig.Owner.RevokeUnqualified(PolicySessionFault.None);
            rig.Owner.FinishRetirementUnqualified();
            Assert.True(rig.Owner.DrainedUnqualified);
        }
        using (Rig rig = new())
        {
            rig.Ports.DriftAgent = true;
            Assert.Throws<InvalidOperationException>(rig.Start);
            Assert.DoesNotContain("upload:Bound", rig.Ports.Trace);
            Assert.Equal((byte) 1, rig.Journal.Read().NativeMetadata.Presence);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(4)]
    public void WrongNativeOrUnqualifiedStoreNeverArms(int defect)
    {
        using Rig rig = new();
        rig.Ports.StoreDefect = defect;
        Assert.Throws<InvalidOperationException>(rig.Start);
        Assert.False(rig.Journal.Read().EverArmed);
        Assert.DoesNotContain("publish:Armed", rig.Ports.Trace);
    }

    [Theory]
    [InlineData("barrier")]
    [InlineData("count")]
    [InlineData("holder")]
    [InlineData("configuration")]
    [InlineData("nonempty")]
    public void CleanupRequiresBarrierNewZeroCountAndStableManager(string defect)
    {
        using Rig rig = new();
        rig.Start();
        rig.Owner.RevokeUnqualified(PolicySessionFault.Cancelled);
        rig.Ports.OnEffect = point =>
        {
            if (point == "barrier")
            {
                rig.Ports.Defect = defect == "count" ? 6 : -1;
                rig.Ports.ChangedHolder = defect == "holder";
                rig.Ports.ChangedConfiguration = defect == "configuration";
                rig.Ports.StoreDefect = defect == "nonempty" ? 2 : 0;
            }
        };
        rig.Ports.Fail = defect == "barrier" ? "barrier" : null;
        Assert.ThrowsAny<Exception>(rig.Owner.FinishRetirementUnqualified);
        Assert.NotEqual(PolicySessionPhase.Empty, rig.Journal.Read().Phase);
        Assert.DoesNotContain("close:Empty", rig.Ports.Trace);
    }

    [Fact]
    public void LostAppliedRemovalAcknowledgementReconcilesRemainingSubsetWithFreshProof()
    {
        using Rig rig = new();
        rig.Start();
        rig.Owner.RevokeUnqualified(PolicySessionFault.Agent);
        rig.Ports.LoseRemovalAck = true;
        IOException original = Assert.Throws<IOException>(rig.Owner.FinishRetirementUnqualified);
        Assert.Same(original, rig.Owner.FirstOperationFailure);
        Assert.Equal(0UL, rig.Journal.Read().ProofRevision);
        rig.Owner.FinishRetirementUnqualified();
        Assert.True(rig.Owner.DrainedUnqualified);
        Assert.Equal(2, rig.Ports.Trace.Count(item => item == "remove:Retired"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AgentOnlyAmbiguousStartRequiresNoWorkProof(bool deny)
    {
        using Rig rig = new();
        rig.Ports.Fail = "start";
        Assert.Throws<IOException>(rig.Start);
        Assert.Equal((byte) 1, rig.Journal.Read().NativeMetadata.Presence);
        rig.Ports.Fail = null;
        rig.Ports.DenyProof = deny;
        if (deny)
        {
            Assert.Throws<InvalidOperationException>(rig.Owner.FinishRetirementUnqualified);
            Assert.NotEqual(PolicySessionPhase.Empty, rig.Journal.Read().Phase);
        }
        else
        {
            rig.Owner.FinishRetirementUnqualified();
            Assert.True(rig.Owner.DrainedUnqualified);
        }
        Assert.DoesNotContain("bind:StartAttempted", rig.Ports.Trace);
        Assert.DoesNotContain("upload:Bound", rig.Ports.Trace);
    }

    [Theory]
    [InlineData("bind")]
    [InlineData("read")]
    public void DeadlineAfterInlineObservationPreventsNextEffect(string point)
    {
        using Rig rig = new();
        rig.Ports.OnEffect = effect =>
        {
            if (effect == point)
            {
                rig.Ports.Clock = 10;
            }
        };
        Assert.Throws<InvalidOperationException>(rig.Start);
        Assert.Equal(PolicySessionFault.Deadline, rig.Journal.Read().FirstFailure);
        Assert.DoesNotContain("publish:Armed", rig.Ports.Trace);
        if (point == "bind")
        {
            Assert.DoesNotContain("upload:Bound", rig.Ports.Trace);
        }
    }

    [Fact]
    public async Task ConcurrentCancellationLinearizesAfterAlreadyEnteredInlineSend()
    {
        using Rig rig = new();
        using ManualResetEventSlim sending = new();
        using ManualResetEventSlim release = new();
        using ManualResetEventSlim cancellationRequested = new();
        rig.Ports.OnEffect = point =>
        {
            if (point == "publish")
            {
                sending.Set();
                Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
            }
        };
        Task startup = Task.Run(rig.Start, TestContext.Current.CancellationToken);
        Assert.True(sending.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Task cancel = Task.Run(() =>
        {
            cancellationRequested.Set();
            rig.Owner.RevokeUnqualified(PolicySessionFault.Cancelled);
        }, TestContext.Current.CancellationToken);
        Assert.True(cancellationRequested.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.False(cancel.IsCompleted);
        release.Set();
        await startup;
        await cancel;
        Assert.True(rig.Journal.Read().EverArmed);
        Assert.Equal(PolicySessionPhase.Retiring, rig.Journal.Read().Phase);
        Assert.Equal(1, rig.Ports.Trace.Count(item => item == "publish:Armed"));
    }

    [Fact]
    public void EveryReopenedNonemptyPhaseIsRetireOnlyIncludingUnusedOccupied()
    {
        foreach (PolicySessionPhase phase in Enum.GetValues<PolicySessionPhase>())
        {
            if (phase == PolicySessionPhase.Empty)
            {
                continue;
            }
            using JournalFixture fixture = new();
            using (PolicySessionJournalV2Unqualified journal = Initialize(fixture))
            {
                PolicySessionRecoveryV2Unqualified recovery = new(journal, fixture.Boot, 2);
                recovery.Reserve(Guid.NewGuid(), new(JournalFixture.Identity(1), new(1, 2, 1, 1, 0x802, 0)));
                if (phase != PolicySessionPhase.Occupied)
                {
                    recovery.ConsumeStartAttempt();
                }
                if (phase is not (PolicySessionPhase.Occupied or PolicySessionPhase.StartAttempted))
                {
                    PolicySessionIdentity worker = JournalFixture.Identity(2);
                    recovery.BindOriginalWorker(new(worker, new(worker.PidfsDevice, worker.PidfsInode, 2),
                        new(recovery.State.Agent.PidfsDevice, recovery.State.Agent.PidfsInode, 0x802), new(3, 2, 1, 1, 0x802, 2)));
                }
                if (phase == PolicySessionPhase.Armed)
                {
                    recovery.Arm(new(recovery.Key, 0), Rows(recovery.State));
                }
                if (phase is PolicySessionPhase.Retiring or PolicySessionPhase.Retired)
                {
                    recovery.Retire(PolicySessionFault.Agent);
                    if (phase == PolicySessionPhase.Retired)
                    {
                        recovery.ConfirmRetirement(new(recovery.Key, (PolicyRetirementObservations) 575));
                    }
                }
                Assert.Equal(phase, journal.Read().Phase);
            }
            using PolicySessionJournalV2Unqualified reopened = Open(fixture);
            Ports ports = new(reopened, fixture.Path);
            PolicySessionCoordinatorV2Unqualified owner = new(reopened, fixture.Boot, 2, ports);
            Assert.Throws<InvalidOperationException>(owner.StartBindUploadReleaseUnqualified);
            Assert.Empty(ports.Trace);
            Assert.Throws<InvalidOperationException>(() => owner.ReserveUnqualified(Guid.NewGuid(), JournalFixture.Identity(4), 0, 10));
            Assert.Empty(ports.Trace);
        }
    }

    [Fact]
    public void FirstOperationErrorSurvivesFailedRetirementCommit()
    {
        using JournalFixture fixture = new();
        using (PolicySessionJournalV2Unqualified initial = Initialize(fixture))
        {
        }
        bool failStorage = false;
        using PolicySessionJournalV2Unqualified journal = Open(fixture, point =>
        {
            if (failStorage && point == PolicyJournalBarrier.Write)
            {
                throw new IOException("Retirement storage failure.");
            }
        });
        Ports ports = new(journal, fixture.Path);
        PolicySessionCoordinatorV2Unqualified owner = new(journal, fixture.Boot, 2, ports);
        ports.OnEffect = effect =>
        {
            if (effect == "start")
            {
                failStorage = true;
                throw new IOException("First start failure.");
            }
        };
        owner.ReserveUnqualified(Guid.NewGuid(), JournalFixture.Identity(1), 0, 10);
        IOException error = Assert.Throws<IOException>(owner.StartBindUploadReleaseUnqualified);
        Assert.Equal("First start failure.", error.Message);
        Assert.Same(error, owner.FirstOperationFailure);
        Assert.Equal("Retirement storage failure.", owner.CleanupFailure?.Message);
        int effects = ports.Trace.Count;
        Assert.Throws<InvalidOperationException>(owner.FinishRetirementUnqualified);
        Assert.Equal(effects, ports.Trace.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SyncAndExactReadbackAmbiguityStopEveryLaterEffect(bool corruptReadback)
    {
        using JournalFixture fixture = new();
        using (PolicySessionJournalV2Unqualified initial = Initialize(fixture))
        {
        }
        int writes = 0;
        using PolicySessionJournalV2Unqualified journal = Open(fixture, point =>
        {
            if (point == PolicyJournalBarrier.Write)
            {
                writes++;
            }
            if (writes == 3 && point == PolicyJournalBarrier.DirectorySync)
            {
                if (corruptReadback)
                {
                    PolicySessionRecordV2Unqualified visible = PolicySessionRecordV2Unqualified.Decode(File.ReadAllBytes(fixture.Path));
                    File.WriteAllBytes(fixture.Path, (visible with
                    {
                        NativeMetadata = visible.NativeMetadata with
                        {
                            WorkerFlags = 0x802
                        }
                    }).Encode());
                }
                else
                {
                    throw new IOException("Directory sync ambiguity.");
                }
            }
        });
        Ports ports = new(journal, fixture.Path);
        PolicySessionCoordinatorV2Unqualified owner = new(journal, fixture.Boot, 2, ports);
        owner.ReserveUnqualified(Guid.NewGuid(), JournalFixture.Identity(1), 0, 10);
        IOException error = Assert.Throws<IOException>(owner.StartBindUploadReleaseUnqualified);
        Assert.Same(error, owner.FirstOperationFailure);
        Assert.Null(owner.CleanupFailure);
        Assert.Equal(3, writes);
        Assert.DoesNotContain("upload:Bound", ports.Trace);
        int effects = ports.Trace.Count;
        Assert.Throws<InvalidOperationException>(() => owner.RevokeUnqualified(PolicySessionFault.Cancelled));
        Assert.Equal(effects, ports.Trace.Count);
        Assert.Equal(3, writes);
    }

    [Theory]
    [InlineData("empty-boundary")]
    [InlineData("agent")]
    [InlineData("proof")]
    [InlineData("remove")]
    [InlineData("barrier")]
    [InlineData("close")]
    public void CaughtNestedReservationPoisonsEveryOtherInlineBoundary(string point)
    {
        using Rig rig = new();
        bool cleanup = point is "proof" or "remove" or "barrier" or "close";
        if (cleanup)
        {
            rig.Start();
            rig.Owner.RevokeUnqualified(PolicySessionFault.Cancelled);
        }
        rig.Ports.OnEffect = effect =>
        {
            if (effect == point)
            {
                Assert.Throws<InvalidOperationException>(rig.Reserve);
            }
        };
        if (cleanup)
        {
            Assert.Throws<InvalidOperationException>(rig.Owner.FinishRetirementUnqualified);
        }
        else
        {
            Assert.Throws<InvalidOperationException>(rig.Reserve);
        }
        Assert.False(rig.Owner.DrainedUnqualified);
        int effects = rig.Ports.Trace.Count;
        Assert.Throws<InvalidOperationException>(rig.Owner.FinishRetirementUnqualified);
        Assert.Equal(effects, rig.Ports.Trace.Count);
        Assert.Equal(point + (point is "empty-boundary" or "agent" ? "" : point == "close" ? ":Empty" : point == "proof" ? ":Retiring" : ":Retired"), rig.Ports.Trace[^1]);
    }

    [Fact]
    public void FailedConstructionConsumesRegistryAndReserveOverflowHasNoPorts()
    {
        using JournalFixture fixture = new();
        using PolicySessionJournalV2Unqualified journal = Initialize(fixture);
        Ports ports = new(journal, fixture.Path);
        Assert.Throws<InvalidOperationException>(() => new PolicySessionCoordinatorV2Unqualified(journal, Guid.NewGuid(), 2, ports));
        Assert.Throws<InvalidOperationException>(() => new PolicySessionCoordinatorV2Unqualified(journal, fixture.Boot, 2, ports));
        Assert.Empty(ports.Trace);
        using Rig rig = new();
        Assert.Throws<OverflowException>(() => rig.Owner.ReserveUnqualified(Guid.NewGuid(), JournalFixture.Identity(1), long.MaxValue, 1));
        Assert.Empty(rig.Ports.Trace);
        Assert.Equal(PolicySessionPhase.Empty, rig.Journal.Read().Phase);
    }

    private sealed class Rig : IDisposable
    {
        internal readonly JournalFixture Fixture = new();
        internal readonly PolicySessionJournalV2Unqualified Journal;
        internal readonly Ports Ports;
        internal readonly PolicySessionCoordinatorV2Unqualified Owner;

        internal Rig()
        {
            Journal = Initialize(Fixture);
            Ports = new(Journal, Fixture.Path);
            Owner = new(Journal, Fixture.Boot, 2, Ports);
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

    private sealed class Ports(PolicySessionJournalV2Unqualified journal, string path) : PolicySessionCoordinatorPortsV2Unqualified
    {
        internal readonly List<string> Trace = [];
        internal long Clock;
        internal bool Reverse;
        internal bool MutateAfterSnapshot;
        internal bool DriftAgent;
        internal bool DenyProof;
        internal int StoreDefect;
        internal int Reads;
        internal bool LoseRemovalAck;
        internal bool ChangedHolder;
        internal bool ChangedConfiguration;
        internal bool StaleProof;
        internal int Defect = -1;
        internal string? Fail;
        internal Action<string>? OnEffect;
        private readonly Guid holder = Guid.NewGuid();
        private readonly List<EscrowEntryV2Unqualified> entries = [];

        internal override long BoottimeUnqualified()
        {
            return Clock;
        }

        private void Effect(string name, PolicySessionRecordV2Unqualified? state = null)
        {
            if (state != null)
            {
                Assert.Equal(state, journal.Read());
                Assert.Equal(state.Encode(), File.ReadAllBytes(path));
            }
            Trace.Add(state == null ? name : $"{name}:{state.Phase}");
            OnEffect?.Invoke(name);
            if (Fail == name)
            {
                throw new IOException("Unknown unqualified effect acknowledgement.");
            }
        }

        internal override void ObserveEmptyOwnerBoundaryUnqualified(PolicySessionRecordV2Unqualified state)
        {
            Assert.Equal(state, journal.Read());
            Assert.Equal(state.Encode(), File.ReadAllBytes(path));
            Effect("empty-boundary");
        }

        internal override AcquiredAgentV2Unqualified AcquireOriginalAgentBeforeReserveUnqualified(PolicySessionIdentity requested)
        {
            Effect("agent");
            return new(requested, new(1, 2, 1, 1, 0x802, 0));
        }

        internal override void StartOriginalUnqualified(PolicySessionRecordV2Unqualified state)
        {
            Assert.True(state.AttemptConsumed);
            Assert.Equal(PolicySessionPhase.StartAttempted, state.Phase);
            Effect("start", state);
        }

        internal override BoundOriginalsV2Unqualified AcquireOriginalWorkerUnqualified(PolicySessionRecordV2Unqualified state)
        {
            Effect("bind", state);
            PolicySessionIdentity worker = JournalFixture.Identity(2);
            PolicySessionNativeMetadataV2Unqualified metadata = state.NativeMetadata with
            {
                Presence = 3,
                WorkerFlags = 2
            };
            return new(worker, new(worker.PidfsDevice, worker.PidfsInode, 2),
                new(state.Agent.PidfsDevice, state.Agent.PidfsInode, DriftAgent ? 2U : metadata.AgentFlags),
                DriftAgent ? metadata with
                {
                    AgentFlags = 2
                } : metadata);
        }

        internal override void UploadOnceUnqualified(PolicySessionRecordV2Unqualified state, PolicySessionExpectedNativePairV2Unqualified expected)
        {
            Effect("upload", state);
            PolicyEscrowFlags flags = PolicyEscrowFlags.OriginalNonThreadPidfd | PolicyEscrowFlags.CloseOnExec | PolicyEscrowFlags.PollingDisabled;
            entries.Add(new($"cm-agent-{state.Epoch:N}", state.Agent, PolicyEscrowRole.Agent, flags, expected.Agent));
            entries.Add(new($"cm-worker-{state.Epoch:N}", state.Worker, PolicyEscrowRole.Worker, flags, expected.Worker));
        }

        internal override StoreSnapshotV2Unqualified ReadStoreUnqualified(PolicySessionRecordV2Unqualified state)
        {
            Effect("read", state);
            EscrowEntryV2Unqualified[] rows = entries.ToArray();
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
            Reads++;
            if (StoreDefect == 1 && rows.Length > 0)
            {
                rows[0] = rows[0] with
                {
                    Native = rows[0].Native with
                    {
                        Flags = rows[0].Native.Flags == 2 ? 0x802U : 2U
                    }
                };
            }
            if (StoreDefect == 2 && state.Phase == PolicySessionPhase.Retired && rows.Length == 0)
            {
                rows = [new("unexpected", state.Agent, PolicyEscrowRole.Agent, (PolicyEscrowFlags) 7, default)];
            }
            StoreSnapshotV2Unqualified snapshot = new(EvidenceKeyV2Unqualified.From(state), Defect == 6 ? 3 : rows.Length,
                rows, ChangedHolder && Reads > 1 ? Guid.NewGuid() : holder, ChangedConfiguration && Reads > 1 ? 2UL : 1UL,
                StoreDefect != 3, StoreDefect != 4);
            if (MutateAfterSnapshot)
            {
                Array.Fill(rows, new EscrowEntryV2Unqualified("bad", default, PolicyEscrowRole.Agent, 0, default));
            }
            return snapshot;
        }

        internal override void PublishOnceUnqualified(PolicySessionRecordV2Unqualified state)
        {
            Assert.True(state.EverArmed);
            Effect("publish", state);
        }

        internal override RetirementEvidenceV2Unqualified ObserveRetirementUnqualified(PolicySessionRecordV2Unqualified state)
        {
            Effect("proof", state);
            return Evidence(state) with
            {
                Key = EvidenceKeyV2Unqualified.From(state) with
                {
                    Revision = StaleProof ? state.Revision - 1 : state.Revision
                },
                Observations = DenyProof ? PolicyRetirementObservations.GateRevoked : (PolicyRetirementObservations) 1023
            };
        }

        internal override void RemoveExactNameUnqualified(PolicySessionRecordV2Unqualified state, EscrowEntryV2Unqualified observed)
        {
            Assert.Equal(PolicySessionPhase.Retired, state.Phase);
            Assert.NotEqual(0UL, state.ProofRevision);
            Effect("remove", state);
            Assert.Equal(1, entries.RemoveAll(entry => entry.Name == observed.Name));
            if (LoseRemovalAck)
            {
                LoseRemovalAck = false;
                throw new IOException("Lost removal acknowledgement after apply.");
            }
        }

        internal override void RemovalBarrierUnqualified(PolicySessionRecordV2Unqualified state)
        {
            Effect("barrier", state);
        }

        internal override void CloseLocalWitnessesOnceUnqualified()
        {
            Effect("close", journal.Read());
        }

        private static RetirementEvidenceV2Unqualified Evidence(PolicySessionRecordV2Unqualified state)
        {
            return new(EvidenceKeyV2Unqualified.From(state), PolicyRetirementObservations.None);
        }
    }
}
