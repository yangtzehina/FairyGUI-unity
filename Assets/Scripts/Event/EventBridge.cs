#if FAIRYGUI_TOLUA
using System;
using LuaInterface;
#endif
using System.Collections.Generic;

namespace FairyGUI
{
    /// <summary>
    /// Holds the listeners of one event type on one dispatcher. Callbacks live in plain
    /// lists, so Add/Remove never allocate (the old multicast-delegate fields allocated a
    /// new invocation array on every Combine/Remove). Dispatch keeps the old multicast
    /// snapshot semantics: the callback set is captured (into a pooled array) when the
    /// call starts, so adds/removes during dispatch only affect the next dispatch.
    /// </summary>
    class EventBridge
    {
        public EventDispatcher owner;
        internal readonly int typeId;

        List<EventCallback1> _callbacks1;
        List<EventCallback0> _callbacks0;
        List<EventCallback1> _captures;
        internal int _dispatching;

        //pooled snapshot buffers; stacks because dispatches nest (an event handler can dispatch)
        static readonly Stack<EventCallback1[]> sPool1 = new Stack<EventCallback1[]>();
        static readonly Stack<EventCallback0[]> sPool0 = new Stack<EventCallback0[]>();

        public EventBridge(EventDispatcher owner, int typeId)
        {
            this.owner = owner;
            this.typeId = typeId;
        }

        public void AddCapture(EventCallback1 callback)
        {
            //a null callback used to be harmless: the old storage was a multicast
            //delegate, and Delegate.Combine(x, null) == x, so it never registered.
            //With list storage an unguarded null lands at index 0, makes isEmpty
            //(count-based) start lying, and then throws from CallInternal on the
            //FIRST entry — which kills every real listener on that event type too,
            //with a stack trace pointing nowhere near the registration site.
            if (callback == null)
                return;
            if (_captures == null)
                _captures = new List<EventCallback1>(2);
            _captures.Remove(callback);
            _captures.Add(callback);
        }

        public void RemoveCapture(EventCallback1 callback)
        {
            if (_captures != null)
                _captures.Remove(callback);
        }

        public void Add(EventCallback1 callback)
        {
            if (callback == null)
                return; //see AddCapture
            if (_callbacks1 == null)
                _callbacks1 = new List<EventCallback1>(2);
            _callbacks1.Remove(callback);
            _callbacks1.Add(callback);
        }

        public void Remove(EventCallback1 callback)
        {
            if (_callbacks1 != null)
                _callbacks1.Remove(callback);
        }

        public void Add(EventCallback0 callback)
        {
            if (callback == null)
                return; //see AddCapture
            if (_callbacks0 == null)
                _callbacks0 = new List<EventCallback0>(2);
            _callbacks0.Remove(callback);
            _callbacks0.Add(callback);
        }

        public void Remove(EventCallback0 callback)
        {
            if (_callbacks0 != null)
                _callbacks0.Remove(callback);
        }

#if FAIRYGUI_TOLUA
        public void Add(LuaFunction func, LuaTable self)
        {
            EventCallback1 callback;
            if (self != null)
                callback = (EventCallback1)DelegateTraits<EventCallback1>.Create(func, self);
            else
                callback = (EventCallback1)DelegateTraits<EventCallback1>.Create(func);
            Add(callback);
        }

        public void Add(LuaFunction func, GComponent self)
        {
            if (self._peerTable == null)
                throw new Exception("self is not connected to lua.");

            Add(func, self._peerTable);
        }

        public void Remove(LuaFunction func, LuaTable self)
        {
            if (_callbacks1 == null)
                return;

            LuaState state = func.GetLuaState();
            LuaDelegate target;
            if (self != null)
                target = state.GetLuaDelegate(func, self);
            else
                target = state.GetLuaDelegate(func);

            int cnt = _callbacks1.Count;
            for (int i = 0; i < cnt; i++)
            {
                LuaDelegate ld = _callbacks1[i].Target as LuaDelegate;
                if (ld != null && ld.Equals(target))
                {
                    _callbacks1.RemoveAt(i);
                    break;
                }
            }
        }

        public void Remove(LuaFunction func, GComponent self)
        {
            if (self._peerTable == null)
                throw new Exception("self is not connected to lua.");

            Remove(func, self._peerTable);
        }
#endif

        public bool isEmpty
        {
            get
            {
                return (_callbacks1 == null || _callbacks1.Count == 0)
                    && (_callbacks0 == null || _callbacks0.Count == 0)
                    && (_captures == null || _captures.Count == 0);
            }
        }

        public void Clear()
        {
            if (_callbacks1 != null) _callbacks1.Clear();
            if (_callbacks0 != null) _callbacks0.Clear();
            if (_captures != null) _captures.Clear();
        }

        public void CallInternal(EventContext context)
        {
            _dispatching++;
            context.sender = owner;
            try
            {
                if (_callbacks1 != null && _callbacks1.Count > 0)
                {
                    int cnt = _callbacks1.Count;
                    EventCallback1[] snapshot = Rent1(cnt);
                    _callbacks1.CopyTo(snapshot);
                    try
                    {
                        for (int i = 0; i < cnt; i++)
                            snapshot[i](context);
                    }
                    finally
                    {
                        Return1(snapshot, cnt);
                    }
                }

                if (_callbacks0 != null && _callbacks0.Count > 0)
                {
                    int cnt = _callbacks0.Count;
                    EventCallback0[] snapshot = Rent0(cnt);
                    _callbacks0.CopyTo(snapshot);
                    try
                    {
                        for (int i = 0; i < cnt; i++)
                            snapshot[i]();
                    }
                    finally
                    {
                        Return0(snapshot, cnt);
                    }
                }
            }
            finally
            {
                _dispatching--;
            }
        }

        public void CallCaptureInternal(EventContext context)
        {
            if (_captures == null || _captures.Count == 0)
                return;

            _dispatching++;
            context.sender = owner;
            try
            {
                int cnt = _captures.Count;
                EventCallback1[] snapshot = Rent1(cnt);
                _captures.CopyTo(snapshot);
                try
                {
                    for (int i = 0; i < cnt; i++)
                        snapshot[i](context);
                }
                finally
                {
                    Return1(snapshot, cnt);
                }
            }
            finally
            {
                _dispatching--;
            }
        }

        static EventCallback1[] Rent1(int minSize)
        {
            if (sPool1.Count > 0)
            {
                EventCallback1[] buffer = sPool1.Pop();
                if (buffer.Length >= minSize)
                    return buffer;
                //too small for this bridge; larger replacement joins the pool on return
            }
            return new EventCallback1[NextSize(minSize)];
        }

        static void Return1(EventCallback1[] buffer, int used)
        {
            System.Array.Clear(buffer, 0, used);
            sPool1.Push(buffer);
        }

        static EventCallback0[] Rent0(int minSize)
        {
            if (sPool0.Count > 0)
            {
                EventCallback0[] buffer = sPool0.Pop();
                if (buffer.Length >= minSize)
                    return buffer;
            }
            return new EventCallback0[NextSize(minSize)];
        }

        static void Return0(EventCallback0[] buffer, int used)
        {
            System.Array.Clear(buffer, 0, used);
            sPool0.Push(buffer);
        }

        static int NextSize(int minSize)
        {
            int size = 8;
            while (size < minSize)
                size *= 2;
            return size;
        }
    }
}
