using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Nirvana.Game.Launcher.Services.Java.RPC.Events;

public static class SimplePack {
    public static byte[]? Pack(params object[]? data)
    {
        if (data == null) {
            return null;
        }

        using var buffer = new MemoryStream();
        foreach (var obj in data) {
            WriteValue(buffer, obj);
        }

        return buffer.ToArray();
    }

    private static void WriteValue(MemoryStream buffer, object obj)
    {
        switch (obj) {
            case bool v: {
                buffer.Write(BitConverter.GetBytes(v));
                break;
            }
            case byte v: {
                buffer.WriteByte(v);
                break;
            }
            case short v: {
                buffer.Write(BitConverter.GetBytes(v));
                break;
            }
            case ushort v: {
                buffer.Write(BitConverter.GetBytes(v));
                break;
            }
            case int v: {
                buffer.Write(BitConverter.GetBytes(v));
                break;
            }
            case uint v: {
                buffer.Write(BitConverter.GetBytes(v));
                break;
            }
            case long v: {
                buffer.Write(BitConverter.GetBytes(v));
                break;
            }
            case ulong v: {
                buffer.Write(BitConverter.GetBytes(v));
                break;
            }
            case float v: {
                buffer.Write(BitConverter.GetBytes(v));
                break;
            }
            case double v: {
                buffer.Write(BitConverter.GetBytes(v));
                break;
            }
            case string v: {
                WriteString(buffer, v);
                break;
            }
            case byte[] v: {
                buffer.Write(v);
                break;
            }
            case List<uint> v: {
                WritePrimitiveList(buffer, v, sizeof(uint), BitConverter.GetBytes);
                break;
            }
            case List<ulong> v: {
                WritePrimitiveList(buffer, v, sizeof(ulong), BitConverter.GetBytes);
                break;
            }
            case List<long> v: {
                WritePrimitiveList(buffer, v, sizeof(long), BitConverter.GetBytes);
                break;
            }
            default:
                throw new NotSupportedException($"SimplePack does not support type: {obj.GetType().FullName}");
        }
    }

    private static void WriteString(MemoryStream buffer, string value)
    {
        var utf8 = Encoding.UTF8.GetBytes(value);
        buffer.Write(BitConverter.GetBytes((ushort)utf8.Length));
        buffer.Write(utf8);
    }

    private static void WritePrimitiveList<T>(MemoryStream buffer, List<T> list, int elementSize, Func<T, byte[]> converter)
    {
        buffer.Write(BitConverter.GetBytes((ushort)(list.Count * elementSize)));
        foreach (var item in list) {
            buffer.Write(converter(item));
        }
    }
}