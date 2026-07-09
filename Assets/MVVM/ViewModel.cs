namespace FairyGUI.Mvvm
{
    /// <summary>
    /// Base class for bindable view models. Changed properties are tracked as bits in a
    /// 64-bit dirty mask (hence at most 64 [Observable] properties per inheritance chain);
    /// a Binder consumes the mask on Flush. Single-threaded by design: mutate view models
    /// and flush binders on the main thread.
    /// </summary>
    public abstract class ViewModel
    {
        ulong _dirtyMask;

        internal ulong dirtyMask
        {
            get { return _dirtyMask; }
        }

        internal void ClearDirty()
        {
            _dirtyMask = 0;
        }

        /// <summary>
        /// Marks one property dirty. Generated setters call this; call it manually after
        /// mutating collection contents in place.
        /// </summary>
        public void MarkDirty(int propertyIndex)
        {
            _dirtyMask |= 1UL << propertyIndex;
        }

        /// <summary>
        /// Marks every property dirty, e.g. after replacing the whole model state.
        /// </summary>
        public void MarkAllDirty()
        {
            _dirtyMask = ulong.MaxValue;
        }

        public bool IsDirty(int propertyIndex)
        {
            return (_dirtyMask & (1UL << propertyIndex)) != 0;
        }
    }
}
