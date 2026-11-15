using System.Linq;
using Fixture;
using Xunit;

namespace FixtureTests;

public sealed class CounterTests
{
    [Fact]
    public void IncrementChangesValue()
    {
        Counter counter = new Counter();
        counter.Increment();
        Assert.Equal(1, counter.Value);
    }

    [Fact]
    public void NumberReturnsOne()
    {
        Counter counter = new Counter();
        Assert.Equal(1, counter.Number());
    }

    [Fact]
    public void OutputAssignsOne()
    {
        Counter counter = new Counter();
        Assert.True(counter.Output(out int number));
        Assert.Equal(1, number);
    }

    [Fact]
    public void SequenceYieldsOne()
    {
        Counter counter = new Counter();
        Assert.Equal(1, Assert.Single(counter.Sequence()));
    }
}
