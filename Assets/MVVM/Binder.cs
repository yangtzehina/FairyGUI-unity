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
            public readonly List<Entry> entries = new List<Entry>();
        }

        readonly List<Group> _groups = new List<Group>();
        readonly List<Group> _flushScratch = new List<Group>();

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
            _flushScratch.Clear();
            _flushScratch.AddRange(_groups);
            int cnt = _flushScratch.Count;
            for (int i = 0; i < cnt; i++)
            {
                Group group = _flushScratch[i];
                if (group.unbound)
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
                for (int j = 0; j < entryCount; j++)
                {
                    Entry e = entries[j];
                    if ((mask & (1UL << e.propertyIndex)) != 0)
                        e.apply();
                }
            }
            _flushScratch.Clear();
        }

        /// <summary>
        /// Reapplies every binding regardless of dirty state (e.g. after view recreation).
        /// </summary>
        public void ApplyAll()
        {
            _flushScratch.Clear();
            _flushScratch.AddRange(_groups);
            int cnt = _flushScratch.Count;
            for (int i = 0; i < cnt; i++)
            {
                Group group = _flushScratch[i];
                if (group.unbound)
                    continue;
                //consume the mask up front: everything is reapplied anyway, and
                //writes made during the applies below must survive
                group.vm.ClearDirty(group.vm.dirtyMask);
                List<Entry> entries = group.entries;
                int entryCount = entries.Count;
                for (int j = 0; j < entryCount; j++)
                    entries[j].apply();
            }
            _flushScratch.Clear();
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
