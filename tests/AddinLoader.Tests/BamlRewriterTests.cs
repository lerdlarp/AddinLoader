using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Resources;
using System.Text;
using AddinLoader.Core;
using Xunit;

namespace AddinLoader.Tests
{
    public class BamlRewriterTests
    {
        static readonly Dictionary<string, string> Renames = new Dictionary<string, string>
        {
            ["Styles"] = "Styles.r123",
        };

        [Fact]
        public void Rewrites_signed_assembly_identity_and_preserves_deferred_dictionary_targets()
        {
            var original = Document();
            var records = BamlRewriter.Read(original);
            var deferred = records.Single(r => r.Type == 37);
            var values = records.Where(r => r.Type == 3).ToArray();
            PutInt(original, deferred.Start + 1, records.Last().Start - deferred.End);
            var key = records.Single(r => r.Type == 38);
            PutInt(original, key.End - key.Data.Length + 2, values[1].Start - values[0].Start);

            var rewritten = BamlRewriter.Read(BamlRewriter.Rewrite(original, Renames));
            var assembly = rewritten.Single(r => r.Type == 28);
            using (var reader = new BinaryReader(new MemoryStream(assembly.Data)))
            {
                Assert.Equal(0, reader.ReadInt16());
                var identity = new System.Reflection.AssemblyName(reader.ReadString());
                Assert.Equal("Styles.r123", identity.Name);
                Assert.True(identity.GetPublicKeyToken() == null || identity.GetPublicKeyToken().Length == 0);
            }
            deferred = rewritten.Single(r => r.Type == 37);
            Assert.Equal(rewritten.Last().Start, deferred.End + BitConverter.ToInt32(deferred.Data, 0));
            values = rewritten.Where(r => r.Type == 3).ToArray();
            key = rewritten.Single(r => r.Type == 38);
            Assert.Equal(values[1].Start, values[0].Start + BitConverter.ToInt32(key.Data, 2));
            using (var reader = new BinaryReader(new MemoryStream(rewritten.Single(r => r.Type == 5).Data)))
            {
                reader.ReadInt16();
                Assert.Equal("/Styles.r123;component/theme.xaml", reader.ReadString());
            }
        }

        [Fact]
        public void Resource_rewrite_preserves_non_baml_entries()
        {
            byte[] resources;
            using (var stream = new MemoryStream())
            using (var writer = new ResourceWriter(stream))
            {
                writer.AddResource("view.baml", new MemoryStream(Document(false)));
                writer.AddResource("icon.png", new byte[] { 1, 2, 3 });
                writer.Generate();
                resources = stream.ToArray();
            }
            using (var reader = new ResourceReader(new MemoryStream(BamlRewriter.RewriteResources(resources, Renames))))
            {
                reader.GetResourceData("icon.png", out var type, out var bytes);
                Assert.Equal("ResourceTypeCode.ByteArray", type);
                Assert.Equal(new byte[] { 3, 0, 0, 0, 1, 2, 3 }, bytes);
                reader.GetResourceData("view.baml", out type, out bytes);
                Assert.Equal(bytes.Length - 4, BitConverter.ToInt32(bytes, 0));
                Assert.Contains("Styles.r123", Encoding.UTF8.GetString(bytes));
            }
        }

        [Fact]
        public void Rejects_unknown_records_instead_of_corrupting_resources()
        {
            var document = Document(false);
            document[28] = 255;
            Assert.Throws<InvalidDataException>(() => BamlRewriter.Rewrite(document, Renames));
        }

        static byte[] Document(bool deferred = true)
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(12);
                writer.Write(Encoding.Unicode.GetBytes("MSBAML"));
                for (var i = 0; i < 3; i++) { writer.Write((ushort)0); writer.Write((ushort)96); }
                Sized(writer, 28, payload => { payload.Write((short)0); payload.Write("Styles, Version=1.0.0.0, Culture=neutral, PublicKeyToken=0123456789abcdef"); });
                if (deferred)
                {
                    writer.Write((byte)37); writer.Write(0);
                    Sized(writer, 38, payload => { payload.Write((short)0); payload.Write(0); payload.Write(false); payload.Write(false); });
                    writer.Write((byte)3); writer.Write((short)0); writer.Write((byte)0);
                    Sized(writer, 5, payload => { payload.Write((short)0); payload.Write("/Styles;component/theme.xaml"); });
                    writer.Write((byte)4);
                    writer.Write((byte)3); writer.Write((short)0); writer.Write((byte)0);
                    writer.Write((byte)4);
                }
                writer.Write((byte)2);
                return stream.ToArray();
            }
        }

        static void Sized(BinaryWriter writer, byte type, Action<BinaryWriter> payload)
        {
            using (var stream = new MemoryStream())
            using (var data = new BinaryWriter(stream))
            {
                payload(data);
                var bytes = stream.ToArray();
                Assert.True(bytes.Length + 1 < 128);
                writer.Write(type); writer.Write((byte)(bytes.Length + 1)); writer.Write(bytes);
            }
        }

        static void PutInt(byte[] bytes, int offset, int value) => Buffer.BlockCopy(BitConverter.GetBytes(value), 0, bytes, offset, 4);
    }
}
