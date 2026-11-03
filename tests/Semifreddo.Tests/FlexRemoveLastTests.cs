using Semifreddo.Testing;
using Xunit;

namespace Semifreddo.Tests;

/// <summary>RemoveLast refills an empty tail from the tree's last leaf (one cut of the right spine), so most pops are
/// header copies: every shape of tree, persistent and transient, against a list.</summary>
public class FlexRemoveLastTests
{
    public static TheoryData<string> Shapes => ["appended", "concatenated", "inserted"];

    private static (ImmFlexVector<int> Vector, List<int> Model) Build(string shape, int n, int seed)
    {
        var rnd = new Random(seed);
        var model = new List<int>();
        var v = ImmFlexVector<int>.Empty;
        switch (shape)
        {
            case "appended":
                for (var i = 0; i < n; i++) { v = v.Add(i); model.Add(i); }
                break;
            case "concatenated":                  // relaxed nodes: pieces of every size, joined in random order
                while (model.Count < n)
                {
                    int len = rnd.Next(1, 70), start = model.Count;
                    var piece = ImmFlexVector.Create<int>(Enumerable.Range(start, len).ToArray());
                    v = v.AddRange(piece);
                    model.AddRange(Enumerable.Range(start, len));
                }
                break;
            default:                              // random inserts: B-tree style splits and rebalances
                for (var i = 0; i < n; i++)
                {
                    var at = rnd.Next(model.Count + 1);
                    v = v.Insert(at, i);
                    model.Insert(at, i);
                }
                break;
        }
        return (v, model);
    }

    [Theory]
    [MemberData(nameof(Shapes))]
    public void Persistent_DrainsLikeAList_AndLeavesEveryEarlierVersionAlone(string shape)
    {
        var (v, model) = Build(shape, 5_000, 1);
        var history = new List<(ImmFlexVector<int> Version, int Count, int Last)>();
        while (!v.IsEmpty)
        {
            if (model.Count % 97 == 0) history.Add((v, model.Count, model[^1]));
            v = v.RemoveLast();
            model.RemoveAt(model.Count - 1);
            Assert.Equal(model.Count, v.Count);
            if (model.Count > 0) Assert.Equal(model[^1], v.Last);
            if (model.Count % 211 == 0)
            {
                Check.FlexVector(v);
                Assert.Equal(model, v.ToArray());
            }
        }
        foreach (var (version, count, last) in history)
        {
            Assert.Equal(count, version.Count);
            Assert.Equal(last, version.Last);
        }
    }

    [Theory]
    [MemberData(nameof(Shapes))]
    public void Transient_DrainsLikeAList(string shape)
    {
        var (v, model) = Build(shape, 3_000, 2);
        var built = v.Count;
        var t = v.ToTransient();
        while (t.Count > 0)
        {
            t.RemoveLast();
            model.RemoveAt(model.Count - 1);
            if (model.Count % 127 == 0)
            {
                var frozen = t.ToPersistent();
                Check.FlexVector(frozen);
                Assert.Equal(model, frozen.ToArray());
            }
        }
        Assert.Equal(built, v.Count);                         // the source version is untouched
    }

    [Fact]
    public void PopsAndAppends_AcrossTheTailBoundary_MatchAList()
    {
        var rnd = new Random(3);
        var (v, model) = Build("concatenated", 2_000, 3);
        for (var step = 0; step < 20_000; step++)
        {
            if (model.Count > 0 && rnd.Next(3) != 0)
            {
                v = v.RemoveLast();
                model.RemoveAt(model.Count - 1);
            }
            else
            {
                v = v.Add(step);
                model.Add(step);
            }
            if (step % 499 == 0)
            {
                Check.FlexVector(v);
                Assert.Equal(model, v.ToArray());
            }
        }
        Assert.Equal(model, v.ToArray());
    }
}
