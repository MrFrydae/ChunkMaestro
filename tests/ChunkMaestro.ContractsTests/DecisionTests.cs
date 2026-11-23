using ChunkMaestro.Contracts;
using System.Text;
using Xunit;

namespace ChunkMaestro.ContractsTests;

public sealed class DecisionTests
{
    [Fact]
    public void EveryOutcomeReasonStatePresenceCombinationIsChecked()
    {
        HashSet<string> allowed = new(StringComparer.Ordinal);
        foreach (ServiceState state in new ServiceState[]
        {
            ServiceState.Active, ServiceState.Inactive, ServiceState.Failed, ServiceState.Transitional
        })
        {
            allowed.Add($"Observed/None/{state}/Present");
        }
        allowed.Add("Denied/PolicyDenied/Unknown/Unresolved");
        foreach (ManagerPresence presence in Enum.GetValues<ManagerPresence>())
        {
            allowed.Add($"Unknown/TargetDrift/Unknown/{presence}");
        }
        allowed.Add("Unknown/NotLoaded/Unknown/Present");
        allowed.Add("Unknown/UnsupportedState/Unknown/Present");
        allowed.Add("Unknown/ConcurrentChange/Unknown/Unresolved");
        foreach (InspectReason reason in new InspectReason[]
        {
            InspectReason.DependencyUnavailable, InspectReason.PermissionDenied, InspectReason.Timeout, InspectReason.Busy
        })
        {
            allowed.Add($"Unavailable/{reason}/Unknown/Unresolved");
        }
        foreach (InspectOutcome outcome in Enum.GetValues<InspectOutcome>().Append((InspectOutcome) 999))
        {
            foreach (InspectReason reason in Enum.GetValues<InspectReason>().Append((InspectReason) 999))
            {
                foreach (ServiceState state in Enum.GetValues<ServiceState>().Append((ServiceState) 999))
                {
                    foreach (ManagerPresence presence in Enum.GetValues<ManagerPresence>().Append((ManagerPresence) 999))
                    {
                        bool valid = allowed.Contains($"{outcome}/{reason}/{state}/{presence}");
                        Exception? error = Record.Exception(() => new InspectResponse(CodecTests.Request, CodecTests.Epoch, outcome, reason, DateTimeOffset.UnixEpoch, 0, state, presence));
                        Assert.Equal(valid, error is null);
                        string wire = Encoding.UTF8.GetString(InspectCodec.EncodeResponse(CodecTests.Response)[4..])
                            .Replace("Observed", outcome.ToString(), StringComparison.Ordinal)
                            .Replace("None", reason.ToString(), StringComparison.Ordinal)
                            .Replace("Active", state.ToString(), StringComparison.Ordinal)
                            .Replace("Present", presence.ToString(), StringComparison.Ordinal);
                        Exception? wireError = Record.Exception(() => InspectCodec.DecodeResponse(Encoding.UTF8.GetBytes(wire), CodecTests.Request));
                        Assert.Equal(valid, wireError is null);
                    }
                }
            }
        }
    }

    [Fact]
    public void SyntheticPolicyDenialPrecedesDriftAndNeverAuthenticatesKernelPeer()
    {
        PolicyFacts policy = new(1001, 1002, 1003, CodecTests.InstanceId, CodecTests.Revision, true, true, true);
        Assert.Null(InspectionDecisions.Refusal(1001, CodecTests.Request, policy));
        foreach (uint peer in new uint[]
        {
            0, 1002, 1003, 9999
        })
        {
            Assert.Equal(InspectReason.PolicyDenied, InspectionDecisions.Refusal(peer, CodecTests.Request, policy with
            {
                TargetBound = false
            }));
        }
        foreach (PolicyFacts bad in new PolicyFacts[]
        {
            policy with
        {
            AgentUid = 0
        }, policy with
        {
            GameUid = 0
        }, policy with
        {
            InspectorUid = 0
        },
            policy with
        {
            AgentUid = 1003
        }, policy with
        {
            InspectorUid = 1003
        }, policy with
        {
            InspectorUid = 1001
        },
            policy with
        {
            IdentityQualified = false
        }, policy with
        {
            InstanceId = CodecTests.Epoch
        }, policy with
        {
            Revision = "bad"
        }
        })
        {
            Assert.Equal(InspectReason.PolicyDenied, InspectionDecisions.Refusal(1001, CodecTests.Request, bad));
        }
        Assert.Equal(InspectReason.TargetDrift, InspectionDecisions.Refusal(1001, CodecTests.Request, policy with
        {
            TargetBound = false
        }));
        Assert.Equal(InspectReason.TargetDrift, InspectionDecisions.Refusal(1001, CodecTests.Request, policy with
        {
            InputsStable = false
        }));
        Assert.Equal(ServiceState.Unknown, InspectionDecisions.MapActiveState("not-loaded"));
        Assert.Equal(ServiceState.Unknown, InspectionDecisions.MapActiveState("new-future-value"));
        Assert.Equal(ServiceState.Inactive, InspectionDecisions.MapActiveState("inactive"));
        Assert.Equal(ServiceState.Active, InspectionDecisions.MapActiveState("active"));
        Assert.Equal(ServiceState.Failed, InspectionDecisions.MapActiveState("failed"));
        foreach (string state in new string[]
        {
            "activating", "deactivating", "reloading", "refreshing"
        })
        {
            Assert.Equal(ServiceState.Transitional, InspectionDecisions.MapActiveState(state));
        }
    }

    [Fact]
    public void InvalidAndOverflowingMonotonicIntervalsCannotBecomeFresh()
    {
        foreach (long start in new long[]
        {
            long.MinValue, -1
        })
        {
            foreach (long receipt in new long[]
            {
                0, long.MaxValue
            })
            {
                ObservationWindow invalid = new(CodecTests.Request, start);
                Assert.False(invalid.Accept(CodecTests.Response, receipt, null));
                Assert.False(invalid.IsFresh(receipt));
            }
        }
        ObservationWindow tooOld = new(CodecTests.Request, 0);
        Assert.False(tooOld.Accept(CodecTests.Response, long.MaxValue, null));
        ObservationWindow nearLimit = new(CodecTests.Request, long.MaxValue - 2);
        Assert.True(nearLimit.Accept(CodecTests.Response, long.MaxValue, null));
        Assert.True(nearLimit.IsFresh(long.MaxValue));
        Assert.False(nearLimit.IsFresh(long.MinValue));
    }

    [Fact]
    public void MonotonicAgeNormalCloseAndDetectableChangesAreDistinct()
    {
        ObservationWindow window = new(CodecTests.Request, 100);
        Assert.True(window.Accept(CodecTests.Response, 200, null));
        window.ConnectionClosed(true);
        Assert.True(window.IsFresh(15100));
        Assert.False(window.IsFresh(15101));
        Assert.False(window.IsFresh(199));
        window.Invalidate();
        Assert.False(window.IsFresh(201));
        ObservationWindow disconnected = new(CodecTests.Request, 100);
        Assert.True(disconnected.Accept(CodecTests.Response, 200, CodecTests.Epoch));
        disconnected.ConnectionClosed(false);
        Assert.False(disconnected.IsFresh(201));
        ObservationWindow duplicate = new(CodecTests.Request, 100);
        Assert.True(duplicate.Accept(CodecTests.Response, 200, null));
        Assert.False(duplicate.Accept(CodecTests.Response, 201, null));
        Assert.False(duplicate.IsFresh(201));
        ObservationWindow newEpoch = new(CodecTests.Request, 100);
        Assert.False(newEpoch.Accept(CodecTests.Response, 200, CodecTests.InstanceId));
        Assert.False(newEpoch.IsFresh(201));
        foreach (long receipt in new long[]
        {
            99, 3101
        })
        {
            ObservationWindow late = new(CodecTests.Request, 100);
            Assert.False(late.Accept(CodecTests.Response, receipt, null));
        }
        InspectRequest changed = new(CodecTests.RequestId, CodecTests.InstanceId, new string('b', 64));
        ObservationWindow revision = new(changed, 100);
        Assert.False(revision.Accept(CodecTests.Response, 200, null));
        ObservationWindow correlation = new(new InspectRequest(CodecTests.Epoch, CodecTests.InstanceId, CodecTests.Revision), 100);
        Assert.False(correlation.Accept(CodecTests.Response, 200, null));
        ObservationWindow target = new(new InspectRequest(CodecTests.RequestId, CodecTests.Epoch, CodecTests.Revision), 100);
        Assert.False(target.Accept(CodecTests.Response, 200, null));
        ObservationWindow failure = new(CodecTests.Request, 100);
        failure.ConnectionClosed(true);
        Assert.False(failure.Accept(CodecTests.Response, 200, null));
    }
}
