using System.Text.Json;
using KalTerm.Core.RateLimiting;

namespace KalTerm.Core.Tests.RateLimiting;

/// <summary>
/// Differential tests: the reference bucket (Python, floats) ran each scenario under a fake clock (ratelimit.json).
/// The port is exact; the reference carries float drift, and a wait here is rounded up to a whole millisecond
/// (the granularity of timers), so the clock may differ by a millisecond per acquire.
/// </summary>
public class TokenBucketVectorTests
{
    private const double SecondsPerMillisecond = 1e-3;

    public static TheoryData<string> ScenarioNames()
    {
        var data = new TheoryData<string>();
        foreach (var s in VectorFile.Load("ratelimit.json").GetProperty("scenarios").EnumerateArray())
        {
            data.Add(s.GetProperty("name").GetString()!);
        }

        return data;
    }

    public static TheoryData<decimal, decimal, string> ConstructorCases()
    {
        var data = new TheoryData<decimal, decimal, string>();
        foreach (var c in VectorFile.Load("ratelimit.json").GetProperty("constructor").EnumerateArray())
        {
            data.Add(c.GetProperty("rate").GetDecimal(), c.GetProperty("capacity").GetDecimal(), c.GetProperty("expect").GetString()!);
        }

        return data;
    }

    public static TheoryData<decimal, string> AcquireCases()
    {
        var data = new TheoryData<decimal, string>();
        foreach (var c in VectorFile.Load("ratelimit.json").GetProperty("acquire").EnumerateArray())
        {
            data.Add(c.GetProperty("cost").GetDecimal(), c.GetProperty("expect").GetString()!);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(ScenarioNames))]
    public async Task ScenarioClockMatchesTheReference(string name)
    {
        var scenario = VectorFile.Load("ratelimit.json").GetProperty("scenarios").EnumerateArray()
            .Single(s => s.GetProperty("name").GetString() == name);
        var time = new AutoAdvanceTimeProvider();
        using var bucket = new TokenBucket(scenario.GetProperty("rate").GetDecimal(), scenario.GetProperty("capacity").GetDecimal(), time);

        var ops = scenario.GetProperty("ops").EnumerateArray().ToList();
        var expected = scenario.GetProperty("nowAfter").EnumerateArray().Select(e => double.Parse(e.GetString()!, CultureInfo.InvariantCulture)).ToList();
        var acquires = 0;
        for (var i = 0; i < ops.Count; i++)
        {
            var op = ops[i];
            switch (op[0].GetString())
            {
                case "acquire":
                    acquires++;
                    await bucket.AcquireAsync(op[1].GetDecimal(), TestContext.Current.CancellationToken);
                    break;
                case "idle":
                    time.Advance(TimeSpan.FromTicks((long)(op[1].GetDecimal() * TimeSpan.TicksPerSecond)));
                    break;
                case "concurrent":
                    var pending = op[1].EnumerateArray().Select(c => bucket.AcquireAsync(c.GetDecimal(), TestContext.Current.CancellationToken).AsTask()).ToArray();
                    acquires += pending.Length;
                    await Task.WhenAll(pending);
                    break;
                default:
                    throw new InvalidOperationException($"unknown operation in {name}");
            }

            var tolerance = (acquires * SecondsPerMillisecond) + 1e-9;
            Assert.True(
                Math.Abs(time.Elapsed.TotalSeconds - expected[i]) <= tolerance,
                $"{name} after op {i}: clock {time.Elapsed.TotalSeconds:R} s, reference {expected[i]:R} s");
        }
    }

    [Theory]
    [MemberData(nameof(ConstructorCases))]
    public void ConstructorValidationMatchesTheReference(decimal rate, decimal capacity, string expected)
    {
        if (expected == "refuse")
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new TokenBucket(rate, capacity));
        }
        else
        {
            new TokenBucket(rate, capacity).Dispose();
        }
    }

    [Theory]
    [MemberData(nameof(AcquireCases))]
    public async Task CostValidationMatchesTheReference(decimal cost, string expected)
    {
        using var bucket = new TokenBucket(100, 100, new AutoAdvanceTimeProvider());

        if (expected == "refuse")
        {
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await bucket.AcquireAsync(cost, TestContext.Current.CancellationToken));
        }
        else
        {
            await bucket.AcquireAsync(cost, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public void TheVectorFileHasTheExpectedBreadth()
    {
        var file = VectorFile.Load("ratelimit.json");
        Assert.True(file.GetProperty("scenarios").GetArrayLength() >= 45);
        Assert.Matches("^[0-9a-f]{40}$", file.GetProperty("referenceCommit").GetString());
        Assert.Equal(JsonValueKind.Array, file.GetProperty("constructor").ValueKind);
    }
}
