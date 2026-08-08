#if UNITY_2020_1_OR_NEWER
using System.Text;
using FairyGUI;

/// <summary>
/// Event-layer semantics suite (10 checks) — the durable home of the 7
/// play-mode assertions that commit e96f994 (int-keyed bridges, list storage,
/// pooled snapshot dispatch) only recorded in its commit message. E1a taught
/// why that is not enough: a later edit regressed the null-callback guard and
/// only a full re-audit caught it, because nothing here would have gone red.
///
/// The contract under test is "the old multicast semantics, exactly":
/// adds/removes during a dispatch affect the NEXT dispatch only, capture runs
/// before target-and-bubble, nested dispatches never corrupt the outer
/// snapshot, and isDispatching is a counter (stays true in the outer frame
/// after a nested dispatch returns — upstream's bool misreported false).
///
/// Pure logic, no pixels. Invoke EventSemanticsSuite.Run() from a Play-mode
/// eval; returns a "RESULT pass=N fail=N" report.
/// </summary>
public static class EventSemanticsSuite
{
    const string kEvt = "evt_semantics_suite";

    public static string Run()
    {
        var sb = new StringBuilder();
        int pass = 0, fail = 0;
        void Check(string name, bool ok)
        {
            if (ok) pass++; else fail++;
            sb.Append(ok ? "PASS " : "FAIL ").Append(name).Append('\n');
        }

        //--- e1: double-Add of the same callback dedupes --------------------
        {
            var d = new EventDispatcher();
            int calls = 0;
            EventCallback1 cb = ctx => { calls++; };
            d.AddEventListener(kEvt, cb);
            d.AddEventListener(kEvt, cb);
            d.DispatchEvent(kEvt);
            Check($"e1.double Add dedupes (calls={calls})", calls == 1);
            d.RemoveEventListener(kEvt, cb);
        }

        //--- e2: remove DURING dispatch still runs this round ---------------
        {
            var d = new EventDispatcher();
            int bCalls = 0;
            EventCallback1 b = ctx => { bCalls++; };
            EventCallback1 a = ctx => { d.RemoveEventListener(kEvt, b); };
            d.AddEventListener(kEvt, a);
            d.AddEventListener(kEvt, b);
            d.DispatchEvent(kEvt);
            int afterFirst = bCalls;
            d.DispatchEvent(kEvt);
            Check($"e2.remove during dispatch: snapshot completes ({afterFirst}), next round skips ({bCalls})",
                afterFirst == 1 && bCalls == 1);
        }

        //--- e3: add DURING dispatch waits for the next round ---------------
        {
            var d = new EventDispatcher();
            int cCalls = 0;
            EventCallback1 c = ctx => { cCalls++; };
            bool added = false;
            EventCallback1 a = ctx =>
            {
                if (!added) { added = true; d.AddEventListener(kEvt, c); }
            };
            d.AddEventListener(kEvt, a);
            d.DispatchEvent(kEvt);
            int afterFirst = cCalls;
            d.DispatchEvent(kEvt);
            Check($"e3.add during dispatch: absent this round ({afterFirst}), present next ({cCalls})",
                afterFirst == 0 && cCalls == 1);
        }

        //--- e4: capture before target before bubble ------------------------
        {
            var parent = new GComponent();
            var child = new GGraph();
            parent.AddChild(child);
            var order = new StringBuilder();
            parent.AddCapture(kEvt, ctx => order.Append("C"));
            child.AddEventListener(kEvt, ctx => order.Append("T"));
            parent.AddEventListener(kEvt, ctx => order.Append("B"));
            child.BubbleEvent(kEvt, null);
            Check($"e4.capture -> target -> bubble ({order})", order.ToString() == "CTB");
            parent.Dispose();
        }

        //--- e5: nested dispatch of ANOTHER type completes both -------------
        {
            var d = new EventDispatcher();
            const string kInner = kEvt + "_inner";
            var order = new StringBuilder();
            d.AddEventListener(kInner, ctx => order.Append("I"));
            d.AddEventListener(kEvt, ctx => { order.Append("A"); d.DispatchEvent(kInner); });
            d.AddEventListener(kEvt, ctx => order.Append("Z"));
            d.DispatchEvent(kEvt);
            Check($"e5.nested other-type dispatch keeps the outer snapshot ({order})",
                order.ToString() == "AIZ");
        }

        //--- e6: nested dispatch of the SAME type (pooled snapshots) --------
        {
            var d = new EventDispatcher();
            int aCalls = 0, zCalls = 0, depth = 0;
            d.AddEventListener(kEvt, ctx =>
            {
                aCalls++;
                if (depth == 0) { depth++; d.DispatchEvent(kEvt); }
            });
            d.AddEventListener(kEvt, ctx => { zCalls++; });
            d.DispatchEvent(kEvt);
            Check($"e6.nested same-type dispatch runs each listener per round (a={aCalls} z={zCalls})",
                aCalls == 2 && zCalls == 2);
        }

        //--- e7: isDispatching is a counter, not a bool ---------------------
        {
            var d = new EventDispatcher();
            const string kInner = kEvt + "_inner";
            bool outerStillDispatching = false;
            d.AddEventListener(kInner, ctx => { });
            d.AddEventListener(kEvt, ctx =>
            {
                d.DispatchEvent(kInner);
                //upstream's bool was cleared by the inner finally — the
                //counter keeps the OUTER dispatch visible here
                outerStillDispatching = d.isDispatching(kEvt);
            });
            d.DispatchEvent(kEvt);
            Check($"e7.isDispatching survives a nested dispatch (still={outerStillDispatching})",
                outerStillDispatching && !d.isDispatching(kEvt));
        }

        //--- e8: RemoveEventListeners during dispatch -----------------------
        {
            var d = new EventDispatcher();
            int bCalls = 0;
            d.AddEventListener(kEvt, ctx => { d.RemoveEventListeners(kEvt); });
            d.AddEventListener(kEvt, ctx => { bCalls++; });
            d.DispatchEvent(kEvt);
            int afterFirst = bCalls;
            bool stillRegistered = d.hasEventListeners(kEvt);
            d.DispatchEvent(kEvt);
            Check($"e8.RemoveEventListeners mid-dispatch: round completes ({afterFirst}), registry empties (has={stillRegistered}, next={bCalls})",
                afterFirst == 1 && !stillRegistered && bCalls == 1);
        }

        //--- e9: context carries data and sender ----------------------------
        {
            var d = new EventDispatcher();
            object seenData = null, seenSender = null;
            d.AddEventListener(kEvt, ctx => { seenData = ctx.data; seenSender = ctx.sender; });
            d.DispatchEvent(kEvt, "payload");
            Check("e9.context data and sender reach the listener",
                (string)seenData == "payload" && ReferenceEquals(seenSender, d));
        }

        //--- e10: unknown-type queries answer false and never throw ---------
        {
            var d = new EventDispatcher();
            bool ok = !d.hasEventListeners(kEvt + "_never")
                && !d.isDispatching(kEvt + "_never");
            d.RemoveEventListeners(kEvt + "_never2"); //no-op, must not throw
            Check("e10.unknown-type queries are quiet no-ops", ok);
        }

        sb.Insert(0, $"RESULT pass={pass} fail={fail}\n");
        return sb.ToString();
    }
}
#endif
