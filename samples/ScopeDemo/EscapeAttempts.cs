// Each case must FAIL to compile. Built one at a time with -p:DefineConstants=CASE_n by verify-escape.sh.
// ReSharper disable once RedundantUsingDirective
using Semifreddo;

static class EscapeAttempts
{
#if CASE_1 // store the scope in a class field
    class Holder { public ImmVector<int>.Scope S; }
#endif
#if CASE_2 // capture the scope in a nested lambda (e.g. to call later)
    static Action Leak(ImmVector<int> v) { Action? a = null; v.Mutate(s => { a = () => s.Add(1); }); return a!; }
#endif
#if CASE_3 // box it
    static object Leak(ImmVector<int> v) { object? o = null; v.Mutate(s => { o = s; }); return o!; }
#endif
#if CASE_4 // smuggle it out via a generic that does not allow ref structs
    static List<ImmVector<int>.Scope> Leak() => new();
#endif
#if CASE_5 // hold it across an await
    static async Task Leak(ImmVector<int> v) => v.Mutate(async s => { await Task.Yield(); s.Add(1); });
#endif
#if CASE_6 // return the element ref out of the batch via a ref local captured from outside
    static void Leak(ImmVector<int> v) { int dummy = 0; ref int r = ref dummy; v.Mutate(s => { r = ref s.ItemRef(0); }); }
#endif
}
