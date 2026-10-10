using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Resources;
using System.Text;

namespace AddinLoader.Core
{
    // BAML record layouts follow WPF's BamlRecords.cs. Preserve opaque records and relocate
    // deferred dictionary offsets whenever an assembly identity or resource URI grows.
    internal static class BamlRewriter
    {
        public static byte[] RewriteResources(byte[] data, IDictionary<string, string> renames)
        {
            using (var input = new MemoryStream(data))
            using (var reader = new ResourceReader(input))
            using (var output = new MemoryStream())
            {
                using (var writer = new ResourceWriter(output))
                {
                    foreach (DictionaryEntry entry in reader)
                    {
                        var name = (string)entry.Key;
                        reader.GetResourceData(name, out var type, out var bytes);
                        if (name.EndsWith(".baml", StringComparison.OrdinalIgnoreCase)
                            && (type == "ResourceTypeCode.Stream" || type == "ResourceTypeCode.ByteArray"))
                        {
                            if (bytes.Length < 4 || BitConverter.ToInt32(bytes, 0) != bytes.Length - 4)
                                throw new InvalidDataException("Invalid BAML resource length: " + name);
                            var rewritten = Rewrite(bytes.Skip(4).ToArray(), renames);
                            bytes = BitConverter.GetBytes(rewritten.Length).Concat(rewritten).ToArray();
                        }
                        writer.AddResourceData(name, type, bytes);
                    }
                    writer.Generate();
                    return output.ToArray();
                }
            }
        }

        internal sealed class Record
        {
            public byte Type;
            public int Start, End, OutputStart, OutputEnd;
            public bool Sized;
            public byte[] Data;
        }

        internal static List<Record> Read(byte[] bytes)
        {
            using (var stream = new MemoryStream(bytes))
            using (var reader = new BinaryReader(stream, Encoding.UTF8))
            {
                if (reader.ReadInt32() != 12 || Encoding.Unicode.GetString(reader.ReadBytes(12)) != "MSBAML")
                    throw new InvalidDataException("Unsupported BAML header.");
                for (var i = 0; i < 3; i++)
                    if (reader.ReadUInt16() != 0 || reader.ReadUInt16() != 96)
                        throw new InvalidDataException("Unsupported BAML version.");
                var records = new List<Record>();
                while (stream.Position < stream.Length)
                {
                    var record = new Record { Start = checked((int)stream.Position), Type = reader.ReadByte() };
                    var length = FixedLength(record.Type);
                    if (length == -1)
                    {
                        record.Sized = true;
                        var sizeStart = stream.Position;
                        var size = ReadSize(reader);
                        length = checked(size - (int)(stream.Position - sizeStart));
                    }
                    else if (record.Type == 47)
                    {
                        var start = stream.Position;
                        reader.ReadInt16();
                        reader.ReadString();
                        length = checked((int)(stream.Position - start));
                        stream.Position = start;
                    }
                    if (length < 0 || length > stream.Length - stream.Position)
                        throw new InvalidDataException("Invalid BAML record length.");
                    record.Data = reader.ReadBytes(length);
                    record.End = checked((int)stream.Position);
                    records.Add(record);
                }
                return records;
            }
        }

        public static byte[] Rewrite(byte[] bytes, IDictionary<string, string> renames)
        {
            var records = Read(bytes);
            var rules = renames.Select(r => new AssemblyRewriter.StringRule(r.Key, r.Value)).ToList();
            var positions = new Dictionary<int, int>();
            var position = 28;
            foreach (var record in records)
            {
                PatchRecord(record, renames, rules);
                record.OutputStart = position;
                positions.Add(record.Start, position);
                position = checked(position + 1 + record.Data.Length + (record.Sized ? SizeLength(TotalSize(record.Data.Length)) : 0));
                record.OutputEnd = position;
            }
            positions.Add(bytes.Length, position);

            for (var i = 0; i < records.Count; i++)
            {
                var start = records[i];
                if (start.Type != 37)
                    continue;
                var end = checked(start.End + BitConverter.ToInt32(start.Data, 0));
                WriteInt(start.Data, 0, checked(positions[end] - start.OutputEnd));
                var keys = new List<Record>();
                var depth = 0;
                Record values = null;
                for (var j = i + 1; j < records.Count && records[j].Start < end; j++)
                {
                    var record = records[j];
                    if (depth == 0 && (record.Type == 38 || record.Type == 39 || record.Type == 40))
                        keys.Add(record);
                    if (record.Type == 40 || record.Type == 48)
                        depth++;
                    else if (record.Type == 41 || record.Type == 49)
                        depth--;
                    else if (depth == 0 && (record.Type == 3 || record.Type == 47))
                    {
                        values = record;
                        break;
                    }
                }
                if (keys.Count == 0)
                    continue;
                if (values == null)
                    throw new InvalidDataException("BAML dictionary values were not found.");
                foreach (var key in keys)
                {
                    var offset = key.Type == 38 ? 2 : 3;
                    var target = checked(values.Start + BitConverter.ToInt32(key.Data, offset));
                    WriteInt(key.Data, offset, checked(positions[target] - values.OutputStart));
                }
            }

            using (var output = new MemoryStream())
            using (var writer = new BinaryWriter(output, Encoding.UTF8))
            {
                writer.Write(bytes, 0, 28);
                foreach (var record in records)
                {
                    writer.Write(record.Type);
                    if (record.Sized)
                        WriteSize(writer, TotalSize(record.Data.Length));
                    writer.Write(record.Data);
                }
                return output.ToArray();
            }
        }

        static void PatchRecord(Record record, IDictionary<string, string> renames, List<AssemblyRewriter.StringRule> rules)
        {
            int offset;
            switch (record.Type)
            {
                case 5: case 18: case 28: case 32: case 36: offset = 2; break;
                case 29: case 30: offset = 4; break;
                case 15: case 16: case 17: case 20: case 25: case 27: case 52: offset = 0; break;
                default: return;
            }
            using (var input = new MemoryStream(record.Data))
            using (var reader = new BinaryReader(input, Encoding.UTF8))
            using (var output = new MemoryStream())
            using (var writer = new BinaryWriter(output, Encoding.UTF8))
            {
                writer.Write(reader.ReadBytes(offset));
                var count = record.Type == 20 || record.Type == 27 ? 2 : 1;
                for (var i = 0; i < count; i++)
                {
                    var value = reader.ReadString();
                    if (record.Type == 28)
                    {
                        var identity = new AssemblyName(value);
                        if (renames.TryGetValue(identity.Name, out var name))
                        {
                            identity.Name = name;
                            identity.SetPublicKey(null);
                            identity.SetPublicKeyToken(null);
                            value = identity.FullName;
                        }
                    }
                    else
                        value = AssemblyRewriter.PatchString(value, rules);
                    writer.Write(value);
                }
                writer.Write(reader.ReadBytes(checked((int)(input.Length - input.Position))));
                record.Data = output.ToArray();
            }
        }

        static int FixedLength(byte type)
        {
            switch (type)
            {
                case 1: return 6;
                case 2: case 4: case 8: case 10: case 12: case 14: case 26:
                case 41: case 42: case 43: case 49: return 0;
                case 3: case 48: case 55: return 3;
                case 7: case 9: case 11: case 13: case 44: case 46: case 50: return 2;
                case 33: case 34: case 37: case 45: case 54: case 56: return 4;
                case 35: return 6;
                case 39: case 40: return 9;
                case 47: return 0; // TypeId and a length-prefixed runtime name; read separately.
                case 53: return 8;
                case 5: case 6: case 15: case 16: case 17: case 18: case 20: case 25:
                case 27: case 28: case 29: case 30: case 31: case 32: case 36: case 38:
                case 51: case 52: return -1;
                default: throw new InvalidDataException("Unsupported BAML record: " + type);
            }
        }

        static void WriteInt(byte[] data, int offset, int value) => Buffer.BlockCopy(BitConverter.GetBytes(value), 0, data, offset, 4);
        static int SizeLength(int value) { var length = 1; while ((value >>= 7) != 0) length++; return length; }
        static int TotalSize(int length) { var size = length + 1; while (size != length + SizeLength(size)) size = length + SizeLength(size); return size; }
        static int ReadSize(BinaryReader reader)
        {
            var value = 0;
            for (var shift = 0; shift < 35; shift += 7)
            {
                var b = reader.ReadByte();
                if (shift == 28 && (b & 0xf8) != 0) throw new InvalidDataException("Invalid BAML size.");
                value |= (b & 127) << shift;
                if ((b & 128) == 0) return value;
            }
            throw new InvalidDataException("Invalid BAML size.");
        }
        static void WriteSize(BinaryWriter writer, int value)
        {
            while (value >= 128) { writer.Write((byte)(value | 128)); value >>= 7; }
            writer.Write((byte)value);
        }
    }
}
