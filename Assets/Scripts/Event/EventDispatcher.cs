using System;
using System.Collections.Generic;

namespace FairyGUI
{
    public delegate void EventCallback0();
    public delegate void EventCallback1(EventContext context);

    /// <summary>
    /// Event types are interned to int ids (EventTypeRegistry) at the string entry points;
    /// per-dispatcher bridges live in a small inline array keyed by id, so lookups along
    /// capture/bubble chains are int compares instead of string-hashing dictionary probes.
    /// </summary>
    public class EventDispatcher : IEventDispatcher
    {
        EventBridge[] _bridges;
        int _bridgeCount;

        public EventDispatcher()
        {
        }

        /// <summary>
        ///
        /// </summary>
        /// <param name="strType"></param>
        /// <param name="callback"></param>
        public void AddEventListener(string strType, EventCallback1 callback)
        {
            GetEventBridge(EventTypeRegistry.GetId(strType)).Add(callback);
        }

        /// <summary>
        ///
        /// </summary>
        /// <param name="strType"></param>
        /// <param name="callback"></param>
        public void AddEventListener(string strType, EventCallback0 callback)
        {
            GetEventBridge(EventTypeRegistry.GetId(strType)).Add(callback);
        }

        /// <summary>
        ///
        /// </summary>
        /// <param name="strType"></param>
        /// <param name="callback"></param>
        public void RemoveEventListener(string strType, EventCallback1 callback)
        {
            EventBridge bridge = TryGetEventBridge(EventTypeRegistry.GetId(strType));
            if (bridge != null)
                bridge.Remove(callback);
        }

        /// <summary>
        ///
        /// </summary>
        /// <param name="strType"></param>
        /// <param name="callback"></param>
        public void RemoveEventListener(string strType, EventCallback0 callback)
        {
            EventBridge bridge = TryGetEventBridge(EventTypeRegistry.GetId(strType));
            if (bridge != null)
                bridge.Remove(callback);
        }

        /// <summary>
        ///
        /// </summary>
        /// <param name="strType"></param>
        /// <param name="callback"></param>
        public void AddCapture(string strType, EventCallback1 callback)
        {
            GetEventBridge(EventTypeRegistry.GetId(strType)).AddCapture(callback);
        }

        /// <summary>
        ///
        /// </summary>
        /// <param name="strType"></param>
        /// <param name="callback"></param>
        public void RemoveCapture(string strType, EventCallback1 callback)
        {
            EventBridge bridge = TryGetEventBridge(EventTypeRegistry.GetId(strType));
            if (bridge != null)
                bridge.RemoveCapture(callback);
        }

        /// <summary>
        ///
        /// </summary>
        public void RemoveEventListeners()
        {
            RemoveEventListeners(null);
        }

        /// <summary>
        ///
        /// </summary>
        /// <param name="strType"></param>
        public void RemoveEventListeners(string strType)
        {
            if (_bridgeCount == 0)
                return;

            if (strType != null)
            {
                EventBridge bridge = TryGetEventBridge(EventTypeRegistry.GetId(strType));
                if (bridge != null)
                    bridge.Clear();
            }
            else
            {
                for (int i = 0; i < _bridgeCount; i++)
                    _bridges[i].Clear();
            }
        }

        /// <summary>
        ///
        /// </summary>
        /// <param name="strType"></param>
        /// <returns></returns>
        public bool hasEventListeners(string strType)
        {
            EventBridge bridge = TryGetEventBridge(EventTypeRegistry.GetId(strType));
            if (bridge == null)
                return false;

            return !bridge.isEmpty;
        }

        /// <summary>
        ///
        /// </summary>
        /// <param name="strType"></param>
        /// <returns></returns>
        public bool isDispatching(string strType)
        {
            EventBridge bridge = TryGetEventBridge(EventTypeRegistry.GetId(strType));
            if (bridge == null)
                return false;

            return bridge._dispatching > 0;
        }

        internal EventBridge TryGetEventBridge(string strType)
        {
            return TryGetEventBridge(EventTypeRegistry.GetId(strType));
        }

        internal EventBridge TryGetEventBridge(int typeId)
        {
            for (int i = 0; i < _bridgeCount; i++)
            {
                if (_bridges[i].typeId == typeId)
                    return _bridges[i];
            }
            return null;
        }

        internal EventBridge GetEventBridge(string strType)
        {
            return GetEventBridge(EventTypeRegistry.GetId(strType));
        }

        internal EventBridge GetEventBridge(int typeId)
        {
            EventBridge bridge = TryGetEventBridge(typeId);
            if (bridge != null)
                return bridge;

            if (_bridges == null)
                _bridges = new EventBridge[4];
            else if (_bridgeCount == _bridges.Length)
                Array.Resize(ref _bridges, _bridges.Length * 2);

            bridge = new EventBridge(this, typeId);
            _bridges[_bridgeCount++] = bridge;
            return bridge;
        }

        /// <summary>
        ///
        /// </summary>
        /// <param name="strType"></param>
        /// <returns></returns>
        public bool DispatchEvent(string strType)
        {
            return DispatchEvent(strType, null);
        }

        /// <summary>
        ///
        /// </summary>
        /// <param name="strType"></param>
        /// <param name="data"></param>
        /// <returns></returns>
        public bool DispatchEvent(string strType, object data)
        {
            return InternalDispatchEvent(strType, null, data, null);
        }

        public bool DispatchEvent(string strType, object data, object initiator)
        {
            return InternalDispatchEvent(strType, null, data, initiator);
        }

        static InputEvent sCurrentInputEvent = new InputEvent();

        internal bool InternalDispatchEvent(string strType, EventBridge bridge, object data, object initiator)
        {
            int typeId = bridge != null ? bridge.typeId : EventTypeRegistry.GetId(strType);
            if (bridge == null)
                bridge = TryGetEventBridge(typeId);

            EventBridge gBridge = null;
            if ((this is DisplayObject) && ((DisplayObject)this).gOwner != null)
                gBridge = ((DisplayObject)this).gOwner.TryGetEventBridge(typeId);

            bool b1 = bridge != null && !bridge.isEmpty;
            bool b2 = gBridge != null && !gBridge.isEmpty;
            if (b1 || b2)
            {
                EventContext context = EventContext.Get();
                context.initiator = initiator != null ? initiator : this;
                context.type = strType;
                context.data = data;
                if (data is InputEvent)
                    sCurrentInputEvent = (InputEvent)data;
                context.inputEvent = sCurrentInputEvent;

                if (b1)
                {
                    bridge.CallCaptureInternal(context);
                    bridge.CallInternal(context);
                }

                if (b2)
                {
                    gBridge.CallCaptureInternal(context);
                    gBridge.CallInternal(context);
                }

                EventContext.Return(context);
                context.initiator = null;
                context.sender = null;
                context.data = null;

                return context._defaultPrevented;
            }
            else
                return false;
        }

        /// <summary>
        ///
        /// </summary>
        /// <param name="context"></param>
        /// <returns></returns>
        public bool DispatchEvent(EventContext context)
        {
            int typeId = EventTypeRegistry.GetId(context.type);
            EventBridge bridge = TryGetEventBridge(typeId);
            EventBridge gBridge = null;
            if ((this is DisplayObject) && ((DisplayObject)this).gOwner != null)
                gBridge = ((DisplayObject)this).gOwner.TryGetEventBridge(typeId);

            EventDispatcher savedSender = context.sender;

            if (bridge != null && !bridge.isEmpty)
            {
                bridge.CallCaptureInternal(context);
                bridge.CallInternal(context);
            }

            if (gBridge != null && !gBridge.isEmpty)
            {
                gBridge.CallCaptureInternal(context);
                gBridge.CallInternal(context);
            }

            context.sender = savedSender;
            return context._defaultPrevented;
        }

        /// <summary>
        ///
        /// </summary>
        /// <param name="strType"></param>
        /// <param name="data"></param>
        /// <param name="addChain"></param>
        /// <returns></returns>
        internal bool BubbleEvent(string strType, object data, List<EventBridge> addChain)
        {
            int typeId = EventTypeRegistry.GetId(strType);

            EventContext context = EventContext.Get();
            context.initiator = this;

            context.type = strType;
            context.data = data;
            if (data is InputEvent)
                sCurrentInputEvent = (InputEvent)data;
            context.inputEvent = sCurrentInputEvent;
            List<EventBridge> bubbleChain = context.callChain;
            bubbleChain.Clear();

            GetChainBridges(typeId, bubbleChain, true);

            int length = bubbleChain.Count;
            for (int i = length - 1; i >= 0; i--)
            {
                bubbleChain[i].CallCaptureInternal(context);
                if (context._touchCapture)
                {
                    context._touchCapture = false;
                    if (strType == "onTouchBegin")
                        Stage.inst.AddTouchMonitor(context.inputEvent.touchId, bubbleChain[i].owner);
                }
            }

            if (!context._stopsPropagation)
            {
                for (int i = 0; i < length; ++i)
                {
                    bubbleChain[i].CallInternal(context);

                    if (context._touchCapture)
                    {
                        context._touchCapture = false;
                        if (strType == "onTouchBegin")
                            Stage.inst.AddTouchMonitor(context.inputEvent.touchId, bubbleChain[i].owner);
                    }

                    if (context._stopsPropagation)
                        break;
                }

                if (addChain != null)
                {
                    length = addChain.Count;
                    for (int i = 0; i < length; ++i)
                    {
                        EventBridge bridge = addChain[i];
                        if (bubbleChain.IndexOf(bridge) == -1)
                        {
                            bridge.CallCaptureInternal(context);
                            bridge.CallInternal(context);
                        }
                    }
                }
            }

            EventContext.Return(context);
            context.initiator = null;
            context.sender = null;
            context.data = null;
            return context._defaultPrevented;
        }

        /// <summary>
        ///
        /// </summary>
        /// <param name="strType"></param>
        /// <param name="data"></param>
        /// <returns></returns>
        public bool BubbleEvent(string strType, object data)
        {
            return BubbleEvent(strType, data, null);
        }

        /// <summary>
        ///
        /// </summary>
        /// <param name="strType"></param>
        /// <param name="data"></param>
        /// <returns></returns>
        public bool BroadcastEvent(string strType, object data)
        {
            int typeId = EventTypeRegistry.GetId(strType);

            EventContext context = EventContext.Get();
            context.initiator = this;
            context.type = strType;
            context.data = data;
            if (data is InputEvent)
                sCurrentInputEvent = (InputEvent)data;
            context.inputEvent = sCurrentInputEvent;
            List<EventBridge> bubbleChain = context.callChain;
            bubbleChain.Clear();

            if (this is Container)
                GetChildEventBridges(typeId, (Container)this, bubbleChain);
            else if (this is GComponent)
                GetChildEventBridges(typeId, (GComponent)this, bubbleChain);

            int length = bubbleChain.Count;
            for (int i = 0; i < length; ++i)
                bubbleChain[i].CallInternal(context);

            EventContext.Return(context);
            context.initiator = null;
            context.sender = null;
            context.data = null;
            return context._defaultPrevented;
        }

        static void GetChildEventBridges(int typeId, Container container, List<EventBridge> bridges)
        {
            EventBridge bridge = container.TryGetEventBridge(typeId);
            if (bridge != null)
                bridges.Add(bridge);
            if (container.gOwner != null)
            {
                bridge = container.gOwner.TryGetEventBridge(typeId);
                if (bridge != null && !bridge.isEmpty)
                    bridges.Add(bridge);
            }

            int count = container.numChildren;
            for (int i = 0; i < count; ++i)
            {
                DisplayObject obj = container.GetChildAt(i);
                if (obj is Container)
                    GetChildEventBridges(typeId, (Container)obj, bridges);
                else
                {
                    bridge = obj.TryGetEventBridge(typeId);
                    if (bridge != null && !bridge.isEmpty)
                        bridges.Add(bridge);

                    if (obj.gOwner != null)
                    {
                        bridge = obj.gOwner.TryGetEventBridge(typeId);
                        if (bridge != null && !bridge.isEmpty)
                            bridges.Add(bridge);
                    }
                }
            }
        }

        static void GetChildEventBridges(int typeId, GComponent container, List<EventBridge> bridges)
        {
            EventBridge bridge = container.TryGetEventBridge(typeId);
            if (bridge != null)
                bridges.Add(bridge);

            int count = container.numChildren;
            for (int i = 0; i < count; ++i)
            {
                GObject obj = container.GetChildAt(i);
                if (obj is GComponent)
                    GetChildEventBridges(typeId, (GComponent)obj, bridges);
                else
                {
                    bridge = obj.TryGetEventBridge(typeId);
                    if (bridge != null)
                        bridges.Add(bridge);
                }
            }
        }

        internal void GetChainBridges(string strType, List<EventBridge> chain, bool bubble)
        {
            GetChainBridges(EventTypeRegistry.GetId(strType), chain, bubble);
        }

        internal void GetChainBridges(int typeId, List<EventBridge> chain, bool bubble)
        {
            EventBridge bridge = TryGetEventBridge(typeId);
            if (bridge != null && !bridge.isEmpty)
                chain.Add(bridge);

            if ((this is DisplayObject) && ((DisplayObject)this).gOwner != null)
            {
                bridge = ((DisplayObject)this).gOwner.TryGetEventBridge(typeId);
                if (bridge != null && !bridge.isEmpty)
                    chain.Add(bridge);
            }

            if (!bubble)
                return;

            if (this is DisplayObject)
            {
                DisplayObject element = (DisplayObject)this;
                while ((element = element.parent) != null)
                {
                    bridge = element.TryGetEventBridge(typeId);
                    if (bridge != null && !bridge.isEmpty)
                        chain.Add(bridge);

                    if (element.gOwner != null)
                    {
                        bridge = element.gOwner.TryGetEventBridge(typeId);
                        if (bridge != null && !bridge.isEmpty)
                            chain.Add(bridge);
                    }
                }
            }
            else if (this is GObject)
            {
                GObject element = (GObject)this;
                while ((element = element.parent) != null)
                {
                    bridge = element.TryGetEventBridge(typeId);
                    if (bridge != null && !bridge.isEmpty)
                        chain.Add(bridge);
                }
            }
        }
    }
}
