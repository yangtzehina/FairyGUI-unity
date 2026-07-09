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
            public readonly List<Entry> entries = new List<Entry>();
        }

        readonly List<Group> _groups = new List<Group>();

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
        /// Removes all bindings of a view model.
        /// </summary>
        public void Unbind(ViewModel vm)
        {
            int cnt = _groups.Count;
            for (int i = 0; i < cnt; i++)
            {
                if (_groups[i].vm == vm)
                {
                    _groups.RemoveAt(i);
                    return;
                }
            }
        }

        /// <summary>
        /// Applies pending changes to the UI and clears the dirty masks.
        /// </summary>
        public void Flush()
        {
            int cnt = _groups.Count;
            for (int i = 0; i < cnt; i++)
            {
                Group group = _groups[i];
                ulong mask = group.vm.dirtyMask;
                if (mask == 0)
                    continue;

                List<Entry> entries = group.entries;
                int entryCount = entries.Count;
                for (int j = 0; j < entryCount; j++)
                {
                    Entry e = entries[j];
                    if ((mask & (1UL << e.propertyIndex)) != 0)
                        e.apply();
                }
                group.vm.ClearDirty();
            }
        }

        /// <summary>
        /// Reapplies every binding regardless of dirty state (e.g. after view recreation).
        /// </summary>
        public void ApplyAll()
        {
            int cnt = _groups.Count;
            for (int i = 0; i < cnt; i++)
            {
                List<Entry> entries = _groups[i].entries;
                int entryCount = entries.Count;
                for (int j = 0; j < entryCount; j++)
                    entries[j].apply();
                _groups[i].vm.ClearDirty();
            }
        }

        public void Clear()
        {
            _groups.Clear();
        }
    }
}
