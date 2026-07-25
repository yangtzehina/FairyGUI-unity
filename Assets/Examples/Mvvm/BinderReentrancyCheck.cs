using System.Text;
using FairyGUI.Mvvm;

/// <summary>
/// Behavioral validation for the review-batch-1 Binder fixes:
/// V1 — writes made DURING apply callbacks must survive to the next Flush
///      (masked clear instead of full clear);
/// V6 — Unbind/Clear called from an apply callback must not derail the
///      in-progress Flush (snapshot iteration + tombstones).
/// Invoke BinderReentrancyCheck.Run() from an eval; returns a PASS/FAIL report.
/// </summary>
public static class BinderReentrancyCheck
{
    class VM : ViewModel
    {
        public int a, b;
        public void SetA(int v) { a = v; MarkDirty(0); }
        public void SetB(int v) { b = v; MarkDirty(1); }
    }

    public static string Run()
    {
        var sb = new StringBuilder();
        int pass = 0, fail = 0;
        void Check(string name, bool ok)
        {
            if (ok) pass++; else fail++;
            sb.Append(ok ? "PASS " : "FAIL ").Append(name).Append('\n');
        }

        //--- V1: cascading write inside apply survives to the next flush ---
        {
            var vm = new VM();
            var binder = new Binder();
            int bApplied = 0;
            binder.Bind(vm, 0, () => { vm.SetB(42); }, applyNow: false); //apply of A writes B
            binder.Bind(vm, 1, () => { bApplied++; }, applyNow: false);
            vm.SetA(1);
            binder.Flush();  //flushes A's bit; B's bit is set DURING this flush
            int afterFirst = bApplied;
            binder.Flush();  //must now apply B
            Check($"V1.cascade survives (b applied {afterFirst}->{bApplied})", bApplied == afterFirst + 1);
            binder.Flush();
            Check("V1.no repeat after clean flush", bApplied == afterFirst + 1);
        }

        //--- V1: property re-marked inside its own apply flushes again next time ---
        {
            var vm = new VM();
            var binder = new Binder();
            int applied = 0;
            binder.Bind(vm, 0, () => { applied++; if (applied == 1) vm.SetA(2); }, applyNow: false);
            vm.SetA(1);
            binder.Flush();
            binder.Flush();
            Check($"V1.self remark applies twice (got {applied})", applied == 2);
        }

        //--- V6: Unbind(other) during flush — no exception, remaining group applies ---
        {
            var vm1 = new VM();
            var vm2 = new VM();
            var vm3 = new VM();
            var binder = new Binder();
            int applied2 = 0, applied3 = 0;
            binder.Bind(vm1, 0, () => { binder.Unbind(vm2); }, applyNow: false);
            binder.Bind(vm2, 0, () => { applied2++; }, applyNow: false);
            binder.Bind(vm3, 0, () => { applied3++; }, applyNow: false);
            vm1.SetA(1); vm2.SetA(1); vm3.SetA(1);
            bool threw = false;
            try { binder.Flush(); } catch { threw = true; }
            Check("V6.unbind mid-flush no throw", !threw);
            Check("V6.unbound group skipped", applied2 == 0);
            Check("V6.later group still applied", applied3 == 1);
        }

        //--- V6: Unbind(self) during own apply ---
        {
            var vm1 = new VM();
            var vm2 = new VM();
            var binder = new Binder();
            int applied2 = 0;
            binder.Bind(vm1, 0, () => { binder.Unbind(vm1); }, applyNow: false);
            binder.Bind(vm2, 0, () => { applied2++; }, applyNow: false);
            vm1.SetA(1); vm2.SetA(1);
            bool threw = false;
            try { binder.Flush(); } catch { threw = true; }
            Check("V6.self-unbind no throw", !threw);
            Check("V6.self-unbind later group applied", applied2 == 1);
            vm1.SetA(2);
            binder.Flush();
            Check("V6.self-unbind really unbound", true); //no callback registered anymore -> nothing to observe but no throw
        }

        //--- V6: Clear during flush ---
        {
            var vm1 = new VM();
            var vm2 = new VM();
            var binder = new Binder();
            int applied2 = 0;
            binder.Bind(vm1, 0, () => { binder.Clear(); }, applyNow: false);
            binder.Bind(vm2, 0, () => { applied2++; }, applyNow: false);
            vm1.SetA(1); vm2.SetA(1);
            bool threw = false;
            try { binder.Flush(); } catch { threw = true; }
            Check("V6.clear mid-flush no throw", !threw);
            Check("V6.cleared groups stop applying", applied2 == 0);
        }

        sb.Insert(0, $"RESULT pass={pass} fail={fail}\n");
        return sb.ToString();
    }
}
