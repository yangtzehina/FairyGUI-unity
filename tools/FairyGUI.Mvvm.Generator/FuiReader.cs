using System;
using System.Collections.Generic;
using System.Text;

namespace FairyGUI.Mvvm.Generator
{
    /// <summary>
    /// Minimal read-only parser for FairyGUI .fui packages, ported from the runtime's
    /// UIPackage.LoadPackage / GComponent.ConstructFromResource read paths. Only the
    /// pieces needed for typed view generation are read: the string table, the item
    /// table (component items with their extension type), and each component's child
    /// list (name, object type, referenced item).
    /// </summary>
    sealed class FuiPackage
    {
        public string id = "";
        public string name = "";
        public readonly List<FuiComponent> components = new List<FuiComponent>();

        public FuiComponent FindComponent(string componentName)
        {
            foreach (var c in components)
            {
                if (c.name == componentName)
                    return c;
            }
            return null;
        }

        public FuiComponent FindComponentById(string itemId)
        {
            foreach (var c in components)
            {
                if (c.id == itemId)
                    return c;
            }
            return null;
        }
    }

    sealed class FuiComponent
    {
        public string id = "";
        public string name = "";
        public int objectType;   //ObjectType: 9 plain component, 11+ extensions (button etc.)
        public int rawPos;
        public int rawLen;
        public List<FuiChild> children;
    }

    struct FuiChild
    {
        public string name;
        public int objectType;
        public string srcId;
        public string pkgId;
    }

    sealed class FuiFormatException : Exception
    {
        public FuiFormatException(string message) : base(message)
        {
        }
    }

    static class FuiReader
    {
        const uint Magic = 0x46475549;
        const int TypeComponent = 3; //PackageItemType.Component
        const int ObjectTypeComponent = 9;

        sealed class Cursor
        {
            readonly byte[] _d;
            public readonly int basePos;
            public int pos;
            public string[] table;

            public Cursor(byte[] data, int basePos, string[] table)
            {
                _d = data;
                this.basePos = basePos;
                this.table = table;
            }

            public byte U8()
            {
                return _d[basePos + pos++];
            }

            public bool Bool()
            {
                return U8() == 1;
            }

            public int U16()
            {
                int p = basePos + pos;
                pos += 2;
                return (_d[p] << 8) | _d[p + 1];
            }

            public short I16()
            {
                return (short)U16();
            }

            public int I32()
            {
                int p = basePos + pos;
                pos += 4;
                return (_d[p] << 24) | (_d[p + 1] << 16) | (_d[p + 2] << 8) | _d[p + 3];
            }

            public string Str()
            {
                int len = U16();
                string s = Encoding.UTF8.GetString(_d, basePos + pos, len);
                pos += len;
                return s;
            }

            public string Str(int len)
            {
                string s = Encoding.UTF8.GetString(_d, basePos + pos, len);
                pos += len;
                return s;
            }

            public string S()
            {
                int index = U16();
                if (index == 65534)
                    return null;
                if (index == 65533)
                    return string.Empty;
                return table[index];
            }

            public void Skip(int count)
            {
                pos += count;
            }

            //same layout as ByteBuffer.Seek: [segCount:u8][useShort:u8][offsets...]
            public bool Seek(int indexTablePos, int blockIndex)
            {
                int saved = pos;
                pos = indexTablePos;
                int segCount = U8();
                if (blockIndex >= segCount)
                {
                    pos = saved;
                    return false;
                }

                bool useShort = U8() == 1;
                int newPos;
                if (useShort)
                {
                    Skip(2 * blockIndex);
                    newPos = U16();
                }
                else
                {
                    Skip(4 * blockIndex);
                    newPos = I32();
                }

                if (newPos > 0)
                {
                    pos = indexTablePos + newPos;
                    return true;
                }

                pos = saved;
                return false;
            }
        }

        public static FuiPackage Parse(byte[] data)
        {
            var c = new Cursor(data, 0, null);
            if ((uint)c.I32() != Magic)
                throw new FuiFormatException("not a FairyGUI package (bad magic)");

            c.I32(); //version
            if (c.Bool())
                throw new FuiFormatException("compressed packages are not supported; publish uncompressed");

            var pkg = new FuiPackage();
            pkg.id = c.Str();
            pkg.name = c.Str();
            c.Skip(20);
            int indexTablePos = c.pos;

            if (!c.Seek(indexTablePos, 4))
                throw new FuiFormatException("string table block missing");
            int cnt = c.I32();
            string[] table = new string[cnt];
            for (int i = 0; i < cnt; i++)
                table[i] = c.Str();
            c.table = table;

            if (c.Seek(indexTablePos, 5))
            {
                cnt = c.I32();
                for (int i = 0; i < cnt; i++)
                {
                    int index = c.U16();
                    int len = c.I32();
                    table[index] = c.Str(len);
                }
            }

            if (!c.Seek(indexTablePos, 1))
                throw new FuiFormatException("item table block missing");

            cnt = c.I16();
            for (int i = 0; i < cnt; i++)
            {
                int nextPos = c.I32();
                nextPos += c.pos;

                int type = c.U8();
                string id = c.S();
                string name = c.S();
                c.S(); //path
                c.S(); //file
                c.Bool(); //exported
                c.I32(); //width
                c.I32(); //height

                if (type == TypeComponent)
                {
                    int extension = c.U8();
                    var comp = new FuiComponent();
                    comp.id = id ?? "";
                    comp.name = name ?? "";
                    comp.objectType = extension > 0 ? extension : ObjectTypeComponent;
                    comp.rawLen = c.I32();
                    comp.rawPos = c.pos;
                    pkg.components.Add(comp);
                }

                c.pos = nextPos;
            }

            //resolve children now so consumers never touch raw bytes again
            foreach (var comp in pkg.components)
                comp.children = ReadChildren(data, comp, table);

            return pkg;
        }

        static List<FuiChild> ReadChildren(byte[] data, FuiComponent comp, string[] table)
        {
            var children = new List<FuiChild>();
            var c = new Cursor(data, comp.rawPos, table);

            if (!c.Seek(0, 2))
                return children;

            int childCount = c.I16();
            for (int i = 0; i < childCount; i++)
            {
                int dataLen = c.I16();
                int curPos = c.pos;

                c.Seek(curPos, 0);
                var child = new FuiChild();
                child.objectType = c.U8();
                child.srcId = c.S();
                child.pkgId = c.S();
                c.S(); //id
                child.name = c.S();
                children.Add(child);

                c.pos = curPos + dataLen;
            }

            return children;
        }
    }
}
