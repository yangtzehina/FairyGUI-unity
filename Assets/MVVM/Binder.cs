using System;
using System.Collections.Generic;

namespace FairyGUI.Mvvm
{
    /// <summary>
    /// One-way binding engine: register (viewModel, propertyIndex, apply) entries once,
    /// then call Flush once per frame (or whenever convenient). Flush walks registered
    /// view models, runs the apply actions of dirty properties and clears the masks —
    /// no reflection, no boxing, no per-flush allocation. Writes are coalesced: however
    /// many times a property changed since the last flush, apply runs once.
    ///
    /// Reentrancy (review V1/V6): apply callbacks may freely write view-model
    /// properties (including the one being flushed — the new dirt survives to the
    /// next Flush, because only the flushed snapshot bits are cleared) and may
    /// Bind/Unbind/Clear this binder (Flush iterates a snapshot; unbound groups are
    /// tombstoned, groups bound during a flush apply from the next one).
    ///
    /// Each ViewModel should be flushed by exactly one Binder (flushing consumes the
    /// dirty mask). A Binder typically lives beside a view (panel/window) and is
    /// Cleared when the view is disposed.
    /// </summary>
    public class Binder
    {
        struct Entry
        {
            public int propertyIndex;
            public Action apply;
        }

        class Group
        {
            public ViewModel vm;
            public bool unbound;
            //set while this group's entries are running. A nested Flush/ApplyAll
            //must skip it: ApplyAll ignores dirty state by design, so an apply
            //callback that calls ApplyAll would otherwise re-run ITSELF, for
            //ever — a stack overflow, not a slow loop. (Found the hard way: the
            //regression test written for the nested-call fix crashed the editor
            //three times before this guard existed. The per-depth scratch list
            //keeps the buffers from clobbering each other; only this stops the
            //recursion.)
            public bool applying;
            public readonly List<Entry> entries = new List<Entry>();
        }

        readonly List<Group> _groups = new List<Group>();

        //One scratch list PER NESTING DEPTH. A single shared one was enough for
        //the V6 hazard (Bind/Unbind/Clear during apply) but not for an apply
        //callback that calls Flush() or ApplyAll() again — a realistic "the
        //sub-view was recreated, resync it" move. The nested call would clear
        //and refill the one buffer, then empty it on exit, and the outer loop's
        //next index read threw ArgumentOutOfRangeException with the outer
        //group's dirty bits already consumed: the UI stays permanently out of
        //sync even after the exception is caught. Stack discipline, allocation
        //-free after the deepest nesting seen.
        readonly List<List<Group>> _scratchPool = new List<List<Group>>();
        int _depth;

        List<Group> RentScratch()
        {
            if (_depth == _scratchPool.Count)
                _scratchPool.Add(new List<Group>());
            List<Group> scratch = _scratchPool[_depth++];
            scratch.Clear();
            scratch.AddRange(_groups);
            return scratch;
        }

        void ReturnScratch(List<Group> scratch)
        {
            scratch.Clear();
            _depth--;
        }

        /// <summary>
        /// Binds a property (by its generated index constant) to an apply action that
        /// reads the view model and writes the UI. Runs apply immediately by default so
        /// the view starts in sync.
        /// </summary>
        public Binder Bind(ViewModel vm, int propertyIndex, Action apply, bool applyNow = true)
        {
            Group group = null;
            int cnt = _groups.Count;
            for (int i = 0; i < cnt; i++)
            {
                if (_groups[i].vm == vm)
                {
                    group = _groups[i];
                    break;
                }
            }
            if (group == null)
            {
                group = new Group { vm = vm };
                _groups.Add(group);
            }

            group.entries.Add(new Entry { propertyIndex = propertyIndex, apply = apply });
            if (applyNow)
                apply();
            return this;
        }

        /// <summary>
        /// Removes all bindings of a view model. Safe to call from an apply callback:
        /// the group is tombstoned so an in-progress Flush skips it.
        /// </summary>
        public void Unbind(ViewModel vm)
        {
            int cnt = _groups.Count;
            for (int i = 0; i < cnt; i++)
            {
                if (_groups[i].vm == vm)
                {
                    _groups[i].unbound = true;
                    _groups.RemoveAt(i);
                    return;
                }
            }
        }

        /// <summary>
        /// Applies pending changes to the UI and clears the flushed bits. Bits set
        /// DURING apply callbacks (cascading writes) are preserved for the next Flush.
        /// </summary>
        public void Flush()
        {
            //snapshot: Bind/Unbind/Clear inside apply callbacks must not derail
            //the iteration (review V6)
            List<Group> scratch = RentScratch();
            try
            {
                int cnt = scratch.Count;
                for (int i = 0; i < cnt; i++)
                {
                    Group group = scratch[i];
                    if (group.unbound || group.applying)
                        continue;
                    ulong mask = group.vm.dirtyMask;
                    if (mask == 0)
                        continue;

                    //consume the snapshot up front: ANY write made during the applies
                    //below — another property or a re-mark of the one being applied
                    //(read-clamp-writeback) — lands after this clear and flushes next
                    //time (review V1). If an apply throws, the consumed bits are lost;
                    //apply callbacks are expected not to throw.
                    group.vm.ClearDirty(mask);

                    List<Entry> entries = group.entries;
                    int entryCount = entries.Count;
                    group.applying = true;
                    try
                    {
                    for (int j = 0; j < entryCount; j++)
                    {
                        //re-test per ENTRY, not just per group: an earlier entry of
                        //this same group may have Unbound its own view model (a
                        //"close me" handler) or Clear()ed the binder, and the later
                        //entries would otherwise keep writing into a disposed view.
                        //That is exactly what Unbind's doc promises and what the V6
                        //tests missed by binding only one entry per view model.
                        if (group.unbound)
                            break;
                        Entry e = entries[j];
                        if ((mask & (1UL << e.propertyIndex)) != 0)
                            e.apply();
                    }
                    }
                    finally { group.applying = false; }
                }
            }
            finally
            {
                ReturnScratch(scratch);
            }
        }

        /// <summary>
        /// Reapplies every binding regardless of dirty state (e.g. after view recreation).
        /// </summary>
        public void ApplyAll()
        {
            List<Group> scratch = RentScratch();
            try
            {
                int cnt = scratch.Count;
                for (int i = 0; i < cnt; i++)
                {
                    Group group = scratch[i];
                    if (group.unbound || group.applying)
                        continue;
                    //consume the mask up front: everything is reapplied anyway, and
                    //writes made during the applies below must survive
                    group.vm.ClearDirty(group.vm.dirtyMask);
                    List<Entry> entries = group.entries;
                    int entryCount = entries.Count;
                    group.applying = true;
                    try
                    {
                        for (int j = 0; j < entryCount; j++)
                        {
                            if (group.unbound) //see Flush
                                break;
                            entries[j].apply();
                        }
                    }
                    finally { group.applying = false; }
                }
            }
            finally
            {
                ReturnScratch(scratch);
            }
        }

        public void Clear()
        {
            //tombstone so an in-progress Flush stops touching them
            int cnt = _groups.Count;
            for (int i = 0; i < cnt; i++)
                _groups[i].unbound = true;
            _groups.Clear();
        }
    }
}
