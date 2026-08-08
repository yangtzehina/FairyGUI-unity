#if FAIRYGUI_TOLUA
//Minimal LuaInterface stubs: enough surface for the desktop gate to COMPILE
//the FAIRYGUI_TOLUA branches (the event-layer rewrite ported them without any
//compile coverage — audit). Signatures mirror the toLua# API the code calls;
//behavior is irrelevant, this assembly is never executed.
using System;

namespace LuaInterface
{
    public sealed class NoToLuaAttribute : Attribute
    {
    }

    public class LuaState
    {
        public LuaDelegate GetLuaDelegate(LuaFunction func) => null;
        public LuaDelegate GetLuaDelegate(LuaFunction func, LuaTable self) => null;
    }

    public class LuaDelegate
    {
    }

    public class LuaFunction : IDisposable
    {
        public LuaState GetLuaState() => null;
        public void BeginPCall() { }
        public void Push(object arg) { }
        public void PCall() { }
        public void EndPCall() { }
        public LuaTable CheckLuaTable() => null;
        public void Call(params object[] args) { }
        public void Dispose() { }
    }

    public class LuaTable : IDisposable
    {
        public LuaFunction GetLuaFunction(string name) => null;
        public void Dispose() { }
    }

    public static class DelegateTraits<T>
    {
        public static Delegate Create(LuaFunction func) => null;
        public static Delegate Create(LuaFunction func, LuaTable self) => null;
    }
}
#endif
