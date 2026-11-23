using ChunkMaestro.Persistence;
using System.Diagnostics;
using Xunit;
using Admission = ChunkMaestro.Persistence.OwnerAuthorizationRequest.Admission;

namespace ChunkMaestro.PersistenceTests;

public sealed class OwnerAuthorizationOrderingTests
{
    private static OwnerRequestScope Scope => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1);
    private static long Deadline => Stopwatch.GetTimestamp() + Stopwatch.Frequency * 60;

    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    [InlineData(3, false)]
    [InlineData(3, true)]
    public async Task BarrierOrdersBothAdmissionAndRevocationWinners(int value, bool admissionFirst)
    {
        OwnerEffect target = (OwnerEffect) value;
        OwnerRequestScope scope = Scope;
        OwnerAuthorizationRequest request = new(scope, Deadline);
        Assert.True(request.Verified(scope));
        for (int index = 0; index < value; index++)
        {
            AdmitAndFinish(request, (OwnerEffect) index, scope);
        }
        using Barrier boundary = new(2);
        int effects = 0;
        Task worker = Task.Run(() =>
        {
            if (admissionFirst)
            {
                Admission admission = Admit(request, target, scope);
                Meet(boundary);
                Meet(boundary);
                Interlocked.Increment(ref effects);
                Assert.True(request.Finish(admission, OwnerEffectCompletion.Completed));
            }
            else
            {
                Meet(boundary);
                Meet(boundary);
                Assert.Null(request.Admit(target, scope));
            }
        });
        Meet(boundary);
        request.Revoke();
        Meet(boundary);
        await worker;
        Assert.Equal(admissionFirst ? 1 : 0, effects);
        Assert.Null(request.Admit((OwnerEffect) (value + 1), scope));
        Assert.False(request.Verified(scope));
    }

    [Fact]
    public async Task CallerCheckNegativeControlWouldPublishAfterRevokeWhileWorkerAdmissionDenies()
    {
        OwnerRequestScope scope = Scope;
        OwnerAuthorizationRequest request = new(scope, Deadline);
        Assert.True(request.Verified(scope));
        bool callerChecked = true;
        using Barrier queue = new(2);
        int unsafeEffects = 0;
        int admittedEffects = 0;
        Task worker = Task.Run(() =>
        {
            Meet(queue);
            Meet(queue);
            if (callerChecked)
            {
                unsafeEffects++;
            }
            if (request.Admit(OwnerEffect.Begin, scope) is not null)
            {
                admittedEffects++;
            }
        });
        Meet(queue);
        request.Revoke();
        Meet(queue);
        await worker;
        Assert.Equal(1, unsafeEffects);
        Assert.Equal(0, admittedEffects);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void EveryStageIsSingleUseAndCannotAdvanceAfterRevocation(int value)
    {
        OwnerEffect target = (OwnerEffect) value;
        OwnerRequestScope scope = Scope;
        OwnerAuthorizationRequest request = new(scope, Deadline);
        Assert.True(request.Verified(scope));
        for (int index = 0; index <= (int) target; index++)
        {
            OwnerEffect effect = (OwnerEffect) index;
            Admission admission = Admit(request, effect, scope);
            if (effect == target)
            {
                request.Revoke();
            }
            Assert.True(request.Finish(admission, OwnerEffectCompletion.Completed));
        }
        Assert.Null(request.Admit(target, scope));
        Assert.Null(request.Admit((OwnerEffect) ((int) target + 1), scope));
        Assert.False(request.Finish(new Admission(target), OwnerEffectCompletion.Completed));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(99)]
    public void FailedOrUnknownFinishCannotAuthorizeNextEffect(int value)
    {
        OwnerEffectCompletion completion = (OwnerEffectCompletion) value;
        OwnerRequestScope scope = Scope;
        OwnerAuthorizationRequest request = new(scope, Deadline);
        Assert.True(request.Verified(scope));
        Admission admission = Admit(request, OwnerEffect.Begin, scope);
        Assert.False(request.Finish(admission, completion));
        Assert.Null(request.Admit(OwnerEffect.Create, scope));
    }

    [Theory]
    [InlineData("fleet")]
    [InlineData("controller")]
    [InlineData("attempt")]
    [InlineData("credential")]
    [InlineData("revision")]
    [InlineData("invalid")]
    public void ChangedOrInvalidObservationNeverAdmits(string change)
    {
        OwnerRequestScope scope = Scope;
        OwnerRequestScope wrong = change switch
        {
            "fleet" => scope with
            {
                Fleet = Guid.NewGuid()
            },
            "controller" => scope with
            {
                Controller = Guid.NewGuid()
            },
            "attempt" => scope with
            {
                Attempt = Guid.NewGuid()
            },
            "credential" => scope with
            {
                Credential = Guid.NewGuid()
            },
            "revision" => scope with
            {
                Revision = 2
            },
            _ => default
        };
        OwnerAuthorizationRequest request = new(scope, Deadline);
        Assert.True(request.Verified(scope));
        Assert.Null(request.Admit(OwnerEffect.Begin, wrong));
        Assert.Null(request.Admit(OwnerEffect.Begin, scope));
    }

    [Fact]
    public void UnverifiedDuplicateVerifiedAndExpiredRequestsNeverAdmit()
    {
        OwnerRequestScope scope = Scope;
        OwnerAuthorizationRequest unverified = new(scope, Deadline);
        Assert.Null(unverified.Admit(OwnerEffect.Begin, scope));
        Assert.False(unverified.Verified(scope));
        OwnerAuthorizationRequest duplicate = new(scope, Deadline);
        Assert.True(duplicate.Verified(scope));
        Assert.False(duplicate.Verified(scope));
        Assert.Null(duplicate.Admit(OwnerEffect.Begin, scope));
        OwnerAuthorizationRequest late = new(scope, Deadline);
        late.Revoke();
        Assert.False(late.Verified(scope));
        Assert.Null(late.Admit(OwnerEffect.Begin, scope));
        OwnerAuthorizationRequest expired = new(scope, long.MinValue);
        Assert.False(expired.Verified(scope));
        Assert.Null(expired.Admit(OwnerEffect.Begin, scope));
    }

    [Fact]
    public void DuplicateAdmissionAndOutOfOrderCompletionDoNotAdvance()
    {
        OwnerRequestScope scope = Scope;
        OwnerAuthorizationRequest request = new(scope, Deadline);
        Assert.True(request.Verified(scope));
        Admission admission = Admit(request, OwnerEffect.Begin, scope);
        Assert.Null(request.Admit(OwnerEffect.Begin, scope));
        Assert.False(request.Finish(new Admission(OwnerEffect.Create), OwnerEffectCompletion.Completed));
        Assert.True(request.Finish(admission, OwnerEffectCompletion.Completed));
        Assert.Null(request.Admit(OwnerEffect.Create, scope));
    }

    [Fact]
    public async Task ConcurrentWorkersCannotAdmitTheSameEffectTwice()
    {
        OwnerRequestScope scope = Scope;
        OwnerAuthorizationRequest request = new(scope, Deadline);
        Assert.True(request.Verified(scope));
        using Barrier start = new(3);
        int admitted = 0;
        Admission? winner = null;
        Task Worker() => Task.Run(() =>
        {
            Meet(start);
            Admission? admission = request.Admit(OwnerEffect.Begin, scope);
            if (admission is not null)
            {
                winner = admission;
                Interlocked.Increment(ref admitted);
            }
        });
        Task first = Worker();
        Task second = Worker();
        Meet(start);
        await Task.WhenAll(first, second);
        Assert.Equal(1, admitted);
        Assert.True(request.Finish(Assert.IsType<Admission>(winner), OwnerEffectCompletion.Completed));
        Assert.Null(request.Admit(OwnerEffect.Create, scope));
    }

    [Fact]
    public void CompletionOfTheFixedPlanCannotReplayVerificationOrAnyEffect()
    {
        OwnerRequestScope scope = Scope;
        OwnerAuthorizationRequest request = new(scope, Deadline);
        Assert.True(request.Verified(scope));
        foreach (OwnerEffect effect in Enum.GetValues<OwnerEffect>())
        {
            AdmitAndFinish(request, effect, scope);
        }
        Assert.False(request.Verified(scope));
        Assert.Null(request.Admit(OwnerEffect.Begin, scope));
    }

    [Fact]
    public async Task DeadlineIsCheckedInQueuedWorkerAfterVerification()
    {
        OwnerRequestScope scope = Scope;
        long deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency;
        OwnerAuthorizationRequest request = new(scope, deadline);
        Assert.True(request.Verified(scope));
        using Barrier queue = new(2);
        Task worker = Task.Run(() =>
        {
            Meet(queue);
            Meet(queue);
            Assert.Null(request.Admit(OwnerEffect.Begin, scope));
        });
        Meet(queue);
        // Advance the real monotonic deadline, without sleeping or assuming worker scheduling.
        while (Stopwatch.GetTimestamp() < deadline)
        {
            Thread.SpinWait(64);
        }
        Meet(queue);
        await worker;
        Assert.False(request.Verified(scope));
    }

    [Fact]
    public void FabricatedAdmissionCannotCompleteTheWinningWorkerEffect()
    {
        OwnerRequestScope scope = Scope;
        OwnerAuthorizationRequest request = new(scope, Deadline);
        Assert.True(request.Verified(scope));
        Admission admission = Admit(request, OwnerEffect.Begin, scope);
        Assert.False(request.Finish(new Admission(OwnerEffect.Begin), OwnerEffectCompletion.Completed));
        Assert.True(request.Finish(admission, OwnerEffectCompletion.Completed));
        Assert.Null(request.Admit(OwnerEffect.Create, scope));
    }

    [Fact]
    public void MatchingScopeAndStageCannotTransferAdmissionAcrossRequests()
    {
        OwnerRequestScope scope = Scope;
        OwnerAuthorizationRequest first = new(scope, Deadline);
        OwnerAuthorizationRequest second = new(scope, Deadline);
        Assert.True(first.Verified(scope));
        Assert.True(second.Verified(scope));
        Admission firstAdmission = Admit(first, OwnerEffect.Begin, scope);
        Admission secondAdmission = Admit(second, OwnerEffect.Begin, scope);
        Assert.False(second.Finish(firstAdmission, OwnerEffectCompletion.Completed));
        Assert.False(first.Finish(secondAdmission, OwnerEffectCompletion.Completed));
        Assert.True(first.Finish(firstAdmission, OwnerEffectCompletion.Completed));
        Assert.True(second.Finish(secondAdmission, OwnerEffectCompletion.Completed));
        Assert.Null(first.Admit(OwnerEffect.Create, scope));
        Assert.Null(second.Admit(OwnerEffect.Create, scope));
    }

    [Fact]
    public void CompletedAdmissionCannotFinishALaterStage()
    {
        OwnerRequestScope scope = Scope;
        OwnerAuthorizationRequest request = new(scope, Deadline);
        Assert.True(request.Verified(scope));
        Admission first = Admit(request, OwnerEffect.Begin, scope);
        Assert.True(request.Finish(first, OwnerEffectCompletion.Completed));
        Admission second = Admit(request, OwnerEffect.Create, scope);
        Assert.False(request.Finish(first, OwnerEffectCompletion.Completed));
        Assert.True(request.Finish(second, OwnerEffectCompletion.Completed));
        Assert.Null(request.Admit(OwnerEffect.Use, scope));
    }

    private static Admission Admit(OwnerAuthorizationRequest request, OwnerEffect effect, OwnerRequestScope scope)
    {
        return Assert.IsType<Admission>(request.Admit(effect, scope));
    }

    private static void AdmitAndFinish(OwnerAuthorizationRequest request, OwnerEffect effect, OwnerRequestScope scope)
    {
        Assert.True(request.Finish(Admit(request, effect, scope), OwnerEffectCompletion.Completed));
    }

    private static void Meet(Barrier barrier)
    {
        Assert.True(barrier.SignalAndWait(TimeSpan.FromSeconds(10)), "Barrier timed out");
    }
}
