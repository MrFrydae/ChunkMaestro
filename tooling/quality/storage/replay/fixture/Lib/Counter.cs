using System.Collections.Generic;

namespace Fixture;

public sealed class Counter
{
    public int Value
    {
        get;
        private set;
    }

    public void Increment()
    {
        Value = Value + 1;
    }

    public int Number()
    {
        return Value + 1;
    }

    public bool Output(out int number)
    {
        number = Value + 1;
        return true;
    }

    public IEnumerable<int> Sequence()
    {
        yield return Value + 1;
    }
}
